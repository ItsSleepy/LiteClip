using System.Collections.Concurrent;
using System.Text;

namespace LiteClip.Core;

/// <summary>Tiny file logger. One file, rolled at 2 MB, written off-thread.</summary>
public static class Log
{
    static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    static string _path = "";
    static Thread? _writer;

    public static string FilePath => _path;

    public static void Init()
    {
        Directory.CreateDirectory(Paths.Logs);
        _path = Path.Combine(Paths.Logs, "liteclip.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                File.Move(_path, Path.Combine(Paths.Logs, "liteclip.old.log"), true);
        }
        catch { }
        _writer = new Thread(Pump) { IsBackground = true, Name = "log" };
        _writer.Start();
        Info($"LiteClip {AppInfo.Version} starting on {Environment.OSVersion}");
    }

    static void Pump()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var sb = new StringBuilder(line).AppendLine();
                while (Queue.TryTake(out var more)) sb.AppendLine(more);
                File.AppendAllText(_path, sb.ToString());
            }
            catch { }
        }
    }

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        Queue.TryAdd(line);
    }

    public static void Info(string msg) => Write("info", msg);
    public static void Warn(string msg) => Write("warn", msg);
    public static void Error(string msg, Exception? ex = null) => Write("error", ex == null ? msg : $"{msg}: {ex}");
}
