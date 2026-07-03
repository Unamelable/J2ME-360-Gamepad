using System;
using System.IO;

namespace J2MEGamepad.Services;

internal static class LogHelper
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "J2MEGamepad");

    static LogHelper()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string source, string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(LogDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {msg}\n");
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
