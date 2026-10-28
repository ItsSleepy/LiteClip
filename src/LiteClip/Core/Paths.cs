using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteClip.Core;

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
}

public static class Paths
{
    /// <summary>%APPDATA%\LiteClip: settings and the clip index (small, worth roaming).</summary>
    public static string Roaming { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteClip");

    /// <summary>%LOCALAPPDATA%\LiteClip: tools, rolling buffer, thumbnails, logs.</summary>
    public static string Local { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteClip");

    public static string Logs => Path.Combine(Local, "logs");
    public static string Bin => Path.Combine(Local, "bin");
    public static string Buffer => Path.Combine(Local, "buffer");
    public static string Thumbs => Path.Combine(Local, "thumbs");
    public static string Ui => Path.Combine(Local, "ui");
    public static string WebView => Path.Combine(Local, "webview");
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string IndexFile => Path.Combine(Roaming, "clips.json");

    public static string DefaultClipFolder
    {
        get
        {
            var downloads = KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Path.Combine(downloads, "LiteClip");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
            var s = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return s;
        }
        catch { return null; }
    }

    public static void EnsureAll()
    {
        foreach (var d in new[] { Roaming, Local, Logs, Bin, Buffer, Thumbs, Ui, WebView })
            Directory.CreateDirectory(d);
    }

    /// <summary>Strips characters Windows won't allow in a file name.</summary>
    public static string SafeName(string s, int max = 80)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => bad.Contains(c) ? ' ' : c).ToArray();
        var clean = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (clean.Length > max) clean = clean[..max].Trim();
        return clean.Length == 0 ? "Clip" : clean;
    }
}
