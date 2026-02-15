using System;
using System.IO;

namespace OnionHopV2.Core.Services;

public static class StartupLogger
{
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
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}

