using Avalonia;
using System;
using OnionHopV2.Core.Services;

namespace OnionHopV2.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (AdminHelperServer.IsHelperMode(args))
        {
            AdminHelperServer.Run(args);
            return;
        }

        var instanceMutex = SingleInstanceIpc.AcquireMutex(out var isPrimary);
        if (!isPrimary)
        {
            var message = Array.Exists(args, a => string.Equals(a, "--shutdown-existing", StringComparison.OrdinalIgnoreCase))
                ? "shutdown"
                : "show";

            var sent = false;
            try
            {
                sent = SingleInstanceIpc.TrySendAsync(message).GetAwaiter().GetResult();
            }
            catch
            {
                sent = false;
            }

            if (sent)
            {
                instanceMutex.Dispose();
                return;
            }

            // IPC failed (e.g., stale/hung primary). Launch this instance as a fallback
            // instead of silently exiting so the app can still open.
            instanceMutex.Dispose();
        }

        if (Array.Exists(args, a => string.Equals(a, "--shutdown-existing", StringComparison.OrdinalIgnoreCase)))
        {
            // If we are the primary instance but asked to shutdown, we just exit.
            // This happens if the installer launches us to close us, but we weren't running yet 
            // (or we just acquired the mutex). 
            // If we aren't primary, we sent the message above.
            if (isPrimary)
            {
                instanceMutex.Dispose();
            }
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (isPrimary)
            {
                instanceMutex.Dispose();
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
