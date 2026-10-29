using System.Diagnostics;
using System.Globalization;
using LiteClip.Core;

namespace LiteClip.Media;

public enum RecState { Idle, Starting, Recording, Error }

public sealed record RecStatus(
    RecState State, string Text, string? Game, string? Encoder, int Width, int Height, int Fps, int Kbps,
    DateTime? Since, string Source);

/// <summary>
/// Runs ffmpeg as the rolling buffer: GPU capture -> GPU encoder -> 1-second MPEG-TS segments
/// that wrap around, plus game and mic audio fed in over pipes. Saving a clip is a stream copy
/// of the last N seconds of segments into an mp4, so it takes about a second at full quality.
/// </summary>
public sealed class Recorder : IDisposable
{
    public event Action<RecStatus>? StatusChanged;
    public RecStatus Status { get; private set; } = new(RecState.Idle, "Not recording", null, null, 0, 0, 0, 0, null, "");

    readonly AudioEngine _audio;
    readonly SemaphoreSlim _gate = new(1, 1);
    Session? _s;
    int _failures;
    CancellationTokenSource? _restartCts;

    public Recorder(AudioEngine audio) { _audio = audio; }

    public bool IsRecording => _s is { Alive: true };

    sealed class Session
    {
        public required string Dir;
        public required Process Proc;
        public required List<PipeFeed> Feeds;
        public required string EncoderId;
        public required Settings Settings;
        public required GameInfo? Game;
        public required int Wrap;
        public DateTime Started = DateTime.Now;
        public readonly LinkedList<string> Errors = new();
        public volatile bool Stopping;
        public bool Alive => !Proc.HasExited;
        public string Csv => Path.Combine(Dir, "list.csv");
    }

    void SetStatus(RecStatus st) { Status = st; try { StatusChanged?.Invoke(st); } catch { } }

    /// <summary>Start recording what the settings ask for (game window, or a monitor).</summary>
    public async Task StartAsync(Settings s, GameInfo? game)
    {
        await _gate.WaitAsync();
        try
        {
            _restartCts?.Cancel();
            await StopCoreAsync();
            await StartCoreAsync(s, game);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _restartCts?.Cancel();
            await StopCoreAsync();
            SetStatus(Status with { State = RecState.Idle, Text = "Not recording", Since = null, Game = null });
        }
        finally { _gate.Release(); }
    }

    async Task StartCoreAsync(Settings s, GameInfo? game)
    {
        // ---- what to capture
        string src, sourceLabel;
        int srcW, srcH;
        if (s.CaptureMode == "game" && game != null && game.Alive)
        {
            Native.GetClientRect(game.Hwnd, out var cr);
            srcW = cr.Width; srcH = cr.Height;
            if (srcW < 64 || srcH < 64) { Native.GetWindowRect(game.Hwnd, out var wr); srcW = wr.Width; srcH = wr.Height; }
            src = $"gfxcapture=hwnd={game.Hwnd.ToInt64()}";
            sourceLabel = "Game window";
        }
        else
        {
            var mons = Native.Monitors();
            if (mons.Count == 0) { SetStatus(Status with { State = RecState.Error, Text = "No monitor found" }); return; }
            var mon = game != null ? mons.FirstOrDefault(m => m.Handle == Native.MonitorHandleOf(game.Hwnd)) ?? mons[0]
                                   : mons[Math.Clamp(s.MonitorIndex, 0, mons.Count - 1)];
            srcW = mon.Bounds.Width; srcH = mon.Bounds.Height;
            src = $"gfxcapture=hmonitor={mon.Handle.ToInt64()}";
            sourceLabel = $"Monitor {mons.IndexOf(mon) + 1}";
        }
        var (w, h) = Encoders.Fit(s.Quality, srcW, srcH);
        int fps = s.Fps;

        SetStatus(new RecStatus(RecState.Starting, "Starting…", game?.Name, null, w, h, fps, 0, null, sourceLabel));

        var candidates = await Encoders.CandidatesAsync(s);
        if (candidates.Count == 0)
        {
            SetStatus(Status with { State = RecState.Error, Text = "No working video encoder found. Update your graphics driver." });
            return;
        }

        _audio.Acquire(s);
        try
        {
            string lastErr = "";
            foreach (var enc in candidates)
            {
                int kbps = Encoders.Kbps(s.Quality, s.Codec, w, h, fps);
                var session = await LaunchAsync(s, game, enc, src, w, h, fps, kbps);
                if (session != null)
                {
                    _s = session;
                    _failures = 0;
                    if (s.Encoder == "auto" && s.LastEncoder != enc) { s.LastEncoder = enc; try { s.Save(); } catch { } }
                    SetStatus(new RecStatus(RecState.Recording, game != null ? $"Recording {game.Name}" : "Recording screen",
                        game?.Name, Encoders.Label(enc), w, h, fps, kbps, DateTime.Now, sourceLabel));
                    Log.Info($"recording {sourceLabel} {w}x{h}@{fps} {kbps}k via {enc}/{s.Codec}, gpu priority {_gpuPriority}");
                    return;
                }
                lastErr = _lastLaunchError;
            }
            _audio.Release();
            SetStatus(Status with { State = RecState.Error, Text = "Couldn't start recording. " + Friendly(lastErr) });
        }
        catch
        {
            _audio.Release();
            throw;
        }
    }

