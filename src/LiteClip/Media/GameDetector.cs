using System.Diagnostics;
using LiteClip.Core;

namespace LiteClip.Media;

public sealed record GameInfo(IntPtr Hwnd, int Pid, string Exe, string Name)
{
    public bool Alive => Native.IsWindow(Hwnd);
}

/// <summary>
/// Spots when a game is in front: known game exes, anything the user marked as a game,
/// or any window covering its whole monitor that isn't an obvious desktop app.
/// </summary>
public sealed class GameDetector : IDisposable
{
    public event Action<GameInfo?>? Changed;
    public GameInfo? Current { get; private set; }

    readonly System.Threading.Timer _timer;
    Settings _settings;
    int _busy;

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"] = "CS2", ["csgo"] = "CS:GO", ["valorant-win64-shipping"] = "Valorant",
        ["robloxplayerbeta"] = "Roblox", ["javaw"] = "Minecraft", ["minecraft.windows"] = "Minecraft",
        ["fortniteclient-win64-shipping"] = "Fortnite", ["r5apex"] = "Apex Legends", ["r5apex_dx12"] = "Apex Legends",
        ["gta5"] = "GTA V", ["gta5_enhanced"] = "GTA V", ["rocketleague"] = "Rocket League",
        ["league of legends"] = "League of Legends", ["overwatch"] = "Overwatch 2", ["dota2"] = "Dota 2",
        ["rainbowsix"] = "Rainbow Six Siege", ["rainbowsix_vulkan"] = "Rainbow Six Siege",
        ["cod"] = "Call of Duty", ["cod24-cod"] = "Call of Duty", ["blackops6"] = "Call of Duty",
        ["eldenring"] = "Elden Ring", ["helldivers2"] = "Helldivers 2", ["marvel-win64-shipping"] = "Marvel Rivals",
        ["deadlock"] = "Deadlock", ["tslgame"] = "PUBG", ["eafc25"] = "EA FC", ["eafc26"] = "EA FC",
        ["rustclient"] = "Rust", ["destiny2"] = "Destiny 2", ["thefinals"] = "The Finals",
        ["deltaforce-win64-shipping"] = "Delta Force", ["battlefield6"] = "Battlefield 6", ["bf2042"] = "Battlefield 2042",
        ["geometrydash"] = "Geometry Dash", ["terraria"] = "Terraria", ["stardew valley"] = "Stardew Valley",
        ["re4"] = "Resident Evil 4", ["cyberpunk2077"] = "Cyberpunk 2077", ["bg3"] = "Baldur's Gate 3",
        ["bg3_dx11"] = "Baldur's Gate 3", ["among us"] = "Among Us", ["phasmophobia"] = "Phasmophobia",
        ["lethal company"] = "Lethal Company", ["repo"] = "R.E.P.O.", ["peak"] = "PEAK",
        ["fallguys_client_game"] = "Fall Guys", ["warframe.x64"] = "Warframe", ["pathofexile"] = "Path of Exile",
    };

    // fullscreen but definitely not a game
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc",
        "vlc", "mpv", "mpc-hc64", "potplayermini64", "wmplayer", "video.ui", "microsoft.media.player", "netflix",
        "discord", "spotify", "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "riotclientux", "eadesktop",
        "medal", "obs64", "liteclip", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "lockapp", "textinputhost", "code", "devenv", "rider64", "idea64", "powerpnt", "winword", "excel", "acrobat",
        "photoshop", "premiere pro", "afterfx", "resolve", "blender", "teams", "ms-teams", "zoom", "slack", "notion",
        "claude", "windowsterminal", "cmd", "powershell", "taskmgr", "snippingtool", "screenclippinghost", "systemsettings",
    };

    public GameDetector(Settings s)
    {
        _settings = s;
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 1500);
    }

    public void Update(Settings s) => _settings = s;

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            // the current game keeps the recorder until its window actually goes away
            if (Current != null && !Current.Alive) Set(null);

            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || (Current != null && hwnd == Current.Hwnd)) return;
            var found = Inspect(hwnd);
            if (found != null && (Current == null || found.Pid != Current.Pid)) Set(found);
        }
        catch (Exception ex) { Log.Warn("game detector: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void Set(GameInfo? g)
    {
        Current = g;
        Log.Info(g == null ? "game closed" : $"game detected: {g.Name} ({g.Exe}, pid {g.Pid})");
        Changed?.Invoke(g);
    }

    GameInfo? Inspect(IntPtr hwnd)
    {
        if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        string exe, path = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            exe = p.ProcessName;
            try { path = p.MainModule?.FileName ?? ""; } catch { }
        }
        catch { return null; }

        var lower = exe.ToLowerInvariant();
        if (_settings.NeverGames.Contains(lower) || _settings.NeverGames.Contains(lower + ".exe")) return null;

        bool userGame = _settings.AlwaysGames.Contains(lower) || _settings.AlwaysGames.Contains(lower + ".exe");
        bool known = Known.ContainsKey(exe);
        if (!userGame && !known)
        {
            if (NotGames.Contains(exe)) return null;
            if (!CoversMonitor(hwnd)) return null;
            if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (lower == "javaw" && !Native.WindowTitle(hwnd).Contains("Minecraft", StringComparison.OrdinalIgnoreCase) && !userGame) return null;

        return new GameInfo(hwnd, (int)pid, exe, NameFor(exe, path, hwnd));
    }

    static bool CoversMonitor(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mi = Native.MonitorOf(hwnd);
        if (mi == null) return false;
        var m = mi.Value.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    static string NameFor(string exe, string path, IntPtr hwnd)
    {
        if (Known.TryGetValue(exe, out var n)) return n;
        try
        {
            if (path.Length > 0)
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                var pick = new[] { v.ProductName, v.FileDescription }.FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x) && x.Length <= 40 &&
                    !x.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase) &&
                    !x.Contains("Unity", StringComparison.OrdinalIgnoreCase) &&
                    !x.Contains("Launcher", StringComparison.OrdinalIgnoreCase));
                if (pick != null) return pick.Trim();
            }
        }
        catch { }
        var title = Native.WindowTitle(hwnd).Trim();
        if (title.Length is > 0 and <= 40) return title;
        return exe;
    }

    /// <summary>Re-check right now (after settings change).</summary>
    public void Poke() => ThreadPool.QueueUserWorkItem(_ => Tick());

    public void Dispose() => _timer.Dispose();
}
