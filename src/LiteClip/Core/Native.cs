using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteClip.Core;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string WindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static MONITORINFOEX? MonitorOf(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(mon, ref mi) ? mi : null;
    }

    public sealed record Monitor(IntPtr Handle, RECT Bounds, string Device, bool Primary);

    delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

    /// <summary>Monitors with the primary first, then left to right.</summary>
    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(mon, ref mi)) list.Add(new Monitor(mon, mi.rcMonitor, mi.szDevice, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    public static IntPtr MonitorHandleOf(IntPtr hwnd) => MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);

    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
    }

    /// <summary>
    /// Now on the same clock Windows.Graphics.Capture stamps frames with
    /// (QueryPerformanceCounter, in 100 ns units). Audio is stamped with this so it lines up exactly.
    /// </summary>
    public static long Qpc100ns()
    {
        long t = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
        return t / f * 10_000_000 + t % f * 10_000_000 / f;
    }

    // ---- kill ffmpeg if LiteClip dies: every child goes into a kill-on-close job

    static readonly IntPtr Job = CreateJob();

    static IntPtr CreateJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int len = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(len);
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9 /*ExtendedLimitInformation*/, ptr, (uint)len);
            Marshal.FreeHGlobal(ptr);
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    // ---- GPU scheduling priority, so a game maxing out the graphics card can't starve capture

    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("gdi32.dll")] static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

    /// <summary>
    /// Asks Windows to schedule this process's GPU work ahead of normal programs (games included).
    /// Realtime needs admin rights; High usually works without. Returns what was set.
    /// </summary>
    public static string RaiseGpuPriority(Process p)
    {
        foreach (var (level, name) in new[] { (5, "realtime"), (4, "high"), (3, "above normal") })
        {
            try
            {
                if (D3DKMTSetProcessSchedulingPriorityClass(p.Handle, level) == 0
                    && D3DKMTGetProcessSchedulingPriorityClass(p.Handle, out var got) == 0 && got == level)
                    return name;
            }
            catch { return "unavailable"; }
        }
        return "normal";
    }

    public static void AttachToJob(Process p)
    {
        try { if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, p.Handle); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
}
