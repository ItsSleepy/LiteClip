using LiteClip.Core;

namespace LiteClip.Media;

/// <summary>
/// The graphics-card encoders LiteClip can use, how to drive each one, and which ones
/// actually work on this PC. Frames stay on the GPU the whole way for the hardware paths.
/// </summary>
public static class Encoders
{
    public static readonly string[] AutoOrder = { "nvenc", "amf", "mf", "mf-sw" };

    public static string Label(string id) => id switch
    {
        "nvenc" => "NVIDIA (NVENC)",
        "amf" => "AMD (AMF)",
        "mf" => "Intel / other GPU (Media Foundation)",
        "mf-sw" => "Software (CPU, slow)",
        _ => id,
    };

    public static string FfName(string id, string codec) => id switch
    {
        "nvenc" => $"{codec}_nvenc",
        "amf" => $"{codec}_amf",
        _ => $"{codec}_mf",
    };

    /// <summary>True when frames must leave the GPU before this encoder (software path).</summary>
    public static bool NeedsSystemMemory(string id) => id == "mf-sw";

    public static List<string> VideoArgs(string id, string codec, int kbps, int fps)
    {
        int gop = fps; // a keyframe every second keeps clip cuts within a second of the key press
        var a = new List<string> { "-c:v", FfName(id, codec) };
        string b = $"{kbps}k", max = $"{kbps * 3 / 2}k", buf = $"{kbps * 2}k";
        switch (id)
        {
            case "nvenc":
                a.AddRange(new[] { "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-b:v", b, "-maxrate", max, "-bufsize", buf,
                    "-g", gop.ToString(), "-strict_gop", "1", "-spatial_aq", "1", "-multipass", "disabled" });
                if (codec == "h264") a.AddRange(new[] { "-profile:v", "high" });
                break;
            case "amf":
                a.AddRange(new[] { "-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak",
                    "-b:v", b, "-maxrate", max, "-bufsize", buf, "-g", gop.ToString() });
                break;
            case "mf":
                a.AddRange(new[] { "-hw_encoding", "1", "-rate_control", "pc_vbr", "-b:v", b, "-maxrate", max, "-g", gop.ToString() });
                break;
            default: // mf-sw
                a.AddRange(new[] { "-rate_control", "pc_vbr", "-b:v", b, "-maxrate", max, "-g", gop.ToString() });
                break;
        }
        // What the D3D11 video processor produces: BT.601 matrix, limited range. Tag it so players show true colours.
        a.AddRange(new[] { "-color_range", "tv", "-colorspace", "smpte170m", "-color_primaries", "bt709", "-color_trc", "bt709" });
        return a;
    }

    /// <summary>GPU filter tail: resize + convert to NV12 on the graphics card, then tag colours.</summary>
    public static string GpuTail(string id, int w, int h) => GpuTail(id, w.ToString(), h.ToString());

    public static string GpuTail(string id, string w, string h)
    {
        var s = $"scale_d3d11=width={w}:height={h}:format=nv12";
        if (NeedsSystemMemory(id)) s += ",hwdownload,format=nv12";
        return s + ",setparams=range=tv:colorspace=smpte170m:color_primaries=bt709:color_trc=bt709";
    }

    /// <summary>Target bitrate for a resolution, frame rate and quality preset.</summary>
    public static int Kbps(string quality, string codec, int w, int h, int fps)
    {
        double k = quality switch { "low" => 0.07, "medium" => 0.11, "max" => 0.26, _ => 0.17 };
        double fpsFactor = Math.Pow(fps / 60.0, 0.8) * 60; // more frames need fewer bits each
        double kbps = w * (double)h * fpsFactor * k / 1000;
        if (codec != "h264") kbps *= 0.7;
        return (int)Math.Clamp(kbps, 2500, 130_000);
    }

    /// <summary>Largest size the preset allows, keeping the source's shape (even numbers, as encoders need).</summary>
    public static (int w, int h) Fit(string quality, int srcW, int srcH)
    {
        int maxH = quality switch { "low" => 720, "medium" => 1080, "high" => 1440, _ => 2160 };
        int maxW = maxH * 16 / 9;
        if (srcW <= 0 || srcH <= 0) { srcW = 1920; srcH = 1080; }
        double s = Math.Min(1.0, Math.Min(maxW / (double)srcW, maxH / (double)srcH));
        // ultrawide: let width run past 16:9 but keep the height cap
        if (srcW / (double)srcH > 16.0 / 9) s = Math.Min(1.0, maxH / (double)srcH);
        int w = (int)Math.Round(srcW * s) & ~1, h = (int)Math.Round(srcH * s) & ~1;
        return (Math.Max(w, 160), Math.Max(h, 90));
    }

    static readonly Dictionary<string, bool> Cache = new();

    /// <summary>Encodes half a second of black on the GPU to see if this encoder works here.</summary>
    public static async Task<bool> WorksAsync(string id, string codec)
    {
        var key = id + "/" + codec;
        lock (Cache) if (Cache.TryGetValue(key, out var known)) return known;

        // only the encoder is under test here (GPU frames in, like the real pipeline)
        var args = new List<string> { "-hide_banner", "-loglevel", "error",
            "-init_hw_device", "d3d11va=gpu", "-filter_hw_device", "gpu",
            "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30", "-t", "0.5",
            "-vf", NeedsSystemMemory(id) ? "format=nv12" : "format=nv12,hwupload" };
        args.AddRange(VideoArgs(id, codec, 8000, 30));
        args.AddRange(new[] { "-f", "null", "-" });
        var r = await Ffmpeg.RunAsync(args, TimeSpan.FromSeconds(20));
        Log.Info($"encoder test {key}: {(r.Ok ? "works" : "no")} {(r.Ok ? "" : r.Tail(3))}");
        lock (Cache) Cache[key] = r.Ok;
        return r.Ok;
    }

    public static async Task<List<string>> AvailableAsync(string codec)
    {
        var ok = new List<string>();
        foreach (var id in AutoOrder)
        {
            if (id == "mf-sw" && codec != "h264") continue;
            if (await WorksAsync(id, codec)) ok.Add(id);
        }
        return ok;
    }

    /// <summary>Which encoders to try, best first, given the user's choice.</summary>
    public static async Task<List<string>> CandidatesAsync(Settings s)
    {
        var avail = await AvailableAsync(s.Codec);
        if (s.Encoder != "auto" && avail.Contains(s.Encoder))
            return new List<string> { s.Encoder }.Concat(avail.Where(x => x != s.Encoder)).ToList();
        return avail;
    }
}
