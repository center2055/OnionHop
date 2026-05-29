using System;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace OnionHopV3.Core.Services;

internal static class AdminHelperProtocol
{
    private const string BasePipeName = "OnionHop.AdminHelper";
    public static readonly Encoding PipeTextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    public const int PipeTextBufferSize = 4096;

    public static string PipeName => OperatingSystem.IsWindows() ? GetWindowsPipeName() : BasePipeName;

    [SupportedOSPlatform("windows")]
    private static string GetWindowsPipeName()
    {
        // Make the pipe name unique per user so a stale elevated helper from an older build/install
        // can't block new connects (and avoids UAC token boundary quirks).
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return BasePipeName;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        var suffix = Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
        return $"{BasePipeName}.{suffix}";
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class AdminHelperDnsProxyRequest
{
    public string? NameServerAddress { get; set; }
    public bool RouteAllDns { get; set; }
}

internal sealed class PersistentAdminHelperRequest
{
    public string? UserSid { get; set; }
    public string? UserName { get; set; }
}

internal sealed class HelperRequest
{
    public string? RequestId { get; set; }
    public string? Command { get; set; }
    public object? Payload { get; set; }
}

internal sealed class HelperResponse
{
    public string? RequestId { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public object? Payload { get; set; }
}

internal sealed class AdminHelperStatus
{
    public bool VpnRunning { get; set; }
    public int? VpnExitCode { get; set; }
    public bool KillSwitchEnabled { get; set; }
    public bool IsAdministrator { get; set; }
    public string Mode { get; set; } = "transient";
}
