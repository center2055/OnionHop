using System;

namespace OnionHopV2.Core.Platform.MacOS;

internal static class MacNetworkExtensionService
{
    private const string EnvServiceName = "ONIONHOP_MAC_NE_SERVICE_NAME";
    private const string DefaultServiceName = "OnionHop Tunnel";

    public static string ServiceName
    {
        get
        {
            var envValue = Environment.GetEnvironmentVariable(EnvServiceName);
            return string.IsNullOrWhiteSpace(envValue) ? DefaultServiceName : envValue.Trim();
        }
    }

    public static bool IsConfigured(Action<string>? log = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var logger = log ?? (_ => { });
        return MacSwiftHelper.TryRun($"ne status --service-name \"{EscapeArg(ServiceName)}\"", logger, logFailures: false);
    }

    public static bool TryStart(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        return MacSwiftHelper.TryRun($"ne start --service-name \"{EscapeArg(ServiceName)}\"", log);
    }

    public static bool TryStop(Action<string> log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        return MacSwiftHelper.TryRun($"ne stop --service-name \"{EscapeArg(ServiceName)}\"", log);
    }

    private static string EscapeArg(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);
}
