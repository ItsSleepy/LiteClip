using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteClip.Core;
using LiteClip.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiteClip.UI;

/// <summary>The window: a WebView2 page talking to the app over postMessage.</summary>
public sealed class MainForm : Form
{
    readonly LiteClipApp _app;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 11, 18) };
    readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 80 };
    bool _meters;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public MainForm(LiteClipApp app)
    {
        _app = app;
        Text = "LiteClip";
        Icon = LiteClipApp.AppIcon;
        BackColor = Color.FromArgb(12, 11, 18);
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_web);
        _meterTimer.Tick += (_, _) =>
        {
            var (g, m) = _app.Audio.Levels();
            Send("levels", new { game = g, mic = m });
        };
        _app.Push += OnPush;
        Load += async (_, _) => await InitWebAsync();
        FormClosed += (_, _) =>
        {
            _app.Push -= OnPush;
            SetMeters(false);
            _app.SuspendHotkeys(false);
            _meterTimer.Dispose();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
        // size in real pixels for this screen's scaling, centred, never bigger than 90% of it
        float k = DeviceDpi / 96f;
        var wa = Screen.FromHandle(Handle).WorkingArea;
        int w = Math.Min((int)(1280 * k), wa.Width * 9 / 10), h = Math.Min((int)(820 * k), wa.Height * 9 / 10);
        MinimumSize = new Size(Math.Min((int)(860 * k), w), Math.Min((int)(560 * k), h));
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
    }

    async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView,
                new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --autoplay-policy=no-user-gesture-required"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("webview2", ex);
            var r = MessageBox.Show("LiteClip needs the Microsoft Edge WebView2 Runtime for its window (it's already on most PCs).\n\nOpen the download page?",
                "LiteClip", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            Close();
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = File.Exists(Path.Combine(Paths.Local, "devtools"));
#endif
        core.SetVirtualHostNameToFolderMapping("app.liteclip", UiFolder(), CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("thumbs.liteclip", Paths.Thumbs, CoreWebView2HostResourceAccessKind.Allow);
        MapClipFolder();
        core.NewWindowRequested += (_, e) => { e.Handled = true; };
        core.WebMessageReceived += (_, e) => _ = HandleAsync(e.WebMessageAsJson);
        core.Navigate("https://app.liteclip/index.html");
    }

    public void MapClipFolder()
    {
        if (_web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.ClearVirtualHostNameToFolderMapping("clips.liteclip"); } catch { }
        Directory.CreateDirectory(_app.Settings.ClipFolder);
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping("clips.liteclip", _app.Settings.ClipFolder, CoreWebView2HostResourceAccessKind.Allow);
    }

    /// <summary>The page lives inside the exe; unpack it once per version. A wwwroot folder beside the exe wins (for development).</summary>
    static string UiFolder()
    {
        var beside = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath)!, "wwwroot");
        if (File.Exists(Path.Combine(beside, "index.html"))) return beside;
        var dir = Path.Combine(Paths.Ui, AppInfo.Version);
        var asm = Assembly.GetExecutingAssembly();
        if (!File.Exists(Path.Combine(dir, ".ok")) || AppInfo.Version.Contains("dev"))
        {
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/")))
            {
                var path = Path.Combine(dir, name[3..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var s = asm.GetManifestResourceStream(name)!;
                using var f = File.Create(path);
                s.CopyTo(f);
            }
            File.WriteAllText(Path.Combine(dir, ".ok"), AppInfo.Version);
            foreach (var old in Directory.GetDirectories(Paths.Ui).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                try { Directory.Delete(old, true); } catch { }
        }
        return dir;
    }

    void OnPush(string ev, object? data) => BeginInvokeSafe(() => Send(ev, data));

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    void Send(string ev, object? data)
    {
        if (_web.CoreWebView2 == null || IsDisposed) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = ev, data }, Json)); } catch { }
    }

    void Reply(long id, object? result, string? error)
    {
        BeginInvokeSafe(() =>
        {
            if (_web.CoreWebView2 == null) return;
            try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = error == null, result, error }, Json)); } catch { }
        });
    }

    void SetMeters(bool on)
    {
        if (on == _meters) return;
        _meters = on;
        if (on) { _app.Audio.Acquire(_app.Settings); _meterTimer.Start(); }
        else { _meterTimer.Stop(); _app.Audio.Release(); }
    }

    async Task HandleAsync(string json)
    {
        long id = 0;
        try
        {
            var msg = JsonNode.Parse(json)!.AsObject();
            id = msg["id"]?.GetValue<long>() ?? 0;
            var cmd = msg["cmd"]?.GetValue<string>() ?? "";
            var a = msg["args"] as JsonObject ?? new JsonObject();
            var result = await RunAsync(cmd, a);
            Reply(id, result, null);
        }
        catch (Exception ex)
        {
            if (ex is not InvalidOperationException) Log.Error("ui command", ex);
            Reply(id, null, ex.Message);
        }
    }

    static string Str(JsonObject a, string k) => a[k]?.GetValue<string>() ?? throw new InvalidOperationException($"missing {k}");

    async Task<object?> RunAsync(string cmd, JsonObject a)
    {
        var lib = _app.Library;
        switch (cmd)
        {
            case "init":
                return new
                {
                    version = AppInfo.Version,
                    settings = _app.Settings,
                    status = _app.StatusDto(),
                    clips = lib.List(),
                    mics = AudioEngine.Devices(true),
                    outputs = AudioEngine.Devices(false),
                    monitors = Native.Monitors().Select((m, i) => new { index = i, name = $"Monitor {i + 1}{(m.Primary ? " (main)" : "")}", width = m.Bounds.Width, height = m.Bounds.Height }),
                    encoders = (await Encoders.AvailableAsync(_app.Settings.Codec)).Select(e => new { id = e, name = Encoders.Label(e) }),
                    lastEncoder = _app.Settings.LastEncoder,
                };
            case "clips":
                return lib.List();
            case "status":
                return _app.StatusDto();
            case "saveSettings":
            {
                var s = a["settings"].Deserialize<Settings>(Json) ?? throw new InvalidOperationException("bad settings");
                var saved = _app.ApplySettings(s);
                if (_meters) _app.Audio.Configure(saved);
                return saved;
            }
            case "encoders":
            {
                var codec = a["codec"]?.GetValue<string>() ?? _app.Settings.Codec;
                return (await Encoders.AvailableAsync(codec)).Select(e => new { id = e, name = Encoders.Label(e) });
            }
            case "devices":
                return new { mics = AudioEngine.Devices(true), outputs = AudioEngine.Devices(false) };
            case "saveClip":
                return await _app.SaveClipAsync(a["long"]?.GetValue<bool>() ?? false);
            case "pause":
                _app.SetPaused(a["on"]?.GetValue<bool>() ?? true);
                return _app.StatusDto();
            case "thumb":
            {
                var f = lib.Check(Str(a, "file"));
                var p = await lib.ThumbAsync(f);
                return p == null ? null : "https://thumbs.liteclip/" + Path.GetFileName(p);
            }
            case "duration":
                return await lib.DurationAsync(lib.Check(Str(a, "file")));
            case "favourite":
                lib.SetFavourite(Str(a, "file"), a["on"]?.GetValue<bool>() ?? true);
                return lib.List();
            case "rename":
                lib.Rename(Str(a, "file"), Str(a, "name"));
                return lib.List();
            case "delete":
                lib.Delete(Str(a, "file"));
                return lib.List();
            case "reveal":
                LiteClipApp.Reveal(lib.Check(Str(a, "file")));
                return null;
            case "copy":
            {
                var f = lib.Check(Str(a, "file"));
                var files = new System.Collections.Specialized.StringCollection { f };
                Clipboard.SetFileDropList(files);
                return true;
            }
            case "trim":
            {
                var f = await lib.TrimAsync(Str(a, "file"), a["start"]!.GetValue<double>(), a["end"]!.GetValue<double>(), a["replace"]?.GetValue<bool>() ?? false);
                _ = lib.ThumbAsync(f);
                return new { file = f, clips = lib.List() };
            }
            case "discord":
            {
                var enc = _app.Settings.LastEncoder ?? (await Encoders.AvailableAsync("h264")).FirstOrDefault() ?? "mf-sw";
                var f = await lib.DiscordExportAsync(Str(a, "file"), enc);
                if (a["copy"]?.GetValue<bool>() ?? true)
                    Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { f });
                return new { file = f, size = new FileInfo(f).Length };
            }
            case "openFolder":
            {
                var which = a["which"]?.GetValue<string>() ?? "clips";
                LiteClipApp.OpenFolder(which == "logs" ? Paths.Logs : which == "discord" ? Path.Combine(_app.Settings.ClipFolder, "Discord") : _app.Settings.ClipFolder);
                return null;
            }
            case "pickFolder":
            {
                using var d = new FolderBrowserDialog { Description = "Where should LiteClip save clips?", UseDescriptionForTitle = true, SelectedPath = _app.Settings.ClipFolder, ShowNewFolderButton = true };
                return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null;
            }
            case "storage":
                return await Task.Run(() =>
                {
                    var clipDir = _app.Settings.ClipFolder;
                    long free = 0, total = 0;
                    try { var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(clipDir))!); free = di.AvailableFreeSpace; total = di.TotalSize; } catch { }
                    var discordDir = Path.Combine(clipDir, "Discord");
                    long discord = ClipLibrary.FolderSize(discordDir);
                    long clips = Directory.Exists(clipDir) ? new DirectoryInfo(clipDir).EnumerateFiles("*.mp4").Sum(f => f.Length) : 0;
                    return new { clips, discord, buffer = ClipLibrary.FolderSize(Paths.Buffer), thumbs = ClipLibrary.FolderSize(Paths.Thumbs), free, total, folder = clipDir };
                });
            case "clearThumbs":
                foreach (var f in Directory.GetFiles(Paths.Thumbs)) try { File.Delete(f); } catch { }
                return null;
            case "meters":
                SetMeters(a["on"]?.GetValue<bool>() ?? false);
                return null;
            case "suspendHotkeys":
                _app.SuspendHotkeys(a["on"]?.GetValue<bool>() ?? false);
                return null;
            case "openUrl":
            {
                var url = Str(a, "url");
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return null;
            }
            case "quit":
                _app.Quit();
                return null;
            default:
                throw new InvalidOperationException("unknown command " + cmd);
        }
    }
}
