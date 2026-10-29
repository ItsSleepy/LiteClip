using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using LiteClip.Core;

namespace LiteClip.Media;

/// <summary>
/// Game capture the way OBS and Medal do it: OBS's graphics-hook DLL is loaded into the game and
/// copies each finished frame into a shared GPU texture, which ffmpeg then reads at a steady rate.
/// Unlike Windows Graphics Capture this doesn't lose frames when the game keeps the GPU at 100%
/// or runs in exclusive fullscreen.
///
/// Talks the hook's own protocol (obs-studio shared/obs-hook-config/graphics-hook-info.h, ABI 648 bytes).
/// Some anti-cheats block it (CS2 needs -allow_third_party_software); callers fall back to
/// Windows Graphics Capture when <see cref="TryStartAsync"/> returns null.
/// </summary>
public sealed class GameHook : IDisposable
{
    public int Pid { get; }
    public ulong TextureHandle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    IntPtr _keepalive, _ready, _stop, _exit;

    GameHook(int pid) { Pid = pid; }

    /// <summary>True when the hook has a new texture (game resized, re-created its swap chain) or went away.</summary>
    public bool Changed => Signalled(_ready) || Signalled(_exit);

    static GraphicsOffsets? _offsets;

    public static async Task<(GameHook? hook, string why)> TryStartAsync(GameInfo game, int fps, CancellationToken ct = default)
    {
        var dir = Ffmpeg.HookDir;
        if (dir == null) return (null, "not in this build");
        if (!Is64Bit(game.Pid, out var bitsWhy)) return (null, bitsWhy);

        var hook = new GameHook(game.Pid);
        try
        {
            var why = await hook.StartAsync(game, fps, dir, ct);
            if (why == null) return (hook, "ok");
            hook.Dispose();
            return (null, why);
        }
        catch (Exception ex)
        {
            hook.Dispose();
            return (null, ex.Message);
        }
    }

    async Task<string?> StartAsync(GameInfo game, int fps, string dir, CancellationToken ct)
    {
        int pid = game.Pid;
        _offsets ??= await GraphicsOffsets.LoadAsync(Path.Combine(dir, "get-graphics-offsets64.exe"));
        if (_offsets == null) return "couldn't read graphics offsets";

        // while this mutex exists the hook keeps capturing; closing it stops it within ~5 s
        _keepalive = CreateMutexW(IntPtr.Zero, false, "CaptureHook_KeepAlive" + pid);
        if (_keepalive == IntPtr.Zero) return "keepalive mutex failed " + Marshal.GetLastWin32Error();

        bool existing = false;
        var restart = OpenEventW(EventFlags, false, "CaptureHook_Restart" + pid);
        if (restart != IntPtr.Zero)
        {
            // already hooked (by an earlier LiteClip session, or OBS). An idle hook restarts by itself
            // as soon as our keepalive exists; a busy one is told to stop below if it doesn't re-signal.
            existing = true;
            SetEvent(restart);
            CloseHandle(restart);
        }
        else
        {
            uint thread = GetWindowThreadProcessId(game.Hwnd, out _);
            if (thread == 0) return "game window has no thread";
            // "1" = inject through a window hook on the game's UI thread (what OBS calls anti-cheat compatibility)
            var psi = new ProcessStartInfo(Path.Combine(dir, "inject-helper64.exe"),
                $"\"{Path.Combine(dir, "graphics-hook64.dll")}\" 1 {thread}")
            { UseShellExecute = false, CreateNoWindow = true };
            using var inj = Process.Start(psi)!;
            if (!await WaitExitAsync(inj, 8000, ct)) { try { inj.Kill(); } catch { } return "inject helper timed out"; }
            if (inj.ExitCode != 0) return $"inject helper failed ({inj.ExitCode})";
        }

        // the DLL creates its objects as it loads; wait for them
        IntPtr info = IntPtr.Zero;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            ct.ThrowIfCancellationRequested();
            var m = OpenMutexW(0x00100000 /*SYNCHRONIZE*/, false, "CaptureHook_TextureMutex1" + pid);
            if (m != IntPtr.Zero)
            {
                CloseHandle(m);
                info = OpenFileMappingW(0x0002 | 0x0004 /*WRITE|READ*/, false, "CaptureHook_HookInfo" + pid);
                if (info != IntPtr.Zero) break;
            }
            await Task.Delay(50, ct);
        }
        if (info == IntPtr.Zero) return "hook didn't load (the game or its anti-cheat blocked it)";

