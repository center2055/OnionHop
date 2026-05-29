using System.Net;
using System.Reflection;
using System.Text;
using OnionHopV2.Core;
using OnionHopV2.Core.Platform;
using OnionHopV2.Core.Services;

namespace OnionHopV2.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var shutdownCts = new CancellationTokenSource();
        await using var host = new CliHost();
        var ctrlCCount = 0;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            ctrlCCount++;
            if (ctrlCCount == 1)
            {
                host.WriteInfo("Ctrl+C received. Stopping...");
                shutdownCts.Cancel();
                return;
            }

            host.WriteWarning("Second Ctrl+C received. Forcing exit.");
            Environment.Exit(130);
        };

        try
        {
            if (args.Length == 0)
            {
                host.PrintBanner();
                host.PrintHelp();
                await host.RunInteractiveAsync(shutdownCts.Token);
                return 0;
            }

            return await host.ExecuteArgsAsync(args, shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
}

internal sealed class CliHost : IAsyncDisposable
{
    private readonly OnionHopClient _client = new();
    private readonly SmartConnectAdvisor _advisor = new();
    private readonly object _statusLock = new();
    private readonly object _consoleLock = new();
    private CancellationTokenSource? _connectCts;
    private OnionHopClient.StatusUpdate _status = new(
        IsConnecting: false,
        IsConnected: false,
        IsDisconnecting: false,
        ConnectionStatus: "Disconnected",
        StatusMessage: "Ready.",
        ConnectionProgress: 0,
        CurrentIp: "--.--.--.--",
        SocksPort: OnionHopClient.DefaultSocksPort,
        HttpPort: null);
    private OnionHopClient.DependencyUpdate _dependency = new(false, "Idle", 0);
    private string _lastStatusPrintKey = string.Empty;
    private bool _shutdownRequested;

    public CliHost()
    {
        _client.Log += (_, message) => WriteLog("LOG", message, ConsoleColor.DarkGray);
        _client.DnsLog += (_, message) => WriteLog("DNS", message, ConsoleColor.DarkYellow);
        _client.DependencyUpdated += (_, update) => OnDependencyUpdated(update);
        _client.StatusUpdated += (_, update) => OnStatusUpdated(update);
    }

    public void PrintBanner()
    {
        var version = ResolveVersion();
        const string banner = """
   ____        _             _   _
  / __ \      (_)           | | | |
 | |  | |_ __  _  ___  _ __ | |_| | ___  _ __
 | |  | | '_ \| |/ _ \| '_ \|  _  |/ _ \| '_ \
 | |__| | | | | | (_) | | | | | | | (_) | |_) |
  \____/|_| |_|_|\___/|_| |_|_| |_|\___/| .__/
                                        | |
                                        |_|
   _____ _      _____
  / ____| |    |_   _|
 | |    | |      | |
 | |____| |____ _| |_
  \_____|______|_____|
""";
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine(banner, ConsoleColor.Cyan);
        WriteLine($"OnionHop V{version}", ConsoleColor.Cyan);
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    public void PrintHelp()
    {
        WriteLine("Commands:", ConsoleColor.White);
        WriteLine("  help                         Show this help", ConsoleColor.Gray);
        WriteLine("  connect [options]            Connect to Tor", ConsoleColor.Gray);
        WriteLine("  countries                    List country options for --exit/--entry", ConsoleColor.Gray);
        WriteLine("  disconnect                   Disconnect", ConsoleColor.Gray);
        WriteLine("  status                       Show current status snapshot", ConsoleColor.Gray);
        WriteLine("  ip                           Refresh current IP", ConsoleColor.Gray);
        WriteLine("  newnym                       Request a new Tor circuit", ConsoleColor.Gray);
        WriteLine("  plan [options]               Show Smart Connect strategy plan", ConsoleColor.Gray);
        WriteLine("  deps                         Ensure dependencies are downloaded", ConsoleColor.Gray);
        WriteLine("  clear                        Clear console", ConsoleColor.Gray);
        WriteLine("  exit | quit                  Exit CLI", ConsoleColor.Gray);
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine("Connect options:", ConsoleColor.White);
        WriteLine("  --smart <on|off>             Smart Connect mode (default: on)", ConsoleColor.Gray);
        WriteLine("  --mode <proxy|tun>           Connection mode (default: proxy)", ConsoleColor.Gray);
        WriteLine("  --hold <on|off>              Keep process running after connect", ConsoleColor.Gray);
        WriteLine("  --bridges <on|off>           Force bridges in manual mode", ConsoleColor.Gray);
        WriteLine("  --bridge-type <type>         automatic, obfs4, snowflake, webtunnel, conjure, meek-azure, custom", ConsoleColor.Gray);
        WriteLine("  --censored <on|off>          Enable censored mode in manual mode", ConsoleColor.Gray);
        WriteLine("  --tun-core <sing-box|xray>   TUN core backend (default: sing-box)", ConsoleColor.Gray);
        WriteLine("  --proxy-scope <system|socks|local>", ConsoleColor.Gray);
        WriteLine("  --exit <auto|cc|country>     Exit location (e.g. us, de, \"United States\")", ConsoleColor.Gray);
        WriteLine("  --entry <auto|cc|country>    Entry location (ignored when bridges are on)", ConsoleColor.Gray);
        WriteLine("  --exit-fingerprint <hex40>   Pin exit relay fingerprint (40 hex chars)", ConsoleColor.Gray);
        WriteLine("  --strict-exit-fingerprint <on|off>  Fail if pinned exit is unavailable", ConsoleColor.Gray);
        WriteLine("  --dns <auto|cloudflare|quad9|adguard|mullvad|opendns|google|custom>", ConsoleColor.Gray);
        WriteLine("  --onion-dns-proxy <on|off>   Enable .onion DNS proxy listener", ConsoleColor.Gray);
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine("Examples:", ConsoleColor.White);
        WriteLine("  connect --smart on", ConsoleColor.Gray);
        WriteLine("  connect --smart off --mode tun --bridges on --bridge-type snowflake", ConsoleColor.Gray);
        WriteLine("  connect --smart off --exit us --entry nl", ConsoleColor.Gray);
        WriteLine("  countries", ConsoleColor.Gray);
        WriteLine("  plan --mode proxy", ConsoleColor.Gray);
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    public async Task RunInteractiveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            WriteInline("onionhop> ", ConsoleColor.Green);
            var line = Console.ReadLine();
            if (line == null)
            {
                break;
            }

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                continue;
            }

            var result = await ExecuteCommandAsync(tokens, isInteractive: true, token);
            if (result == CommandResult.Exit)
            {
                break;
            }
        }
    }

    public async Task<int> ExecuteArgsAsync(string[] args, CancellationToken token)
    {
        var tokens = args.ToList();
        var result = await ExecuteCommandAsync(tokens, isInteractive: false, token);
        return result switch
        {
            CommandResult.Success => 0,
            CommandResult.Exit => 0,
            CommandResult.Failure => 1,
            _ => 1
        };
    }

    public async Task ShutdownAsync()
    {
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;

        try
        {
            _connectCts?.Cancel();
            await _client.DisconnectAsync();
        }
        catch
        {
        }
    }

    public void WriteInfo(string message) => WriteLog("INFO", message, ConsoleColor.Cyan);
    public void WriteWarning(string message) => WriteLog("WARN", message, ConsoleColor.Yellow);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _connectCts?.Dispose();
        _client.Dispose();
    }

    private async Task<CommandResult> ExecuteCommandAsync(IReadOnlyList<string> tokens, bool isInteractive, CancellationToken token)
    {
        var command = tokens[0].Trim().ToLowerInvariant();
        switch (command)
        {
            case "help":
            case "?":
                PrintHelp();
                return CommandResult.Success;

            case "connect":
            {
                var request = ParseConnectRequest(tokens.Skip(1).ToList(), holdDefault: !isInteractive);
                var connected = await ConnectAsync(request, token);
                if (!connected)
                {
                    return CommandResult.Failure;
                }

                if (!isInteractive && request.Hold)
                {
                    WriteInfo("Connected. Press Ctrl+C to disconnect and exit.");
                    await WaitUntilCanceledAsync(token);
                }

                return CommandResult.Success;
            }

            case "disconnect":
            case "stop":
                await DisconnectAsync();
                return CommandResult.Success;

            case "countries":
            case "country":
            case "locations":
                await ShowCountriesAsync(token);
                return CommandResult.Success;

            case "status":
                await PrintStatusAsync(token);
                return CommandResult.Success;

            case "ip":
            {
                var before = GetStatusSnapshot();
                WriteInfo("Refreshing IP...");
                await _client.RefreshIpAsync(updateStatusMessage: true, token);
                var after = GetStatusSnapshot();
                if (!string.Equals(before.CurrentIp, after.CurrentIp, StringComparison.OrdinalIgnoreCase))
                {
                    WriteInfo($"IP updated: {before.CurrentIp} -> {after.CurrentIp}");
                }
                else
                {
                    WriteInfo($"IP unchanged: {after.CurrentIp}");
                }

                return CommandResult.Success;
            }

            case "newnym":
            case "identity":
            {
                await _client.ChangeIdentityAsync(token);
                var snapshot = GetStatusSnapshot();
                WriteInfo(snapshot.StatusMessage);
                return CommandResult.Success;
            }

            case "plan":
            {
                var request = ParseConnectRequest(tokens.Skip(1).ToList(), holdDefault: false);
                await ShowPlanAsync(request, token);
                return CommandResult.Success;
            }

            case "deps":
            case "dependencies":
            {
                var ok = await _client.EnsureDependenciesAsync(token);
                if (ok)
                {
                    WriteInfo("Dependencies are ready.");
                    return CommandResult.Success;
                }

                WriteWarning("Dependency ensure failed. Check logs above.");
                return CommandResult.Failure;
            }

            case "clear":
                Console.Clear();
                return CommandResult.Success;

            case "quit":
            case "exit":
                return CommandResult.Exit;

            case "version":
                WriteLine($"OnionHop CLI {ResolveVersion()}", ConsoleColor.White);
                return CommandResult.Success;

            default:
                WriteWarning($"Unknown command: {command}");
                WriteInfo("Type 'help' to list commands.");
                return CommandResult.Failure;
        }
    }

    private async Task<bool> ConnectAsync(ConnectRequest request, CancellationToken token)
    {
        var snapshot = GetStatusSnapshot();
        if (snapshot.IsConnected || snapshot.IsConnecting)
        {
            WriteWarning("Already connected/connecting.");
            return snapshot.IsConnected;
        }

        if (!string.IsNullOrWhiteSpace(request.ExitNodeFingerprint) &&
            !IsValidExitNodeFingerprint(request.ExitNodeFingerprint))
        {
            WriteWarning("Invalid --exit-fingerprint. Expected 40 hex characters.");
            return false;
        }

        var baseOptions = BuildBaseOptions(request);
        IReadOnlyList<SmartConnectAdvisor.Strategy> strategies;
        if (request.SmartConnect)
        {
            SmartConnectAdvisor.Plan plan;
            try
            {
                plan = await _advisor.BuildPlanAsync(baseOptions, message => WriteLog("SMART", message, ConsoleColor.Blue), token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteWarning($"Smart Connect planning failed: {ex.Message}");
                plan = new SmartConnectAdvisor.Plan(
                    PublicIp: null,
                    CountryCode: null,
                    Risk: SmartConnectAdvisor.RiskLevel.Unknown,
                    RestrictionScore: 0,
                    SampleCount: 0,
                    NetworkCount: 0,
                    NotOkNetworks: 0,
                    LastTested: null,
                    Strategies: []);
            }

            strategies = plan.Strategies.Count > 0
                ? plan.Strategies
                : SmartConnectAdvisor.BuildStrategiesForRisk(baseOptions, SmartConnectAdvisor.RiskLevel.Unknown);
        }
        else
        {
            strategies =
            [
                new SmartConnectAdvisor.Strategy("manual", "Smart Connect disabled.", baseOptions)
            ];
        }

        Exception? lastError = null;
        for (var i = 0; i < strategies.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var strategy = strategies[i];
            var attemptOptions = strategy.Options;
            if (attemptOptions.OnionDnsProxyEnabled && !PlatformHelper.IsAdministrator())
            {
                attemptOptions = attemptOptions with { OnionDnsProxyEnabled = false };
                WriteWarning(".onion DNS proxy requires elevated privileges; continuing without it.");
            }

            if (request.SmartConnect)
            {
                WriteInfo($"Attempt {i + 1}/{strategies.Count}: {strategy.Name} ({strategy.Reason})");
            }

            if (!await EnsureAdminRequirementsForConnectAsync(attemptOptions, token))
            {
                return false;
            }

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            try
            {
                await _client.ConnectAsync(attemptOptions, _connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                WriteWarning($"Connect attempt failed: {ex.Message}");
            }

            snapshot = GetStatusSnapshot();
            if (snapshot.IsConnected)
            {
                WriteInfo("Connected.");
                return true;
            }
        }

        if (lastError != null)
        {
            WriteWarning($"Connection failed: {lastError.Message}");
        }
        else
        {
            WriteWarning("Connection failed. No strategy succeeded.");
        }

        return false;
    }

    private async Task<bool> EnsureAdminRequirementsForConnectAsync(OnionHopConnectOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var isTun = string.Equals(options.SelectedConnectionMode, OnionHopConnectOptions.ConnectionModeTun, StringComparison.Ordinal);
        var isAdmin = PlatformHelper.IsAdministrator();

        if (options.OnionDnsProxyEnabled && !isAdmin)
        {
            WriteWarning(".onion DNS proxy requires elevated privileges.");
            return false;
        }

        if (isTun && !isAdmin)
        {
            if (OperatingSystem.IsWindows())
            {
                WriteInfo("TUN mode selected. Verifying elevated helper access...");
                if (!await _client.EnsureAdminHelperAsync())
                {
                    WriteWarning("Administrator helper is not available. Start the terminal as Administrator for strict TUN mode.");
                    return false;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (_client.CanUseMacNetworkExtension())
                {
                    WriteInfo("Using configured macOS Network Extension profile for TUN mode (no sudo launch required).");
                    return true;
                }

                WriteWarning("TUN mode on macOS requires root privileges or a configured Network Extension profile.");
                return false;
            }
            else
            {
                WriteWarning("TUN mode requires root privileges on this platform. Re-run with sudo.");
                return false;
            }
        }

        return true;
    }

    private async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        await _client.DisconnectAsync();
        WriteInfo("Disconnected.");
    }

    private async Task ShowCountriesAsync(CancellationToken token)
    {
        var countries = await _client.GetCountryStatsAsync(token).ConfigureAwait(false);
        if (countries.Count == 0)
        {
            WriteWarning("No country data available right now.");
            return;
        }

        WriteLine("Country options (--exit / --entry):", ConsoleColor.White);
        foreach (var country in countries)
        {
            var code = country.CountryCode.ToUpperInvariant();
            WriteLine($"  {code,-2}  {country.CountryName,-28} entry:{country.EntryNodes,5} exit:{country.ExitNodes,5}", ConsoleColor.Gray);
        }
    }

    private async Task ShowPlanAsync(ConnectRequest request, CancellationToken token)
    {
        var baseOptions = BuildBaseOptions(request);
        var plan = await _advisor.BuildPlanAsync(baseOptions, null, token);
        WriteLine($"Country: {plan.CountryCode ?? "unknown"}", ConsoleColor.White);
        WriteLine($"IP: {plan.PublicIp ?? "unknown"}", ConsoleColor.White);
        WriteLine($"Risk: {plan.Risk}", ConsoleColor.White);
        WriteLine($"Score: {plan.RestrictionScore:0.000}", ConsoleColor.White);
        if (plan.Strategies.Count == 0)
        {
            WriteLine("No strategies available.", ConsoleColor.Yellow);
            return;
        }

        WriteLine("Strategies:", ConsoleColor.White);
        for (var i = 0; i < plan.Strategies.Count; i++)
        {
            var strategy = plan.Strategies[i];
            WriteLine($"  {i + 1}. {strategy.Name} - {strategy.Reason}", ConsoleColor.Gray);
        }
    }

    private OnionHopConnectOptions BuildBaseOptions(ConnectRequest request)
    {
        var mode = request.Mode.ToLowerInvariant() switch
        {
            "tun" => OnionHopConnectOptions.ConnectionModeTun,
            _ => OnionHopConnectOptions.ConnectionModeProxy
        };

        var proxyScope = request.ProxyScope.ToLowerInvariant() switch
        {
            "system" => OnionHopConnectOptions.ProxyScopeSystem,
            "local" => OnionHopConnectOptions.ProxyScopeLocalOnly,
            _ => OnionHopConnectOptions.ProxyScopeSystemSocks
        };

        var bridgeType = NormalizeBridgeType(request.BridgeType);
        var useBridges = request.UseBridges;
        if (!useBridges && request.BridgeTypeProvided)
        {
            useBridges = true;
        }

        var useCensoredMode = request.UseCensoredMode ?? useBridges;
        return new OnionHopConnectOptions
        {
            SelectedConnectionMode = mode,
            ProxyScopeMode = proxyScope,
            TunCoreMode = NormalizeTunCoreMode(request.TunCore),
            SelectedLocation = request.ExitLocation,
            SelectedEntryLocation = request.EntryLocation,
            ExitNodeFingerprint = request.ExitNodeFingerprint,
            StrictManualExitNodeFingerprint = request.StrictManualExitNodeFingerprint,
            SelectedBridgeType = bridgeType,
            BridgeSourceMode = OnionHopConnectOptions.BridgeSourceAuto,
            UseTorBridges = useBridges,
            UseCensoredMode = useCensoredMode,
            UseSnowflakeAmp = request.UseSnowflakeAmp,
            SelectedDnsProvider = NormalizeDnsProvider(request.DnsProvider),
            OnionDnsProxyEnabled = request.UseOnionDnsProxy,
            PreferredSocksPort = OnionHopConnectOptions.DefaultSocksPort,
            PreferredHttpPort = OnionHopConnectOptions.DefaultHttpPort,
            ConnectionPaddingMode = OnionHopConnectOptions.ConnectionPaddingAuto,
            TorIpv6Mode = OnionHopConnectOptions.ToggleModeDefault,
            HardwareAccelerationMode = OnionHopConnectOptions.ToggleModeDefault,
            CustomDohPath = "/dns-query"
        };
    }

    private static string NormalizeBridgeType(string bridgeType)
    {
        var normalized = bridgeType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "obfs4" => "obfs4",
            "snowflake" => "snowflake",
            "conjure" => "conjure",
            "webtunnel" => "webtunnel",
            "meek-azure" => "meek-azure",
            "custom" => "custom",
            _ => "automatic"
        };
    }

    private static string NormalizeDnsProvider(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => OnionHopConnectOptions.DnsProviderAuto,
            "cloudflare" => OnionHopConnectOptions.DnsProviderCloudflare,
            "quad9" => OnionHopConnectOptions.DnsProviderQuad9,
            "adguard" => OnionHopConnectOptions.DnsProviderAdGuard,
            "mullvad" => OnionHopConnectOptions.DnsProviderMullvad,
            "opendns" => OnionHopConnectOptions.DnsProviderOpenDns,
            "google" => OnionHopConnectOptions.DnsProviderGoogle,
            "custom" => OnionHopConnectOptions.DnsProviderCustom,
            _ => OnionHopConnectOptions.DnsProviderAuto
        };
    }

    private static string NormalizeTunCoreMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "xray" => OnionHopConnectOptions.TunCoreXray,
            "singbox" => OnionHopConnectOptions.TunCoreSingBox,
            _ => OnionHopConnectOptions.TunCoreSingBox
        };
    }

    private async Task PrintStatusAsync(CancellationToken token)
    {
        await TryRefreshIpForStatusAsync(token).ConfigureAwait(false);
        PrintStatus();
    }

    private async Task TryRefreshIpForStatusAsync(CancellationToken token)
    {
        var snapshot = GetStatusSnapshot();
        if (snapshot.IsConnected || snapshot.IsConnecting || snapshot.IsDisconnecting)
        {
            return;
        }

        if (IPAddress.TryParse(snapshot.CurrentIp?.Trim(), out _))
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        await _client.RefreshIpAsync(updateStatusMessage: false, cts.Token).ConfigureAwait(false);
    }

    private void PrintStatus()
    {
        var snapshot = GetStatusSnapshot();
        var dependency = GetDependencySnapshot();
        WriteLine($"Status: {snapshot.ConnectionStatus}", ConsoleColor.White);
        WriteLine($"Message: {snapshot.StatusMessage}", ConsoleColor.Gray);
        WriteLine($"Progress: {snapshot.ConnectionProgress:P0}", ConsoleColor.Gray);
        WriteLine($"Connected: {snapshot.IsConnected}", ConsoleColor.Gray);
        WriteLine($"Current IP: {snapshot.CurrentIp}", ConsoleColor.Gray);
        WriteLine($"SOCKS: 127.0.0.1:{snapshot.SocksPort}", ConsoleColor.Gray);
        WriteLine($"HTTP: {(snapshot.HttpPort.HasValue ? $"127.0.0.1:{snapshot.HttpPort.Value}" : "disabled")}", ConsoleColor.Gray);
        WriteLine($"Dependencies: {(dependency.InProgress ? "downloading" : "idle")} ({dependency.Progress:P0}) {dependency.Status}", ConsoleColor.Gray);
    }

    private OnionHopClient.StatusUpdate GetStatusSnapshot()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    private OnionHopClient.DependencyUpdate GetDependencySnapshot()
    {
        lock (_statusLock)
        {
            return _dependency;
        }
    }

    private void OnStatusUpdated(OnionHopClient.StatusUpdate update)
    {
        var print = false;
        string statusKey;
        lock (_statusLock)
        {
            _status = update;
            statusKey = $"{update.ConnectionStatus}|{update.StatusMessage}|{(int)Math.Round(update.ConnectionProgress * 10)}|{update.IsConnected}|{update.IsConnecting}|{update.IsDisconnecting}|{update.CurrentIp}|{update.SocksPort}|{update.HttpPort?.ToString() ?? "-"}";
            if (!string.Equals(statusKey, _lastStatusPrintKey, StringComparison.Ordinal))
            {
                _lastStatusPrintKey = statusKey;
                print = true;
            }
        }

        if (!print)
        {
            return;
        }

        WriteLog("STATE", $"{update.ConnectionStatus} ({update.ConnectionProgress:P0}) - {update.StatusMessage}", ConsoleColor.DarkCyan);
    }

    private void OnDependencyUpdated(OnionHopClient.DependencyUpdate update)
    {
        lock (_statusLock)
        {
            _dependency = update;
        }

        if (update.InProgress)
        {
            WriteLog("DEPS", $"{update.Status} ({update.Progress:P0})", ConsoleColor.DarkMagenta);
        }
    }

    private void WriteLog(string prefix, string message, ConsoleColor color)
    {
        WriteLine($"[{DateTime.Now:HH:mm:ss}] [{prefix}] {message}", color);
    }

    private void WriteLine(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
    }

    private void WriteInline(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = previous;
        }
    }

    private static async Task WaitUntilCanceledAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ConnectRequest ParseConnectRequest(IReadOnlyList<string> args, bool holdDefault)
    {
        var parsed = ParseOptions(args);
        var mode = GetOption(parsed.Options, "mode", "proxy");
        var smartConnect = GetBooleanOption(parsed.Options, "smart", true);
        var hold = GetBooleanOption(parsed.Options, "hold", holdDefault);
        var useBridges = GetBooleanOption(parsed.Options, "bridges", false);
        var bridgeTypeProvided = parsed.Options.ContainsKey("bridge-type");
        var bridgeType = GetOption(parsed.Options, "bridge-type", "automatic");
        var useCensoredMode = GetNullableBooleanOption(parsed.Options, "censored");
        var tunCore = GetOption(parsed.Options, "tun-core", "sing-box");
        var proxyScope = GetOption(parsed.Options, "proxy-scope", "socks");
        var dnsProvider = GetOption(parsed.Options, "dns", "auto");
        var onionDnsProxy = GetBooleanOption(parsed.Options, "onion-dns-proxy", false);
        var useSnowflakeAmp = GetBooleanOption(parsed.Options, "snowflake-amp", false);
        var exitLocation = NormalizeLocationSelection(GetOptionAny(
            parsed.Options,
            OnionHopConnectOptions.AutomaticLocationLabel,
            "exit",
            "exit-country",
            "location",
            "country"));
        var entryLocation = NormalizeLocationSelection(GetOptionAny(
            parsed.Options,
            OnionHopConnectOptions.AutomaticLocationLabel,
            "entry",
            "entry-country"));
        var exitNodeFingerprint = NormalizeExitNodeFingerprint(GetOptionAny(
            parsed.Options,
            string.Empty,
            "exit-fingerprint",
            "fingerprint"));
        var strictExitFingerprint = GetBooleanOption(parsed.Options, "strict-exit-fingerprint", true);

        return new ConnectRequest(
            Mode: mode,
            SmartConnect: smartConnect,
            Hold: hold,
            UseBridges: useBridges,
            BridgeType: bridgeType,
            BridgeTypeProvided: bridgeTypeProvided,
            UseCensoredMode: useCensoredMode,
            TunCore: tunCore,
            ProxyScope: proxyScope,
            DnsProvider: dnsProvider,
            UseOnionDnsProxy: onionDnsProxy,
            UseSnowflakeAmp: useSnowflakeAmp,
            ExitLocation: exitLocation,
            EntryLocation: entryLocation,
            ExitNodeFingerprint: exitNodeFingerprint,
            StrictManualExitNodeFingerprint: strictExitFingerprint);
    }

    private static ParsedOptions ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(token);
                continue;
            }

            var span = token.AsSpan(2);
            var equalsIndex = span.IndexOf('=');
            if (equalsIndex >= 0)
            {
                var key = span[..equalsIndex].ToString();
                var value = span[(equalsIndex + 1)..].ToString();
                options[key] = value;
                continue;
            }

            var keyNoValue = span.ToString();
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[keyNoValue] = args[i + 1];
                i++;
            }
            else
            {
                options[keyNoValue] = "true";
            }
        }

        return new ParsedOptions(positional, options);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return tokens;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string GetOption(IReadOnlyDictionary<string, string> options, string key, string fallback)
    {
        return options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
    }

    private static string GetOptionAny(IReadOnlyDictionary<string, string> options, string fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback;
    }

    private static string NormalizeLocationSelection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return OnionHopConnectOptions.AutomaticLocationLabel;
        }

        var trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "auto" => OnionHopConnectOptions.AutomaticLocationLabel,
            "automatic" => OnionHopConnectOptions.AutomaticLocationLabel,
            "any" => OnionHopConnectOptions.AutomaticLocationLabel,
            "*" => OnionHopConnectOptions.AutomaticLocationLabel,
            _ => trimmed
        };
    }

    private static string? NormalizeExitNodeFingerprint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static bool GetBooleanOption(IReadOnlyDictionary<string, string> options, string key, bool fallback)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return ParseBoolean(raw, fallback);
    }

    private static bool? GetNullableBooleanOption(IReadOnlyDictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return ParseBoolean(raw, fallback: false);
    }

    private static bool ParseBoolean(string raw, bool fallback)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" => true,
            "0" => false,
            "true" => true,
            "false" => false,
            "on" => true,
            "off" => false,
            "yes" => true,
            "no" => false,
            _ => fallback
        };
    }

    private static bool IsValidExitNodeFingerprint(string value)
    {
        if (value.Length != 40)
        {
            return false;
        }

        foreach (var c in value)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var core = informationalVersion.Split('+', 2, StringSplitOptions.TrimEntries)[0];
            if (!string.IsNullOrWhiteSpace(core))
            {
                return core;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private enum CommandResult
    {
        Success,
        Failure,
        Exit
    }

    private sealed record ParsedOptions(
        IReadOnlyList<string> Positional,
        IReadOnlyDictionary<string, string> Options);

    private sealed record ConnectRequest(
        string Mode,
        bool SmartConnect,
        bool Hold,
        bool UseBridges,
        string BridgeType,
        bool BridgeTypeProvided,
        bool? UseCensoredMode,
        string TunCore,
        string ProxyScope,
        string DnsProvider,
        bool UseOnionDnsProxy,
        bool UseSnowflakeAmp,
        string ExitLocation,
        string EntryLocation,
        string? ExitNodeFingerprint,
        bool StrictManualExitNodeFingerprint);
}
