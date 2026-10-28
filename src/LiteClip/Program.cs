using LiteClip.Core;
using LiteClip.UI;

namespace LiteClip;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\LiteClip.V2", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LiteClip.V2.Show");
        if (!first)
        {
            showSignal.Set(); // already running: bring its window up instead
            return;
        }

        Paths.EnsureAll();
        Log.Init();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        bool tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var app = new LiteClipApp(showWindow: !tray, showSignal);
        Application.Run(app);
    }
}
