using System.Diagnostics;
using System.Reflection;
using LiteClip.Core;
using LiteClip.Media;
using Microsoft.Win32;

namespace LiteClip.UI;

/// <summary>
/// The whole app without a window: tray icon, hotkeys, game detection and the recorder.
/// The main window is created on demand and destroyed when closed, so LiteClip idles at a
/// few MB instead of keeping a browser engine alive while you play.
/// </summary>
public sealed class LiteClipApp : ApplicationContext
{
    public Settings Settings { get; private set; }
    public readonly AudioEngine Audio = new();
    public readonly Recorder Recorder;
    public readonly ClipLibrary Library;
    readonly GameDetector _detector;
    readonly KeyboardHook _keys = new();
    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    readonly Control _invoker = new();
    MainForm? _window;
    bool _paused, _quitting;
    readonly SemaphoreSlim _saveGate = new(1, 1);
    public event Action<string, object?>? Push; // events for the window
    readonly RegisteredWaitHandle? _showWait;

    public static Icon AppIcon { get; } = LoadIcon();

    public LiteClipApp(bool showWindow, EventWaitHandle showSignal)
    {
        _invoker.CreateControl();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = Settings.Load();
        Library = new ClipLibrary(Settings.ClipFolder);
        Recorder = new Recorder(Audio);
        _detector = new GameDetector(Settings);

        _tray = new NotifyIcon { Icon = AppIcon, Text = "LiteClip", Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        Recorder.StatusChanged += st =>
        {
            OnUi(() =>
            {
                var t = st.State == RecState.Recording ? $"LiteClip · {st.Text}" : $"LiteClip · {st.Text}";
                _tray.Text = t.Length > 63 ? t[..63] : t;
                UpdateMenu();
            });
            Push?.Invoke("status", StatusDto());
        };
        Library.Changed += () => Push?.Invoke("clips", null);
        _detector.Changed += _ => Evaluate();
        _keys.Pressed += OnHotkey;
        ApplyHotkeys();

        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => OnUi(ShowWindow), null, -1, false);

        _ = StartupAsync(showWindow);
    }

