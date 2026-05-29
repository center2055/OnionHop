using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Services;

internal sealed class BrowserExtensionBridgeServer : IDisposable
{
    private const string PipeName = "OnionHopV2.ExtensionBridge";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppStateViewModel _state;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _torBundlePath = Path.Combine(AppContext.BaseDirectory, "tor");
    private Task? _serverTask;

    public BrowserExtensionBridgeServer(AppStateViewModel state)
    {
        _state = state;
    }

    public void Start()
    {
        _serverTask ??= Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            _cts.Dispose();
        }
        catch
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                var line = await reader.ReadLineAsync().WaitAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                BridgeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions);
                    if (request == null || string.IsNullOrWhiteSpace(request.Type))
                    {
                        response = BridgeResponse.Fail("Malformed bridge request.");
                    }
                    else
                    {
                        response = await HandleRequestAsync(request, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    response = BridgeResponse.Fail(ex.Message);
                }

                var payload = JsonSerializer.Serialize(response, JsonOptions);
                await writer.WriteLineAsync(payload).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<BridgeResponse> HandleRequestAsync(BridgeRequest request, CancellationToken token)
    {
        switch (request.Type.Trim().ToLowerInvariant())
        {
            case "healthcheck":
                return BridgeResponse.Success(new
                {
                    appVersion = GetAppVersion(),
                    pipe = PipeName
                });
            case "getversion":
                return BridgeResponse.Success(new
                {
                    appVersion = GetAppVersion(),
                    hostVersion = "desktop-bridge-0.1.0"
                });
            case "getstatus":
                return BridgeResponse.Success(await RunOnUiThreadAsync(BuildStatusPayload).ConfigureAwait(false));
            case "getconfig":
                return BridgeResponse.Success(await RunOnUiThreadAsync(BuildConfigPayload).ConfigureAwait(false));
            case "setconfig":
                await RunOnUiThreadAsync(() =>
                {
                    ApplyConfig(request.Payload);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                return BridgeResponse.Success(await RunOnUiThreadAsync(BuildStatusPayload).ConfigureAwait(false));
            case "connect":
                await RunOnUiThreadAsync(() => _state.ConnectCommand.ExecuteAsync(null)).ConfigureAwait(false);
                return BridgeResponse.Success(await RunOnUiThreadAsync(BuildStatusPayload).ConfigureAwait(false));
            case "disconnect":
                await RunOnUiThreadAsync(() => _state.DisconnectCommand.ExecuteAsync(null)).ConfigureAwait(false);
                return BridgeResponse.Success(await RunOnUiThreadAsync(BuildStatusPayload).ConfigureAwait(false));
            case "newidentity":
                var identityChangesBefore = _state.SessionIdentityChanges;
                await RunOnUiThreadAsync(() => _state.ChangeIdentityCommand.ExecuteAsync(null)).ConfigureAwait(false);
                return BridgeResponse.Success(new { rotated = _state.SessionIdentityChanges > identityChangesBefore });
            case "openapp":
                return BridgeResponse.Success(new { opened = true });
            default:
                return BridgeResponse.Fail($"Unsupported bridge request type '{request.Type}'.");
        }
    }

    private object BuildStatusPayload()
    {
        var socksPort = ParsePort(_state.SocksProxyPort, ParsePort(_state.PreferredSocksPort, OnionHopV2.Core.OnionHopConnectOptions.DefaultSocksPort));
        var httpPort = ParseNullablePort(_state.HttpProxyPort, ParsePort(_state.PreferredHttpPort, OnionHopV2.Core.OnionHopConnectOptions.DefaultHttpPort));
        var connected = _state.IsConnected;
        var running = _state.IsConnecting || _state.IsConnected || _state.IsDisconnecting;
        var bootstrapPercent = _state.IsConnected
            ? 100
            : _state.IsConnecting
                ? (int)Math.Clamp(Math.Round(_state.ConnectionProgress * 100d), 0, 99)
                : 0;

        return new
        {
            installed = true,
            detected = true,
            running,
            connected,
            currentMode = _state.ConnectionStatus,
            bootstrapPercent,
            bootstrapMessage = _state.IsConnecting ? _state.StatusMessage : null,
            bridgeStatus = BuildBridgeStatus(),
            socksEndpoint = running
                ? new
                {
                    label = "OnionHop desktop SOCKS",
                    protocol = "socks5",
                    host = "127.0.0.1",
                    port = socksPort,
                    source = "onionhop"
                }
                : null,
            currentIp = NormalizeIp(_state.CurrentIp),
            currentIpSource = connected ? "tor" : "direct",
            appVersion = GetAppVersion(),
            hostVersion = "desktop-bridge-0.1.0",
            lastError = BuildLastError(),
            capabilities = new
            {
                openApp = true,
                newIdentity = connected,
                connect = true,
                disconnect = true,
                getConfig = true,
                setConfig = true
            },
            runtime = new
            {
                socksPort,
                httpPort
            }
        };
    }

    private object BuildConfigPayload()
    {
        return new
        {
            torBundlePath = Directory.Exists(_torBundlePath) ? _torBundlePath : null,
            socksHost = "127.0.0.1",
            socksPort = ParsePort(_state.PreferredSocksPort, OnionHopV2.Core.OnionHopConnectOptions.DefaultSocksPort),
            bridgeType = NormalizeBridgeType(_state.UseTorBridges ? _state.SelectedBridgeType : "direct"),
            exitLocation = NormalizeExitLocation(_state.SelectedLocation),
            strictExitLocation = true,
            connectTimeoutMs = ParsePort(_state.ConnectionTimeoutSeconds, 30) * 1000,
            runtime = new
            {
                currentIp = NormalizeIp(_state.CurrentIp),
                currentIpSource = _state.IsConnected ? "tor" : "direct",
                lastError = BuildLastError()
            }
        };
    }

    private void ApplyConfig(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return;
        }

        if (json.TryGetProperty("bridgeType", out var bridgeTypeElement))
        {
            var bridgeType = NormalizeBridgeType(bridgeTypeElement.GetString());
            if (string.Equals(bridgeType, "direct", StringComparison.OrdinalIgnoreCase))
            {
                _state.UseTorBridges = false;
            }
            else
            {
                _state.UseTorBridges = true;
                _state.SelectedBridgeType = bridgeType;
            }
        }

        if (json.TryGetProperty("exitLocation", out var exitLocationElement))
        {
            _state.SelectedLocation = ToDesktopExitLocation(exitLocationElement.GetString());
        }

        if (json.TryGetProperty("socksPort", out var socksPortElement) && socksPortElement.TryGetInt32(out var socksPort) && socksPort is >= 1 and <= 65535)
        {
            _state.PreferredSocksPort = socksPort.ToString();
        }

        if (json.TryGetProperty("connectTimeoutMs", out var timeoutElement) && timeoutElement.TryGetInt32(out var connectTimeoutMs))
        {
            var seconds = Math.Clamp(connectTimeoutMs / 1000, 0, 3600);
            _state.ConnectionTimeoutSeconds = seconds.ToString();
        }
    }

    private string BuildBridgeStatus()
    {
        if (_state.IsConnecting)
        {
            return _state.UseTorBridges
                ? $"Bootstrapping {NormalizeBridgeType(_state.SelectedBridgeType)} bridges"
                : "Bootstrapping direct Tor";
        }

        if (_state.IsConnected)
        {
            return _state.UseTorBridges
                ? $"Using {NormalizeBridgeType(_state.SelectedBridgeType)} bridges"
                : "Direct Tor active";
        }

        return _state.UseTorBridges
            ? $"{NormalizeBridgeType(_state.SelectedBridgeType)} bridges ready"
            : "Configured for direct Tor";
    }

    private string? BuildLastError()
    {
        if (_state.IsConnected || _state.IsConnecting)
        {
            return null;
        }

        var message = (_state.StatusMessage ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(message) ||
            string.Equals(message, "Ready to route traffic through Tor.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "Bereit, den Datenverkehr über Tor zu leiten.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return message;
    }

    private static string? NormalizeIp(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 ||
            candidate == "--.--.--.--" ||
            candidate.Contains("Resolving", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("Aufl", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return candidate;
    }

    private static string NormalizeBridgeType(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "automatic" => "automatic",
            "snowflake" => "snowflake",
            "webtunnel" => "webtunnel",
            "obfs4" => "obfs4",
            _ => "direct"
        };
    }

    private static string? NormalizeExitLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, AppStateViewModel.AutomaticLocationLabel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = value.Trim().Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        return trimmed.Length == 2 ? $"{{{trimmed.ToUpperInvariant()}}}" : trimmed;
    }

    private static string ToDesktopExitLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AppStateViewModel.AutomaticLocationLabel;
        }

        var trimmed = value.Trim().Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        return trimmed.Length == 2 ? trimmed.ToLowerInvariant() : trimmed;
    }

    private static int ParsePort(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : fallback;
    }

    private static int? ParseNullablePort(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "--")
        {
            return null;
        }

        return ParsePort(value, fallback);
    }

    private static string GetAppVersion()
    {
        var assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private sealed class BridgeRequest
    {
        public string Type { get; set; } = string.Empty;
        public JsonElement? Payload { get; set; }
    }

    private sealed class BridgeResponse
    {
        public bool Ok { get; set; }
        public object? Data { get; set; }
        public string? Error { get; set; }

        public static BridgeResponse Success(object? data) => new()
        {
            Ok = true,
            Data = data
        };

        public static BridgeResponse Fail(string error) => new()
        {
            Ok = false,
            Error = error
        };
    }
}
