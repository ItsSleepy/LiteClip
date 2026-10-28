using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteClip.Core;

namespace LiteClip.Media;

public sealed class ClipMeta
{
    public string Game { get; set; } = "";
    public DateTime Created { get; set; }
    public double Duration { get; set; }
    public bool Favourite { get; set; }
}

public sealed record ClipItem(string File, string Name, string Game, DateTime Created, double Duration,
    long Size, bool Favourite, string Thumb, string Url);

/// <summary>Everything in the clip folder, plus the bits a file can't hold (favourite, game).</summary>
public sealed class ClipLibrary
{
    readonly object _gate = new();
    Dictionary<string, ClipMeta> _meta = new(StringComparer.OrdinalIgnoreCase);
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly SemaphoreSlim _thumbGate = new(2, 2);
    public string Folder { get; private set; } = "";
    public event Action? Changed;

    public ClipLibrary(string folder)
    {
        SetFolder(folder);
        try
        {
            if (File.Exists(Paths.IndexFile))
                _meta = JsonSerializer.Deserialize<Dictionary<string, ClipMeta>>(File.ReadAllText(Paths.IndexFile), Opts)
                        ?? new(StringComparer.OrdinalIgnoreCase);
            _meta = new Dictionary<string, ClipMeta>(_meta, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("clip index unreadable: " + ex.Message); }
    }

    public void SetFolder(string folder)
    {
        Folder = folder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { Log.Warn($"clip folder {folder}: {ex.Message}"); }
    }

    void SaveIndex()
    {
        lock (_gate)
        {
            try
            {
                var tmp = Paths.IndexFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_meta, Opts));
                File.Move(tmp, Paths.IndexFile, true);
            }
            catch (Exception ex) { Log.Warn("saving clip index: " + ex.Message); }
        }
    }

    static string Key(string path) => Path.GetFullPath(path);

    public List<ClipItem> List()
    {
        var items = new List<ClipItem>();
        if (!Directory.Exists(Folder)) return items;
        foreach (var f in Directory.EnumerateFiles(Folder, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fi = new FileInfo(f);
                ClipMeta? m;
                lock (_gate) _meta.TryGetValue(Key(f), out m);
                var name = Path.GetFileNameWithoutExtension(f);
                var game = m?.Game is { Length: > 0 } g ? g : GuessGame(name);
                items.Add(new ClipItem(f, name, game, m?.Created ?? fi.CreationTime, m?.Duration ?? 0, fi.Length,
                    m?.Favourite ?? false, "https://thumbs.liteclip/" + ThumbName(fi), "https://clips.liteclip/" + Uri.EscapeDataString(fi.Name)));
            }
            catch { }
        }
        return items.OrderByDescending(x => x.Created).ToList();
    }

    static string GuessGame(string name)
    {
        var i = name.IndexOf(" · ", StringComparison.Ordinal);
        return i > 0 ? name[..i] : "Other";
    }

    public static string ThumbName(FileInfo fi)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"));
        return Convert.ToHexString(h)[..16].ToLowerInvariant() + ".jpg";
    }

    public void Add(string path, string game, double duration)
    {
        lock (_gate) _meta[Key(path)] = new ClipMeta { Game = game, Created = DateTime.Now, Duration = duration };
        SaveIndex();
        Changed?.Invoke();
    }

    ClipMeta? Meta(string path) { lock (_gate) return _meta.TryGetValue(Key(path), out var m) ? m : null; }

    public string Check(string file)
    {
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("That clip isn't in your clip folder any more.");
        return full;
    }

    public void SetFavourite(string file, bool on)
    {
        file = Check(file);
        lock (_gate)
        {
            if (!_meta.TryGetValue(Key(file), out var m))
                _meta[Key(file)] = m = new ClipMeta { Game = GuessGame(Path.GetFileNameWithoutExtension(file)), Created = File.GetCreationTime(file) };
            m.Favourite = on;
        }
        SaveIndex();
    }

    public string Rename(string file, string newName)
    {
        file = Check(file);
        var clean = Paths.SafeName(newName, 120);
        var target = Path.Combine(Path.GetDirectoryName(file)!, clean + ".mp4");
        if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase)) return file;
        if (File.Exists(target)) throw new InvalidOperationException("A clip with that name already exists.");
        File.Move(file, target);
        lock (_gate)
        {
            if (_meta.Remove(Key(file), out var m)) _meta[Key(target)] = m;
        }
        SaveIndex();
        return target;
    }

    /// <summary>Sends the clip to the Recycle Bin rather than deleting it outright.</summary>
    public void Delete(string file)
    {
        file = Check(file);
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        lock (_gate) _meta.Remove(Key(file));
        SaveIndex();
    }

    // ------------------------------------------------------------ thumbnails & durations

    public async Task<string?> ThumbAsync(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return null;
        var path = Path.Combine(Paths.Thumbs, ThumbName(fi));
        if (File.Exists(path)) return path;
        await _thumbGate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            double dur = Meta(file)?.Duration ?? 0;
            if (dur <= 0) { dur = await Ffmpeg.DurationAsync(file); RememberDuration(file, dur); }
            var at = dur > 0 ? Math.Min(dur * 0.4, 4) : 0;
            var r = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "d3d11va",
                "-ss", Ffmpeg.Num(at), "-i", file, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", path },
                TimeSpan.FromSeconds(30));
            return r.Ok && File.Exists(path) ? path : null;
        }
        finally { _thumbGate.Release(); }
    }

    void RememberDuration(string file, double dur)
    {
        if (dur <= 0) return;
        lock (_gate)
        {
            if (!_meta.TryGetValue(Key(file), out var m))
                _meta[Key(file)] = m = new ClipMeta { Game = GuessGame(Path.GetFileNameWithoutExtension(file)), Created = File.GetCreationTime(file) };
            m.Duration = dur;
        }
        SaveIndex();
    }

    public async Task<double> DurationAsync(string file)
    {
        var d = Meta(file)?.Duration ?? 0;
        if (d > 0) return d;
        d = await Ffmpeg.DurationAsync(file);
        RememberDuration(file, d);
        return d;
    }

    // ------------------------------------------------------------ edits

    /// <summary>Lossless trim: a stream copy, so nothing is re-encoded.</summary>
    public async Task<string> TrimAsync(string file, double start, double end, bool replace)
    {
        file = Check(file);
        if (end - start < 0.5) throw new InvalidOperationException("That's too short to keep.");
        var dir = Path.GetDirectoryName(file)!;
        var stem = Path.GetFileNameWithoutExtension(file);
        var target = replace ? file : NextFree(dir, stem + " (trim)");
        var tmp = Path.Combine(dir, $".{Guid.NewGuid():N}.part.mp4");
        var r = await Ffmpeg.RunAsync(new[] { "-hide_banner", "-loglevel", "error", "-y",
            "-ss", Ffmpeg.Num(start), "-i", file, "-t", Ffmpeg.Num(end - start),
            "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart", "-map_metadata", "0", tmp });
        if (!r.Ok) { TryDelete(tmp); throw new InvalidOperationException("Trim failed: " + r.Tail(2)); }
        var game = Meta(file)?.Game ?? GuessGame(stem);
        var fav = Meta(file)?.Favourite ?? false;
        File.Move(tmp, target, true);
        lock (_gate) _meta[Key(target)] = new ClipMeta { Game = game, Created = replace ? (Meta(file)?.Created ?? DateTime.Now) : DateTime.Now, Duration = end - start, Favourite = fav };
        SaveIndex();
        Changed?.Invoke();
        return target;
    }

    static string NextFree(string dir, string stem)
    {
        var p = Path.Combine(dir, stem + ".mp4");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{stem} {i}.mp4");
        return p;
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    /// <summary>
    /// Re-encodes a copy that fits Discord's free upload limit (10 MB), as sharp as the size allows.
    /// </summary>
    public async Task<string> DiscordExportAsync(string file, string encoder, int limitMb = 10)
    {
        file = Check(file);
        double dur = await DurationAsync(file);
        if (dur <= 0) throw new InvalidOperationException("Couldn't read that clip.");
        double budgetKbit = limitMb * 1_000_000 * 8 / 1000.0 * 0.92;
        int audioK = 96;
        int videoK = (int)(budgetKbit / dur) - audioK;
        if (videoK < 250) throw new InvalidOperationException($"At {dur:0}s it won't fit in {limitMb} MB. Trim it to under {(int)(budgetKbit / 350)}s first.");

        var outDir = Path.Combine(Folder, "Discord");
        Directory.CreateDirectory(outDir);
        var target = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".mp4");
        for (double factor = 1.0; factor >= 0.6; factor -= 0.15)
        {
            int k = (int)(videoK * factor);
            var (w, h) = k >= 4500 ? (1920, 1080) : k >= 2200 ? (1280, 720) : k >= 1000 ? (960, 540) : (854, 480);
            var ok = await EncodeAsync(file, target, encoder, k, audioK, w, h, hw: true)
                  || await EncodeAsync(file, target, encoder, k, audioK, w, h, hw: false);
            if (!ok) throw new InvalidOperationException("Export failed. Check the log for details.");
            if (new FileInfo(target).Length <= limitMb * 1_000_000) return target;
            Log.Info($"discord export {new FileInfo(target).Length / 1e6:0.0} MB at {k}k, trying smaller");
        }
        return target;
    }

    static async Task<bool> EncodeAsync(string src, string dst, string encoder, int kbps, int audioK, int w, int h, bool hw)
    {
        // decode (on the GPU when possible), then resize + convert on the CPU: works on every driver
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (hw) args.AddRange(new[] { "-hwaccel", "d3d11va" });
        args.AddRange(new[] { "-i", src, "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", $"fps=60,scale='trunc(min({w},iw*{h}/ih)/2)*2':'trunc(min({h},ih*{w}/iw)/2)*2':out_color_matrix=bt709:out_range=tv,format=nv12" });
        args.AddRange(Encoders.VideoArgs(encoder, "h264", kbps, 60, cpuFrames: true));
        args.AddRange(new[] { "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k",
            "-c:a", "aac", "-b:a", $"{audioK}k", "-ac", "2", "-movflags", "+faststart", dst });
        var r = await Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(10));
        if (!r.Ok) Log.Warn($"discord export ({(hw ? "gpu decode" : "cpu decode")}) failed: {r.Tail(6)}");
        return r.Ok && File.Exists(dst);
    }

    // ------------------------------------------------------------ storage

    public static long FolderSize(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch { return 0; }
    }
}
