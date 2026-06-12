using System;
using System.IO;

namespace OnionHopV3.Core.Services;

public static class StartupLogger
{
    // Cap the diagnostic log so it can't grow without bound across sessions. When the live file
    // exceeds this, it is rotated to a single ".1" backup, bounding total disk use to ~2x this.
    private const long MaxBytes = 512 * 1024;

    private static readonly object LockObj = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OnionHop",
        "startup.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            lock (LockObj)
            {
                RotateIfTooLarge();
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Deletes the diagnostic log and its rotated backup. Wired into the in-app "clear logs" action
    /// so users can purge on-disk diagnostics (which embed paths/usernames) from a shared machine.
    /// </summary>
    public static void Clear()
    {
        lock (LockObj)
        {
            TryDelete(LogPath);
            TryDelete(LogPath + ".1");
        }
    }

    private static void RotateIfTooLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backup = LogPath + ".1";
                TryDelete(backup);
                File.Move(LogPath, backup);
            }
        }
        catch
        {
            // If rotation fails, fall back to truncating so the file can't grow unbounded.
            try { File.WriteAllText(LogPath, string.Empty); } catch { }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