    string _lastLaunchError = "";
    string _gpuPriority = "normal";

    static string Friendly(string err)
    {
        if (err.Contains("gfxcapture", StringComparison.OrdinalIgnoreCase) || err.Contains("GraphicsCapture", StringComparison.OrdinalIgnoreCase))
            return "Windows wouldn't let LiteClip capture that window.";
        if (err.Length > 160) err = err[^160..];
        return err;
    }

    async Task<Session?> LaunchAsync(Settings s, GameInfo? game, string enc, string src, int w, int h, int fps, int kbps)
    {
        var id = $"{Environment.ProcessId:x}-{Environment.TickCount64:x}";
        var dir = Path.Combine(Paths.Buffer, id);
        Directory.CreateDirectory(dir);
        CleanOldBuffers(dir);

        long t0 = Native.Qpc100ns();
        var game0 = new PipeFeed($"liteclip-{id}-game", _audio.GameSources, t0);
        var mic0 = new PipeFeed($"liteclip-{id}-mic", new[] { _audio.Mic }, t0);
        var feeds = new List<PipeFeed> { game0, mic0 };
        feeds.ForEach(f => f.Start());

        int wrap = s.BufferSeconds + 10; // one-second segments
        var filter =
            $"{src}:max_framerate={fps}:capture_cursor={(s.CaptureCursor ? 1 : 0)}:width={w}:height={h}:resize_mode=scale_aspect," +
            $"setpts=PTS-{t0}" + Encoders.CaptureTail(enc) + "[v];" +
            "[0:a]asettb=1/48000,asetpts=N,asplit=2[g1][g2];" +
            "[1:a]asettb=1/48000,asetpts=N,asplit=2[m1][m2];" +
            "[g1][m1]amix=inputs=2:normalize=0:dropout_transition=0[mix]";

        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-progress", "pipe:1", "-stats_period", "15",
            "-thread_queue_size", "1024", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", game0.FfmpegPath,
            "-thread_queue_size", "1024", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", mic0.FfmpegPath,
            "-filter_complex", filter,
            "-map", "[v]", "-map", "[mix]", "-map", "[g2]", "-map", "[m2]",
            "-fps_mode:v", "cfr", "-r:v", fps.ToString() }; // cfr here (not the fps filter) so ffmpeg reports how many frames it had to repeat
        args.AddRange(Encoders.VideoArgs(enc, s.Codec, kbps, fps));
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k",
            "-metadata:s:a:0", "title=Game + Mic", "-metadata:s:a:1", "title=Game", "-metadata:s:a:2", "title=Mic",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
            "-f", "segment", "-segment_time", "1", "-segment_format", "mpegts",
            "-segment_wrap", wrap.ToString(), "-segment_list", Path.Combine(dir, "list.csv"),
            "-segment_list_type", "csv", "-segment_list_size", wrap.ToString(), "-reset_timestamps", "0",
            Path.Combine(dir, "seg%05d.ts") });

        var p = new Process { StartInfo = Ffmpeg.StartInfo(Ffmpeg.Exe, args, redirectIn: true), EnableRaisingEvents = true };
        var session = new Session { Dir = dir, Proc = p, Feeds = feeds, EncoderId = enc, Settings = s.Clone(), Game = game, Wrap = wrap };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (session.Errors)
            {
                session.Errors.AddLast(e.Data);
                if (session.Errors.Count > 40) session.Errors.RemoveFirst();
            }
        };
        // once a minute ffmpeg reports how it's keeping up (speed below 1x = it's falling behind)
        var prog = new Dictionary<string, string>();
        long lastAt = 0, lastFrames = 0, lastDup = 0;
        p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            int eq = e.Data.IndexOf('=');
            if (eq <= 0) return;
            string k = e.Data[..eq], v = e.Data[(eq + 1)..].Trim();
            if (k != "progress") { prog[k] = v; return; }
            // "new frames/s" = frames Windows actually handed us; the rest are repeats to keep a steady rate
            long.TryParse(prog.GetValueOrDefault("frame"), out var fr);
            long.TryParse(prog.GetValueOrDefault("dup_frames"), out var dup);
            var now = Environment.TickCount64;
            string fresh = "?";
            if (lastAt > 0 && now > lastAt) fresh = (((fr - lastFrames) - (dup - lastDup)) * 1000.0 / (now - lastAt)).ToString("0");
            lastAt = now; lastFrames = fr; lastDup = dup;
            Log.Info($"recorder: new frames/s={fresh} fps={prog.GetValueOrDefault("fps")} speed={prog.GetValueOrDefault("speed")} frames={fr} repeated={dup} dropped={prog.GetValueOrDefault("drop_frames")}");
        };
        try
        {
            p.Start();
            Native.AttachToJob(p);
            // capture must keep up even when a game pins the CPU and GPU at 100%
            try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
            _gpuPriority = Native.RaiseGpuPriority(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            Log.Error("couldn't start ffmpeg", ex);
            feeds.ForEach(f => f.Dispose());
            _lastLaunchError = ex.Message;
            return null;
        }

        // healthy = the first segment shows up while ffmpeg is still running
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(9))
        {
            if (p.HasExited) break;
            if (Directory.EnumerateFiles(dir, "seg*.ts").Any(f => new FileInfo(f).Length > 0)) break;
            await Task.Delay(150);
        }
        if (p.HasExited || !Directory.EnumerateFiles(dir, "seg*.ts").Any())
        {
            string err;
            lock (session.Errors) err = string.Join(" | ", session.Errors.TakeLast(4));
            string all;
            lock (session.Errors) all = string.Join("\n    ", session.Errors);
            Log.Warn($"encoder {enc} didn't start. ffmpeg said:\n    {all}\n  args: {string.Join(' ', args)}");
            _lastLaunchError = err;
            try { if (!p.HasExited) p.Kill(true); } catch { }
            feeds.ForEach(f => f.Dispose());
            try { Directory.Delete(dir, true); } catch { }
            return null;
        }

        p.Exited += (_, _) => OnExited(session);
        if (p.HasExited) { OnExited(session); }
        return session;
    }

    void OnExited(Session session)
    {
        if (session.Stopping || _s != session) return;
        string err;
        lock (session.Errors) err = string.Join(" | ", session.Errors.TakeLast(4));
        var ran = DateTime.Now - session.Started;
        Log.Warn($"ffmpeg exited (code {SafeExit(session.Proc)}) after {ran.TotalSeconds:0}s: {err}");
        session.Feeds.ForEach(f => f.Dispose());
        _audio.Release();
        _s = null;

        // the game window closing ends capture on purpose; anything else is a crash: retry
        bool gameGone = session.Game != null && !session.Game.Alive;
        if (gameGone)
        {
            SetStatus(Status with { State = RecState.Idle, Text = "Game closed", Since = null });
            return;
        }
        _failures = ran > TimeSpan.FromMinutes(1) ? 1 : _failures + 1;
        if (_failures > 4)
        {
            SetStatus(Status with { State = RecState.Error, Text = "Recording keeps stopping. " + Friendly(err), Since = null });
            return;
        }
        SetStatus(Status with { State = RecState.Starting, Text = "Recording stopped, restarting…" });
        _restartCts = new CancellationTokenSource();
        var ct = _restartCts.Token;
        var s = session.Settings;
        var g = session.Game;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000, ct);
            if (!ct.IsCancellationRequested) await StartAsync(s, g);
        }, ct);
    }

    static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }

    async Task StopCoreAsync()
    {
        var s = _s;
        if (s == null) return;
        s.Stopping = true;
        _s = null;
        try
        {
            if (!s.Proc.HasExited)
            {
                try { await s.Proc.StandardInput.WriteAsync("q"); await s.Proc.StandardInput.FlushAsync(); } catch { }
                var done = s.Proc.WaitForExitAsync();
                if (await Task.WhenAny(done, Task.Delay(3000)) != done) { try { s.Proc.Kill(true); } catch { } }
            }
        }
        catch { }
        s.Feeds.ForEach(f => f.Dispose());
        _audio.Release();
        var dir = s.Dir;
        _ = Task.Run(async () => { await Task.Delay(1000); try { Directory.Delete(dir, true); } catch { } });
    }

    static void CleanOldBuffers(string keep)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Paths.Buffer))
                if (!string.Equals(d, keep, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    // ------------------------------------------------------------------ clips

    sealed record Seg(string File, double Start, double End);

    /// <summary>
    /// Saves the last <paramref name="seconds"/> seconds. Includes the segment still being
    /// written, so the clip ends at the key press, not up to a second before it.
    /// </summary>
    public async Task<(string? path, double seconds)> SaveClipAsync(int seconds, string folder, string baseName)
    {
        var s = _s;
        if (s == null || !s.Alive) return (null, 0);

        List<Seg> segs = new();
        DateTime csvTime = DateTime.Now;
        for (int attempt = 0; attempt < 5 && segs.Count == 0; attempt++)
        {
            try
            {
                csvTime = File.GetLastWriteTime(s.Csv);
                using var fs = new FileStream(s.Csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var rd = new StreamReader(fs);
                string? line;
                while ((line = await rd.ReadLineAsync()) != null)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 3) continue;
                    if (double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                        double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                        segs.Add(new Seg(Path.Combine(s.Dir, string.Join(',', parts[..^2]).Trim('"')), a, b));
                }
            }
            catch (IOException) { await Task.Delay(60); }
        }

        // the segment in progress (not in the list yet)
        string? partial = null;
        double end;
        if (segs.Count > 0)
        {
            var last = segs[^1];
            var idx = int.Parse(Path.GetFileNameWithoutExtension(last.File)[3..]);
            var next = Path.Combine(s.Dir, $"seg{(idx + 1) % s.Wrap:00000}.ts");
            double age = Math.Clamp((DateTime.Now - csvTime).TotalSeconds, 0, 3);
            if (File.Exists(next) && File.GetLastWriteTime(next) >= csvTime.AddSeconds(-0.5)) partial = next;
            end = last.End + (partial != null ? age : 0);
        }
        else
        {
            // very first segment still being written
            var first = Path.Combine(s.Dir, "seg00000.ts");
            if (!File.Exists(first)) return (null, 0);
            partial = first;
            end = (DateTime.Now - s.Started).TotalSeconds;
        }

        // keep only segments that still exist and overlap the window we want
        double from = end - seconds;
        var use = segs.Where(x => x.End > from && File.Exists(x.File)).ToList();
        var files = use.Select(x => x.File).ToList();
        if (partial != null) files.Add(partial);
        if (files.Count == 0) return (null, 0);
        double firstStart = use.Count > 0 ? use[0].Start : (segs.Count > 0 ? segs[^1].End : 0);
        // No seeking: every segment starts on a keyframe (one per second), so starting the clip at a
        // segment boundary makes video and audio begin together. A seek into the middle of a segment
        // lands on whatever keyframe comes next for video but keeps audio from the seek point,
        // which left a gap of up to a second of sound with no picture.

        Directory.CreateDirectory(folder);
        var target = Unique(Path.Combine(folder, Paths.SafeName(baseName) + ".mp4"));
        var tmp = target + ".part";
        // the segment being written ends mid-packet: stop a little before its end so the last frame is whole
        double stopAt = end - firstStart - (partial != null ? 0.3 : 0);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (partial != null && stopAt > 1) args.AddRange(new[] { "-to", Ffmpeg.Num(stopAt) });
        args.AddRange(new[] { "-i", "concat:" + string.Join('|', files), "-map", "0:v:0", "-map", "0:a?", "-c", "copy" });
        args.AddRange(ColourFix(s.EncoderId, s.Settings.Codec));
        args.AddRange(new[] { "-movflags", "+faststart", "-metadata", "title=" + baseName,
            "-metadata", "comment=Saved with LiteClip", "-f", "mp4", tmp });
        var r = await Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(3));
        if (!r.Ok || !File.Exists(tmp) || new FileInfo(tmp).Length < 1000)
        {
            Log.Error($"clip save failed: {r.Tail()}");
            try { File.Delete(tmp); } catch { }
            return (null, 0);
        }
        File.Move(tmp, target);
        return (target, Math.Max(0, partial != null ? stopAt : end - firstStart));
    }

    /// <summary>
    /// Writes the colour description into the stream itself (some encoders, like Media Foundation,
    /// leave it out), so every player shows the same colours. Costs nothing: it's a header edit during the copy.
    /// </summary>
    static string[] ColourFix(string encoder, string codec)
    {
        int matrix = Encoders.GpuDirect(encoder) ? 6 : 1; // 6 = BT.601, 1 = BT.709
        var p = $"video_full_range_flag=0:colour_primaries=1:transfer_characteristics=1:matrix_coefficients={matrix}";
        return codec switch
        {
            "h264" => new[] { "-bsf:v", "h264_metadata=" + p },
            "hevc" => new[] { "-bsf:v", "hevc_metadata=" + p },
            _ => Array.Empty<string>(),
        };
    }

    static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}).mp4");
            if (!File.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        _restartCts?.Cancel();
        StopCoreAsync().Wait(4000);
    }
}
