using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteClip.Core;

public sealed class Settings
{
    // ---- clips
    public string ClipFolder { get; set; } = Paths.DefaultClipFolder;
    public int ClipSeconds { get; set; } = 30;          // 15, 30, 60, 180, 300
    public int LongClipSeconds { get; set; } = 180;
    public string ClipKey { get; set; } = "F8";
    public string LongClipKey { get; set; } = "Shift+F8";
    public bool SaveSound { get; set; } = true;
    public bool SavePopup { get; set; } = true;

    // ---- recording
    public string Quality { get; set; } = "high";       // low, medium, high, max
    public int Fps { get; set; } = 60;                  // 30, 60, 120, 144
    public string Codec { get; set; } = "h264";         // h264, hevc, av1
    public string Encoder { get; set; } = "auto";       // auto, nvenc, amf, mf, mf-sw
    public string CaptureMode { get; set; } = "game";   // game (window) or monitor
    public int MonitorIndex { get; set; } = 0;
    public bool OnlyWhileGaming { get; set; } = true;
    public bool CaptureCursor { get; set; } = false;
    public List<string> AlwaysGames { get; set; } = new();
    public List<string> NeverGames { get; set; } = new();

    // ---- audio
    public bool RecordGameAudio { get; set; } = true;
    public string GameDevice { get; set; } = "";        // "" = Windows default output
    public List<string> ExtraGameDevices { get; set; } = new(); // more outputs mixed into the game track (headset Game + Chat)
    public int GameVolume { get; set; } = 100;          // 0..200 %
    public bool RecordMic { get; set; } = true;
    public string MicDevice { get; set; } = "";         // "" = Windows default mic
    public int MicVolume { get; set; } = 100;

    // ---- app
    public bool StartWithWindows { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; } = false;
    public string? LastEncoder { get; set; }            // what auto picked last time

    [JsonIgnore] public int BufferSeconds => Math.Max(ClipSeconds, LongClipSeconds) + 6;

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly object Gate = new();

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Opts);
                if (s != null) return s.Normalised();
            }
        }
        catch (Exception ex) { Log.Error("settings unreadable, using defaults", ex); }
        return new Settings();
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Paths.Roaming);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, Paths.SettingsFile, true);
        }
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Opts), Opts)!;

    public Settings Normalised()
    {
        int[] lens = { 15, 30, 60, 180, 300 };
        if (!lens.Contains(ClipSeconds)) ClipSeconds = 30;
        if (!lens.Contains(LongClipSeconds)) LongClipSeconds = 180;
        if (!new[] { 30, 60, 120, 144 }.Contains(Fps)) Fps = 60;
        if (!new[] { "low", "medium", "high", "max" }.Contains(Quality)) Quality = "high";
        if (!new[] { "h264", "hevc", "av1" }.Contains(Codec)) Codec = "h264";
        if (!new[] { "auto", "nvenc", "amf", "mf", "mf-sw" }.Contains(Encoder)) Encoder = "auto";
        if (CaptureMode != "monitor") CaptureMode = "game";
        GameVolume = Math.Clamp(GameVolume, 0, 200);
        MicVolume = Math.Clamp(MicVolume, 0, 200);
        if (string.IsNullOrWhiteSpace(ClipFolder)) ClipFolder = Paths.DefaultClipFolder;
        if (Hotkey.Parse(ClipKey) == null) ClipKey = "F8";
        if (Hotkey.Parse(LongClipKey) == null) LongClipKey = "Shift+F8";
        AlwaysGames = AlwaysGames.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        ExtraGameDevices = (ExtraGameDevices ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        NeverGames = NeverGames.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        return this;
    }

    /// <summary>True when a change means the recorder has to restart to pick it up.</summary>
    public bool RecordingDiffers(Settings o) =>
        Quality != o.Quality || Fps != o.Fps || Codec != o.Codec || Encoder != o.Encoder ||
        CaptureMode != o.CaptureMode || MonitorIndex != o.MonitorIndex || OnlyWhileGaming != o.OnlyWhileGaming ||
        CaptureCursor != o.CaptureCursor || BufferSeconds != o.BufferSeconds ||
        RecordGameAudio != o.RecordGameAudio || GameDevice != o.GameDevice ||
        !ExtraGameDevices.SequenceEqual(o.ExtraGameDevices) ||
        RecordMic != o.RecordMic || MicDevice != o.MicDevice ||
        !AlwaysGames.SequenceEqual(o.AlwaysGames) || !NeverGames.SequenceEqual(o.NeverGames);
}