        IntPtr view = MapViewOfFile(info, 0x0002 | 0x0004, 0, 0, (UIntPtr)HookInfoSize);
        if (view == IntPtr.Zero) { CloseHandle(info); return "couldn't map hook info"; }
        try
        {
            // copy each frame at twice our rate, so the texture we sample is never more than half a frame old
            ulong interval = (ulong)(1_000_000_000.0 / Math.Max(fps, 1) / 2);
            Marshal.WriteInt64(view, 56, (long)interval);
            Marshal.WriteByte(view, 64, 0); // use_scale
            Marshal.WriteByte(view, 65, 0); // force_shmem
            Marshal.WriteByte(view, 66, 0); // capture_overlay
            Marshal.WriteByte(view, 67, 1); // allow_srgb_alias
            _offsets.WriteTo(view, 68);

            _ready = OpenEventW(EventFlags, false, "CaptureHook_HookReady" + pid);
            _stop = OpenEventW(EventFlags, false, "CaptureHook_Stop" + pid);
            _exit = OpenEventW(EventFlags, false, "CaptureHook_Exit" + pid);
            var init = OpenEventW(EventFlags, false, "CaptureHook_Initialize" + pid);
            if (_ready == IntPtr.Zero || init == IntPtr.Zero) { if (init != IntPtr.Zero) CloseHandle(init); return "hook events missing"; }
            SetEvent(init);
            CloseHandle(init);

            // the hook is "ready" once the game presents a frame through a hooked API
            sw.Restart();
            while (!Signalled(_ready))
            {
                if (sw.ElapsedMilliseconds > 6000) return "game never presented a hooked frame (Vulkan, or anti-cheat)";
                if (existing && sw.ElapsedMilliseconds > 1500 && _stop != IntPtr.Zero)
                {
                    existing = false;
                    SetEvent(_stop); // still serving an old session: make it free and re-create its texture
                }
                await Task.Delay(30, ct);
            }

            return ReadTexture(view);
        }
        finally
        {
            UnmapViewOfFile(view);
            CloseHandle(info);
        }
    }

    /// <summary>After <see cref="Changed"/> fired: picks up the hook's new texture. Null = ok, else why it failed.</summary>
    public string? Refresh()
    {
        var info = OpenFileMappingW(0x0004, false, "CaptureHook_HookInfo" + Pid);
        if (info == IntPtr.Zero) return "hook info gone (game closed?)";
        IntPtr view = MapViewOfFile(info, 0x0004, 0, 0, (UIntPtr)HookInfoSize);
        try { return view == IntPtr.Zero ? "couldn't map hook info" : ReadTexture(view); }
        finally { if (view != IntPtr.Zero) UnmapViewOfFile(view); CloseHandle(info); }
    }

    string? ReadTexture(IntPtr view)
    {
        int type = Marshal.ReadInt32(view, 8);
        uint window = (uint)Marshal.ReadInt32(view, 12);
        Width = Marshal.ReadInt32(view, 20);
        Height = Marshal.ReadInt32(view, 24);
        uint mapId = (uint)Marshal.ReadInt32(view, 40);
        if (type != 1) return "hook fell back to CPU copies (game GPU differs from LiteClip's)";

        var tex = OpenFileMappingW(0x0004, false, $"CaptureHook_Texture_{window}_{mapId}");
        if (tex == IntPtr.Zero) return "shared texture info missing";
        try
        {
            IntPtr tv = MapViewOfFile(tex, 0x0004, 0, 0, UIntPtr.Zero);
            if (tv == IntPtr.Zero) return "couldn't map shared texture info";
            TextureHandle = (uint)Marshal.ReadInt32(tv, 0);
            UnmapViewOfFile(tv);
        }
        finally { CloseHandle(tex); }
        if (TextureHandle == 0 || Width < 16 || Height < 16) return "hook gave no texture";
        return null;
    }

    public void Dispose()
    {
        foreach (var h in new[] { _ready, _exit, _stop })
            if (h != IntPtr.Zero) CloseHandle(h);
        _ready = _exit = _stop = IntPtr.Zero;
        // dropping the keepalive mutex tells the hook to stop copying frames
        if (_keepalive != IntPtr.Zero) { CloseHandle(_keepalive); _keepalive = IntPtr.Zero; }
    }

    static bool Signalled(IntPtr h) => h != IntPtr.Zero && WaitForSingleObject(h, 0) == 0;

    static async Task<bool> WaitExitAsync(Process p, int ms, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ms);
        try { await p.WaitForExitAsync(cts.Token); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    static bool Is64Bit(int pid, out string why)
    {
        why = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            if (IsWow64Process(p.Handle, out var wow) && wow) { why = "32-bit game"; return false; }
            return true;
        }
        catch (Exception ex) { why = "can't open the game process: " + ex.Message; return false; }
    }

    const int HookInfoSize = 648;
    const uint EventFlags = 0x0002 | 0x00100000; // EVENT_MODIFY_STATE | SYNCHRONIZE

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateMutexW(IntPtr attr, bool owner, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint hi, uint lo, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool IsWow64Process(IntPtr proc, out bool wow);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

/// <summary>Function offsets the hook patches, found by OBS's get-graphics-offsets helper (run once per launch).</summary>
sealed class GraphicsOffsets
{
    readonly Dictionary<string, uint> _v = new();

    uint Get(string section, string key) => _v.GetValueOrDefault(section + "." + key);

    public static async Task<GraphicsOffsets?> LoadAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var text = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(10000);
            await p.WaitForExitAsync(cts.Token);
            var o = new GraphicsOffsets();
            string section = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var val = line[(eq + 1)..].Trim();
                if (val.StartsWith("0x")) val = val[2..];
                if (uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out var n))
                    o._v[section + "." + line[..eq].Trim()] = n;
            }
            if (o.Get("dxgi", "present") == 0) { Log.Info("graphics offsets: " + text.Replace('\n', ' ')); return null; }
            return o;
        }
        catch (Exception ex) { Log.Error("graphics offsets failed", ex); return null; }
    }

    /// <summary>Writes struct graphics_offsets (all uint32, pack 8) at <paramref name="at"/>.</summary>
    public void WriteTo(IntPtr view, int at)
    {
        uint[] values =
        {
            Get("d3d8", "present"),
            Get("d3d9", "present"), Get("d3d9", "present_ex"), Get("d3d9", "present_swap"),
            Get("d3d9", "d3d9_clsoff"), Get("d3d9", "is_d3d9ex_clsoff"),
            Get("dxgi", "present"), Get("dxgi", "resize"), Get("dxgi", "present1"),
            0, 0, 0, 0, 0, 0, 0, 0, // ddraw (unused)
            Get("dxgi", "release"),
            0, // d3d12 execute_command_lists (hook finds it itself)
        };
        for (int i = 0; i < values.Length; i++) Marshal.WriteInt32(view, at + i * 4, (int)values[i]);
    }
}
