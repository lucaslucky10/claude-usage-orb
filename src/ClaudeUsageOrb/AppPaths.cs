using System;
using System.IO;
using System.Text;

namespace ClaudeUsageOrb;

internal static class AppPaths
{
    public static string DataDir
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageOrb");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CacheFile => Path.Combine(DataDir, "last-usage.json");
    public static string LogFile => Path.Combine(DataDir, "orb.log");
}

/// <summary>Small rolling log. Never write tokens or account details here.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 256 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                string path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(path, path + ".1", overwrite: true);
                    File.Delete(path);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