    async Task StartupAsync(bool showWindow)
    {
        try
        {
            await Task.Run(Ffmpeg.Prepare);
        }
        catch (Exception ex)
        {
            Log.Error("ffmpeg", ex);
            OnUi(() => MessageBox.Show("LiteClip couldn't unpack its video tools:\n\n" + ex.Message, "LiteClip", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        if (showWindow) OnUi(ShowWindow);
        // probe encoders in the background so the first recording starts fast
        _ = Task.Run(async () => { await Encoders.AvailableAsync(Settings.Codec); Evaluate(); });
    }

    static Icon LoadIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("liteclip.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void OnUi(Action a)
    {
        if (_invoker.IsDisposed) return;
        if (_invoker.InvokeRequired) _invoker.BeginInvoke(a);
        else a();
    }

    // ------------------------------------------------------------------ tray

    ToolStripMenuItem? _miSave, _miPause, _miStatus;

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false };
        _miStatus = new ToolStripMenuItem("Not recording") { Enabled = false };
        _miSave = new ToolStripMenuItem("Save clip", null, (_, _) => _ = SaveClipAsync(false));
        _miPause = new ToolStripMenuItem("Pause recording", null, (_, _) => SetPaused(!_paused));
        m.Items.Add(new ToolStripMenuItem("Open LiteClip", null, (_, _) => ShowWindow()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(_miStatus);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(_miSave);
        m.Items.Add(_miPause);
        m.Items.Add(new ToolStripMenuItem("Open clip folder", null, (_, _) => OpenFolder(Settings.ClipFolder)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Quit LiteClip", null, (_, _) => Quit()));
        m.Opening += (_, _) => UpdateMenu();
        return m;
    }

    void UpdateMenu()
    {
        if (_miSave == null) return;
        _miSave.Text = $"Save clip ({Settings.ClipKey})";
        _miSave.Enabled = Recorder.IsRecording;
        _miPause!.Text = _paused ? "Resume recording" : "Pause recording";
        _miStatus!.Text = _paused ? "Paused" : Recorder.Status.Text;
    }

    public void ShowWindow()
    {
        if (_quitting) return;
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainForm(this);
            _window.FormClosed += (_, _) =>
            {
                _window = null;
                if (!_quitting && !Settings.TrayHintShown)
                {
                    Settings.TrayHintShown = true;
                    try { Settings.Save(); } catch { }
                    _tray.ShowBalloonTip(4000, "LiteClip is still running", $"It keeps clipping in the background. Press {Settings.ClipKey} to save a clip.", ToolTipIcon.Info);
                }
                GC.Collect();
            };
            _window.Show();
        }
        else
        {
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Show();
            _window.Activate();
        }
    }

    public void Quit()
    {
        _quitting = true;
        _window?.Close();
        _tray.Visible = false;
        ExitThread();
    }

    // ------------------------------------------------------------------ recording logic

    string _wantKey = "";
    readonly SemaphoreSlim _evalGate = new(1, 1);

    /// <summary>Decide what should be recording right now and make it so.</summary>
    public async void Evaluate(bool force = false)
    {
        await _evalGate.WaitAsync();
        try
        {
            var s = Settings;
            var game = _detector.Current;
            string want = _paused ? "off"
                : game != null ? $"game:{game.Pid}:{game.Hwnd}"
                : !s.OnlyWhileGaming ? $"screen:{s.MonitorIndex}"
                : "off";
            bool running = Recorder.IsRecording || Recorder.Status.State == RecState.Starting;
            if (!force && want == _wantKey && (running || want == "off")) return;
            _wantKey = want;
            if (want == "off") await Recorder.StopAsync();
            else await Recorder.StartAsync(s, game);
        }
        catch (Exception ex) { Log.Error("evaluate", ex); }
        finally { _evalGate.Release(); }
    }

    public void SetPaused(bool on)
    {
        _paused = on;
        Evaluate();
        UpdateMenu();
        Push?.Invoke("status", StatusDto());
    }

    public object StatusDto()
    {
        var st = Recorder.Status;
        return new
        {
            state = _paused ? "paused" : st.State.ToString().ToLowerInvariant(),
            text = _paused ? "Paused" : st.Text,
            game = st.Game,
            encoder = st.Encoder,
            width = st.Width, height = st.Height, fps = st.Fps, kbps = st.Kbps,
            since = st.Since?.ToUniversalTime().ToString("o"),
            source = st.Source,
            paused = _paused,
        };
    }

    // ------------------------------------------------------------------ hotkeys & clips

    void ApplyHotkeys() => _keys.Watch(new[] { Hotkey.Parse(Settings.ClipKey), Hotkey.Parse(Settings.LongClipKey) });

    public void SuspendHotkeys(bool on) => _keys.Suspended = on;

    void OnHotkey(Hotkey hk)
    {
        bool isLong = hk.Equals(Hotkey.Parse(Settings.LongClipKey));
        _ = SaveClipAsync(isLong);
    }

    public async Task<ClipItem?> SaveClipAsync(bool isLong)
    {
        var s = Settings;
        if (!await _saveGate.WaitAsync(0)) return null; // one at a time; ignore key mashing
        try
        {
            var game = _detector.Current;
            if (!Recorder.IsRecording)
            {
                Feedback(false, "Not recording", _paused ? "Recording is paused" : "Open a game, or turn off \"Only while gaming\"", game);
                return null;
            }
            int secs = isLong ? s.LongClipSeconds : s.ClipSeconds;
            var gameName = Recorder.Status.Game ?? "Desktop";
            var name = $"{gameName} · {DateTime.Now:d MMM HH-mm-ss}";
            var sw = Stopwatch.StartNew();
            var (path, dur) = await Recorder.SaveClipAsync(secs, s.ClipFolder, name);
            if (path == null)
            {
                Feedback(false, "Couldn't save clip", "Check the log in Settings", game);
                return null;
            }
            Library.Add(path, gameName, dur);
            Log.Info($"clip saved in {sw.ElapsedMilliseconds} ms: {Path.GetFileName(path)} ({dur:0.0}s)");
            Feedback(true, "Clip saved", $"{FormatLen(dur)} · {gameName}", game);
            _ = Library.ThumbAsync(path).ContinueWith(_ => Push?.Invoke("clips", null));
            var item = Library.List().FirstOrDefault(c => string.Equals(c.File, path, StringComparison.OrdinalIgnoreCase));
            Push?.Invoke("clipSaved", item);
            return item;
        }
        catch (Exception ex)
        {
            Log.Error("save clip", ex);
            Feedback(false, "Couldn't save clip", ex.Message, null);
            return null;
        }
        finally { _saveGate.Release(); }
    }

    static string FormatLen(double s) => s >= 60 ? $"{(int)s / 60}:{(int)s % 60:00}" : $"{s:0}s";

    void Feedback(bool ok, string title, string sub, GameInfo? near)
    {
        var s = Settings;
        if (s.SaveSound) { if (ok) Sounds.Saved(); else Sounds.Failed(); }
        if (s.SavePopup) OnUi(() => Toast.Show(title, sub, ok, near?.Hwnd ?? IntPtr.Zero));
    }

    // ------------------------------------------------------------------ settings

    public Settings ApplySettings(Settings incoming)
    {
        var old = Settings;
        var s = incoming.Normalised();
        s.TrayHintShown = old.TrayHintShown;
        s.LastEncoder = old.LastEncoder;
        bool folderChanged = !string.Equals(s.ClipFolder, old.ClipFolder, StringComparison.OrdinalIgnoreCase);
        bool restart = s.RecordingDiffers(old);
        Settings = s;
        s.Save();

        ApplyHotkeys();
        _detector.Update(s);
        Audio.Configure(s);
        if (folderChanged) { Library.SetFolder(s.ClipFolder); Push?.Invoke("clips", null); _window?.MapClipFolder(); }
        SetStartup(s.StartWithWindows);
        if (restart) { _detector.Poke(); Evaluate(force: true); }
        UpdateMenu();
        return s;
    }

    static void SetStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (on) k.SetValue("LiteClip", $"\"{AppInfo.ExePath}\" --tray");
            else if (k.GetValue("LiteClip") != null) k.DeleteValue("LiteClip");
        }
        catch (Exception ex) { Log.Warn("startup setting: " + ex.Message); }
    }

    public static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("open folder: " + ex.Message); }
    }

    public static void Reveal(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("reveal: " + ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait?.Unregister(null);
            _keys.Dispose();
            _detector.Dispose();
            Recorder.Dispose();
            Audio.Dispose();
            _tray.Dispose();
            _invoker.Dispose();
        }
        base.Dispose(disposing);
    }
}
