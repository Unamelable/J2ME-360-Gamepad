using System;
using System.IO;

namespace J2MEGamepad.Services;

internal static class LogHelper
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "J2MEGamepad");

    private static string _lastKey = "";
    private static int _repeatCount;
    private static readonly object _lock = new();

    static LogHelper()
    {
        Directory.CreateDirectory(LogDir);
        var logPath = Path.Combine(LogDir, "crash.log");
        try { File.WriteAllText(logPath, $"=== Session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); }
        catch { }
    }

    public static void Info(string source, string msg)
    {
        string key = source + "|" + msg;
        string logLine;
        lock (_lock)
        {
            if (key == _lastKey)
            {
                _repeatCount++;
                return;
            }

            if (_repeatCount > 0)
            {
                try
                {
                    File.AppendAllText(Path.Combine(LogDir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  ... repeated {_repeatCount}x\n");
                }
                catch { }
                _repeatCount = 0;
            }

            _lastKey = key;
            logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {msg}\n";
        }

        try
        {
            File.AppendAllText(Path.Combine(LogDir, "crash.log"), logLine);
        }
        catch { }
    }

    public static void Error(string source, string context, Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(LogDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {context}: {ex}\n{ex.StackTrace}\n\n");
        }
        catch { }
    }
}
