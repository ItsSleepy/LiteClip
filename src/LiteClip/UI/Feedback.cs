using System.Drawing.Drawing2D;
using System.Media;
using System.Runtime.InteropServices;
using LiteClip.Core;

namespace LiteClip.UI;

/// <summary>The little "Clip saved" chime, synthesised so there's no audio file to ship.</summary>
public static class Sounds
{
    static readonly byte[] Chime = Build(new[] { (880.0, 0.055), (1318.5, 0.11) }, 0.22);
    static readonly byte[] Bonk = Build(new[] { (330.0, 0.09) }, 0.18);

    public static void Saved() => Play(Chime);
    public static void Failed() => Play(Bonk);

    static void Play(byte[] wav)
    {
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
    }

    static byte[] Build((double hz, double sec)[] notes, double vol)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (hz, sec) in notes)
        {
            int n = (int)(rate * sec);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Exp(-t * 18);
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * t) * env * vol * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}

/// <summary>Small pop-up in the corner. Never takes focus, never steals clicks from the game.</summary>
public sealed class Toast : Form
{
    readonly string _title, _sub;
    readonly bool _ok;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    int _tick;
    static Toast? _current;

    Toast(string title, string sub, bool ok)
    {
        _title = title; _sub = sub; _ok = ok;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 19, 24);
        DoubleBuffered = true;
        Opacity = 0;
        var scale = DeviceDpi / 96f;
        Size = new Size((int)(300 * scale), (int)(64 * scale));
        _timer.Tick += (_, _) => Step();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000020 /*TRANSPARENT*/ | 0x00080000 /*LAYERED*/;
            return cp;
        }
    }

    public static void Show(string title, string sub, bool ok, IntPtr nearWindow)
    {
        _current?.Close();
        var t = new Toast(title, sub, ok);
        var area = nearWindow != IntPtr.Zero ? Screen.FromHandle(nearWindow).Bounds : Screen.PrimaryScreen!.Bounds;
        var m = (int)(20 * t.DeviceDpi / 96f);
        t.Location = new Point(area.Right - t.Width - m, area.Top + m);
        _current = t;
        t.Show();
        t._timer.Start();
    }

    void Step()
    {
        _tick++;
        if (_tick <= 10) Opacity = _tick / 10.0 * 0.96;
        else if (_tick > 150 && _tick <= 170) Opacity = (170 - _tick) / 20.0 * 0.96;
        else if (_tick > 170) { _timer.Stop(); Close(); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        try { Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;
        var accent = _ok ? Color.FromArgb(124, 92, 255) : Color.FromArgb(255, 92, 110);
        using (var b = new LinearGradientBrush(new Rectangle(0, 0, (int)(4 * s), Height), accent, _ok ? Color.FromArgb(0, 212, 255) : accent, 90f))
            g.FillRectangle(b, 0, 0, 4 * s, Height);
        // dot
        using (var dot = new SolidBrush(accent)) g.FillEllipse(dot, 18 * s, Height / 2f - 5 * s, 10 * s, 10 * s);
        using var f1 = new Font("Segoe UI Semibold", 10.5f);
        using var f2 = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(g, _title, f1, new Point((int)(38 * s), (int)(12 * s)), Color.White);
        TextRenderer.DrawText(g, _sub, f2, new Point((int)(38 * s), (int)(34 * s)), Color.FromArgb(160, 164, 178));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        if (_current == this) _current = null;
        base.Dispose(disposing);
    }
}
