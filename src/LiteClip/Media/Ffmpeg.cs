using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LiteClip.Core;

namespace LiteClip.Media;

public sealed record RunResult(int ExitCode, string StdErr, string StdOut)
{
    public bool Ok => ExitCode == 0;
    public string Tail(int lines = 6) => string.Join(" | ", StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
}

/// <summary>Finds (or unpacks) the bundled ffmpeg and runs it.</summary>
public static class Ffmpeg
{
    static string? _ffmpeg, _ffprobe;

    public static string Exe => _ffmpeg ?? throw new InvalidOperationException("ffmpeg not ready");
    public static string Probe => _ffprobe ?? throw new InvalidOperationException("ffprobe not ready");

    /// <summary>
    /// Uses ffmpeg.exe next to LiteClip.exe if there is one (handy when developing),
    /// otherwise unpacks the copy built into the exe to %LOCALAPPDATA%\LiteClip\bin\&lt;hash&gt;.
    /// </summary>
    public static void Prepare()
    {
        var beside = Path.GetDirectoryName(AppInfo.ExePath)!;
        if (File.Exists(Path.Combine(beside, "ffmpeg.exe")) && File.Exists(Path.Combine(beside, "ffprobe.exe")))
        {
            _ffmpeg = Path.Combine(beside, "ffmpeg.exe");
            _ffprobe = Path.Combine(beside, "ffprobe.exe");
            Log.Info($"using ffmpeg beside the exe: {_ffmpeg}");
            return;
        }

        var asm = Assembly.GetExecutingAssembly();
        using var ff = asm.GetManifestResourceStream("bin/ffmpeg.exe")
            ?? throw new FileNotFoundException("This LiteClip build has no ffmpeg inside it.");
        using var fp = asm.GetManifestResourceStream("bin/ffprobe.exe")
            ?? throw new FileNotFoundException("This LiteClip build has no ffprobe inside it.");

        // folder named after the contents, so a new LiteClip version never fights an old ffmpeg
        var tag = $"{ff.Length:x}-{fp.Length:x}-{AppInfo.Version}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(tag)))[..12].ToLowerInvariant();
        var dir = Path.Combine(Paths.Bin, hash);
        _ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        _ffprobe = Path.Combine(dir, "ffprobe.exe");
        UnpackHook(asm, Path.Combine(dir, "hook"));
        if (File.Exists(_ffmpeg) && new FileInfo(_ffmpeg).Length == ff.Length &&
            File.Exists(_ffprobe) && new FileInfo(_ffprobe).Length == fp.Length) return;

        Directory.CreateDirectory(dir);
        Unpack(ff, _ffmpeg);
        Unpack(fp, _ffprobe);
        Log.Info($"unpacked ffmpeg to {dir}");

        // tidy away older copies
        foreach (var old in Directory.GetDirectories(Paths.Bin).Where(d => !d.EndsWith(hash)))
            try { Directory.Delete(old, true); } catch { }
    }

    /// <summary>Folder with OBS's game-capture hook (graphics-hook64.dll etc.), or null if this build has none.</summary>
    public static string? HookDir { get; private set; }

    static readonly string[] HookFiles = { "graphics-hook64.dll", "inject-helper64.exe", "get-graphics-offsets64.exe" };

    static void UnpackHook(Assembly asm, string dir)
    {
        try
        {
            if (HookFiles.Any(n => asm.GetManifestResourceInfo("hook/" + n) == null)) return;
            Directory.CreateDirectory(dir);
            foreach (var n in HookFiles)
            {
                using var r = asm.GetManifestResourceStream("hook/" + n)!;
                var path = Path.Combine(dir, n);
                // a game may still have yesterday's hook loaded: leave a same-size file alone
                if (File.Exists(path) && new FileInfo(path).Length == r.Length) continue;
                Unpack(r, path);
            }
            HookDir = dir;
        }
        catch (Exception ex) { Log.Error("couldn't unpack the game capture hook", ex); }
    }

    static void Unpack(Stream s, string path)
    {
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp)) s.CopyTo(f);
        File.Move(tmp, path, true);
    }

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, bool redirectIn = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = redirectIn,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Local,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static Task<RunResult> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunExeAsync(Exe, args, timeout, ct);

    public static Task<RunResult> ProbeAsync(IEnumerable<string> args, TimeSpan? timeout = null)
        => RunExeAsync(Probe, args, timeout ?? TimeSpan.FromSeconds(20), default);

    static async Task<RunResult> RunExeAsync(string exe, IEnumerable<string> args, TimeSpan? timeout, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(exe, args) };
        var err = new StringBuilder();
        var outp = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { if (err.Length < 64_000) err.AppendLine(e.Data); } };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outp) { if (outp.Length < 1_000_000) outp.AppendLine(e.Data); } };
        p.Start();
        Native.AttachToJob(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new RunResult(-1, err + "\n(timed out or cancelled)", outp.ToString());
        }
        p.WaitForExit(); // flush async readers
        return new RunResult(p.ExitCode, err.ToString(), outp.ToString());
    }

    /// <summary>Duration in seconds via ffprobe, or 0.</summary>
    public static async Task<double> DurationAsync(string file)
    {
        var r = await ProbeAsync(new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file });
        return double.TryParse(r.StdOut.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
