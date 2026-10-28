using System.Runtime.InteropServices;

namespace LiteClip.Core;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

public sealed record Hotkey(Mods Mods, Keys Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(k - Keys.D0)).ToString(),
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        _ => k.ToString(),
    };

    public static Hotkey? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var mods = Mods.None;
        Keys? key = null;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Mods.Ctrl; break;
                case "shift": mods |= Mods.Shift; break;
                case "alt": mods |= Mods.Alt; break;
                case "win": case "meta": mods |= Mods.Win; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else if (raw == "`") key = Keys.Oemtilde;
                    else if (raw == "-") key = Keys.OemMinus;
                    else if (raw == "=") key = Keys.Oemplus;
                    else if (Enum.TryParse<Keys>(raw, true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        if (key is null or Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return null;
        return new Hotkey(mods, key.Value);
    }
}

/// <summary>
/// Global keyboard hook (WH_KEYBOARD_LL). Unlike RegisterHotKey it still fires while
/// fullscreen games hold the keyboard, and it never swallows the key.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action<Hotkey>? Pressed;
    public volatile bool Suspended;

    readonly Thread _thread;
    uint _threadId;
    IntPtr _hook;
    HookProc? _proc; // keep the delegate alive
    readonly HashSet<int> _down = new();
    volatile Hotkey[] _watch = Array.Empty<Hotkey>();

    public KeyboardHook()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "keyboard-hook" };
        _thread.Start();
    }

    public void Watch(IEnumerable<Hotkey?> keys) => _watch = keys.Where(k => k != null).Cast<Hotkey>().ToArray();

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = Callback;
        _hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"keyboard hook failed: {Marshal.GetLastWin32Error()}");
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if (msg is 0x100 or 0x104) // WM_KEYDOWN / WM_SYSKEYDOWN
            {
                if (_down.Add(vk) && !Suspended) Check((Keys)vk);
            }
            else if (msg is 0x101 or 0x105) _down.Remove(vk);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Check(Keys key)
    {
        var watch = _watch;
        if (watch.Length == 0) return;
        var mods = Mods.None;
        if (IsDown(0x11)) mods |= Mods.Ctrl;
        if (IsDown(0x10)) mods |= Mods.Shift;
        if (IsDown(0x12)) mods |= Mods.Alt;
        if (IsDown(0x5B) || IsDown(0x5C)) mods |= Mods.Win;
        foreach (var hk in watch)
        {
            if (hk.Key == key && hk.Mods == mods)
            {
                // never do work inside the hook: Windows drops slow hooks
                ThreadPool.QueueUserWorkItem(_ => Pressed?.Invoke(hk));
                return;
            }
        }
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
