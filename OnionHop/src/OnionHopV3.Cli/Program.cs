using System.Net;
using System.Reflection;
using System.Text;
using OnionHopV3.Core;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Platform;
using OnionHopV3.Core.Services;

namespace OnionHopV3.Cli;

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

            host.WriteWarning("Second Ctrl+C - forcing exit.");
            Environment.Exit(130);
        };

        try
        {
            if (args.Length == 0)
            {
                host.PrintBanner();
                host.PrintQuickStart();
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
    private readonly SettingsService _settingsService = new();
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
        CurrentIp: "--",
        SocksPort: OnionHopClient.DefaultSocksPort,
        HttpPort: null);
    private OnionHopClient.DependencyUpdate _dependency = new(false, "Idle", 0);
    private DateTimeOffset? _connectedSinceUtc;
    private ConnectRequest? _lastConnectRequest;

    // Output verbosity. By default the firehose engine/Tor log is hidden; `logs on` shows it.
    private bool _showLogs;
    private bool _jsonMode;

    public CliHost()
    {
        _client.Log += (_, message) => { if (_showLogs) WriteLog("tor", message, ConsoleColor.DarkGray); };
        _client.DnsLog += (_, message) => { if (_showLogs) WriteLog("dns", message, ConsoleColor.DarkYellow); };
        _client.VpnLog += (_, message) => { if (_showLogs) WriteLog("vpn", message, ConsoleColor.DarkGray); };
        _client.DependencyUpdated += (_, update) => OnDependencyUpdated(update);
        _client.StatusUpdated += (_, update) => OnStatusUpdated(update);
        _client.LoadCachedBridgeMetadata();
    }

    // ----- Banner / help ----------------------------------------------------------------------

    public void PrintBanner()
    {
        var version = ResolveVersion();
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine(@"   ___       _          _  _", ConsoleColor.Magenta);
        WriteLine(@"  / _ \ _ _ (_)___ _ _ | || |___ _ __", ConsoleColor.Magenta);
        WriteLine(@" | (_) | ' \| / _ \ ' \| __ / _ \ '_ \", ConsoleColor.Magenta);
        WriteLine(@"  \___/|_||_|_\___/_||_|_||_\___/ .__/", ConsoleColor.Magenta);
        WriteLine(@"                                |_|    command line", ConsoleColor.DarkMagenta);
        WriteLine($"  OnionHop CLI v{version}  -  route traffic through Tor", ConsoleColor.Cyan);
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    public void PrintQuickStart()
    {
        WriteLine("Quick start:", ConsoleColor.White);
        WriteHint("  connect", "smart-connect to Tor (auto-picks the best route)");
        WriteHint("  status", "show the live connection dashboard");
        WriteHint("  watch", "continuously monitor the connection");
        WriteHint("  scan obfs4", "find which obfs4 bridges are reachable from here");
        WriteHint("  help", "full command reference");
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    public void PrintHelp(string? topic = null)
    {
        topic = topic?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(topic) && topic != "all")
        {
            PrintHelpTopic(topic);
            return;
        }

        WriteSection("Connection");
        WriteHint("  connect [options]", "connect to Tor (Smart Connect on by default)");
        WriteHint("  disconnect", "disconnect");
        WriteHint("  status", "connection dashboard");
        WriteHint("  watch [secs]", "live monitor, refreshing every N seconds (default 2)");
        WriteHint("  ip", "refresh and show the current public IP");
        WriteHint("  newnym", "request a fresh Tor circuit / new identity");
        WriteHint("  exit-country <cc|auto>", "change exit country on the live connection");
        WriteHint("  proxy <on|off>", "toggle the system proxy without dropping Tor");

        WriteSection("Bridges & censorship");
        WriteHint("  bridges", "list available bridge types");
        WriteHint("  scan <type> [count]", "probe which bridges of a type are reachable here");
        WriteHint("  reachability", "race all bridge transports and rank by reachability");
        WriteHint("  update-bridges", "refresh bridge data from the collector / bridge service");
        WriteHint("  plan [options]", "show the Smart Connect strategy plan for your network");

        WriteSection("Relays & countries");
        WriteHint("  countries [filter]", "list exit/entry country options with relay counts");

        WriteSection("Snowflake (volunteer)");
        WriteHint("  snowflake start [cap]", "run a Snowflake proxy to help censored users");
        WriteHint("  snowflake stop", "stop the Snowflake proxy");
        WriteHint("  snowflake status", "show Snowflake proxy stats");

        WriteSection("Config & misc");
        WriteHint("  config", "show saved default connect options");
        WriteHint("  config save", "save the last connect options as defaults");
        WriteHint("  deps", "download/verify Tor + transport dependencies");
        WriteHint("  logs <on|off>", "show or hide the live engine/Tor log stream");
        WriteHint("  json <on|off>", "machine-readable output for status/plan/scan");
        WriteHint("  clear", "clear the screen");
        WriteHint("  version", "print the CLI version");
        WriteHint("  exit | quit", "leave the CLI");

        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine("Connect options (also accepted by `plan`):", ConsoleColor.White);
        WriteHint("  --smart <on|off>", "Smart Connect (default on)");
        WriteHint("  --mode <proxy|tun>", "connection mode (default proxy)");
        WriteHint("  --engine <auto|classic|artihop>", "Tor engine (artihop = fast 2-hop)");
        WriteHint("  --bridges <on|off>", "force bridges (manual mode)");
        WriteHint("  --bridge-type <type>", "automatic, obfs4, snowflake, webtunnel, conjure, dnstt, meek-azure, vanilla, custom");
        WriteHint("  --bridge-source <auto|online|collector|offline>", "where bridges come from");
        WriteHint("  --exit <auto|cc>", "exit country, e.g. us, de");
        WriteHint("  --entry <auto|cc>", "entry country (ignored with bridges)");
        WriteHint("  --exit-fingerprint <hex40>", "pin an exit relay");
        WriteHint("  --dns <auto|cloudflare|quad9|adguard|mullvad|opendns|google|custom>", "DoH provider");
        WriteHint("  --onion-dns-proxy <on|off>", "enable the .onion DNS proxy listener");
        WriteHint("  --hold <on|off>", "keep running after connect (non-interactive)");
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine("Examples:", ConsoleColor.White);
        WriteHint("  connect --engine artihop", "fast 2-hop connect");
        WriteHint("  connect --mode tun --bridge-type snowflake", "TUN mode over snowflake");
        WriteHint("  scan obfs4 200", "probe up to 200 obfs4 bridges");
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    private void PrintHelpTopic(string topic)
    {
        switch (topic)
        {
            case "connect":
                WriteLine("connect [options] - connect to Tor.", ConsoleColor.White);
                WriteLine("  Smart Connect (default) analyzes your network/country and auto-picks the", ConsoleColor.Gray);
                WriteLine("  best route, escalating to bridges if direct is blocked. Use --smart off for", ConsoleColor.Gray);
                WriteLine("  manual control. See `help` for the full option list.", ConsoleColor.Gray);
                break;
            case "scan":
                WriteLine("scan <type> [count] - TCP-probe bridges to see which are reachable here.", ConsoleColor.White);
                WriteLine("  Example: scan obfs4 150   (probes up to 150 obfs4 bridges, fastest first).", ConsoleColor.Gray);
                break;
            case "snowflake":
                WriteLine("snowflake start|stop|status [capacity] - volunteer as a Snowflake proxy.", ConsoleColor.White);
                WriteLine("  Relays censored users' traffic into Tor over WebRTC, independent of your own", ConsoleColor.Gray);
                WriteLine("  connection. capacity caps concurrent clients (0 = unlimited).", ConsoleColor.Gray);
                break;
            default:
                WriteWarning($"No detailed help for '{topic}'. Type `help` for the full reference.");
                break;
        }
    }

    // ----- REPL / arg execution ---------------------------------------------------------------

    public async Task RunInteractiveAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            WriteInline(PromptText(), ConnectedColor());
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

            CommandResult result;
            try
            {
                result = await ExecuteCommandAsync(tokens, isInteractive: true, token);
            }
            catch (OperationCanceledException)
            {
                WriteInfo("Canceled.");
                continue;
            }
            catch (Exception ex)
            {
                WriteWarning($"Error: {ex.Message}");
                continue;
            }

            if (result == CommandResult.Exit)
            {
                break;
            }
        }
    }

    public async Task<int> ExecuteArgsAsync(string[] args, CancellationToken token)
    {
        var result = await ExecuteCommandAsync(args.ToList(), isInteractive: false, token);
        return result switch
        {
            CommandResult.Success => 0,
            CommandResult.Exit => 0,
            _ => 1
        };
    }

    private async Task<CommandResult> ExecuteCommandAsync(IReadOnlyList<string> tokens, bool isInteractive, CancellationToken token)
    {
        var command = tokens[0].Trim().ToLowerInvariant();
        var rest = tokens.Skip(1).ToList();

        switch (command)
        {
            case "help":
            case "?":
                PrintHelp(rest.Count > 0 ? rest[0] : null);
                return CommandResult.Success;

            case "connect":
            case "c":
            {
                var request = ParseConnectRequest(rest, holdDefault: !isInteractive);
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
            case "d":
                await DisconnectAsync();
                return CommandResult.Success;

            case "status":
            case "s":
                await PrintStatusAsync(token);
                return CommandResult.Success;

            case "watch":
            case "w":
                await WatchAsync(rest, token);
                return CommandResult.Success;

            case "ip":
            {
                var before = GetStatusSnapshot();
                WriteInfo("Refreshing IP...");
                await _client.RefreshIpAsync(updateStatusMessage: true, token);
                var after = GetStatusSnapshot();
                WriteInfo(string.Equals(before.CurrentIp, after.CurrentIp, StringComparison.OrdinalIgnoreCase)
                    ? $"IP unchanged: {after.CurrentIp}"
                    : $"IP updated: {before.CurrentIp} -> {after.CurrentIp}");
                return CommandResult.Success;
            }

            case "newnym":
            case "identity":
            case "n":
            {
                WriteInfo("Requesting a new circuit...");
                await _client.ChangeIdentityAsync(token);
                WriteInfo(GetStatusSnapshot().StatusMessage);
                return CommandResult.Success;
            }

            case "exit-country":
            case "exitcountry":
            {
                if (rest.Count == 0) { WriteWarning("Usage: exit-country <cc|auto>"); return CommandResult.Failure; }
                var cc = NormalizeLocationSelection(rest[0]);
                var arg = string.Equals(cc, OnionHopConnectOptions.AutomaticLocationLabel, StringComparison.Ordinal) ? null : cc;
                WriteInfo($"Changing exit country to {rest[0]}...");
                await _client.ChangeExitCountryAsync(arg, token);
                WriteInfo(GetStatusSnapshot().StatusMessage);
                return CommandResult.Success;
            }

            case "proxy":
            {
                if (rest.Count == 0) { WriteWarning("Usage: proxy <on|off>"); return CommandResult.Failure; }
                var enable = ParseBoolean(rest[0], false);
                if (!_client.CanToggleSystemProxy)
                {
                    WriteWarning("System proxy can only be toggled while connected in a system-scope proxy mode.");
                    return CommandResult.Failure;
                }
                _client.SetSystemProxyEnabled(enable);
                WriteInfo($"System proxy {(_client.IsSystemProxyEnabled ? "ON" : "OFF")}.");
                return CommandResult.Success;
            }

            case "bridges":
            case "bridge-types":
                PrintBridgeTypes();
                return CommandResult.Success;

            case "scan":
                await ScanBridgesAsync(rest, token);
                return CommandResult.Success;

            case "reachability":
            case "race":
                await ReachabilityAsync(token);
                return CommandResult.Success;

            case "update-bridges":
            case "refresh-bridges":
                await UpdateBridgesAsync(token);
                return CommandResult.Success;

            case "countries":
            case "country":
            case "locations":
                await ShowCountriesAsync(rest, token);
                return CommandResult.Success;

            case "snowflake":
            case "sf":
                await SnowflakeAsync(rest, token);
                return CommandResult.Success;

            case "plan":
            {
                var request = ParseConnectRequest(rest, holdDefault: false);
                await ShowPlanAsync(request, token);
                return CommandResult.Success;
            }

            case "config":
                HandleConfig(rest);
                return CommandResult.Success;

            case "deps":
            case "dependencies":
            {
                WriteInfo("Ensuring dependencies...");
                var ok = await _client.EnsureDependenciesAsync(token);
                if (ok) { WriteInfo("Dependencies are ready."); return CommandResult.Success; }
                WriteWarning("Dependency ensure failed. See logs (`logs on`).");
                return CommandResult.Failure;
            }

            case "logs":
                _showLogs = rest.Count > 0 ? ParseBoolean(rest[0], !_showLogs) : !_showLogs;
                WriteInfo($"Engine/Tor log stream {(_showLogs ? "ON" : "OFF")}.");
                return CommandResult.Success;

            case "json":
                _jsonMode = rest.Count > 0 ? ParseBoolean(rest[0], !_jsonMode) : !_jsonMode;
                WriteInfo($"JSON output {(_jsonMode ? "ON" : "OFF")}.");
                return CommandResult.Success;

            case "clear":
            case "cls":
                Console.Clear();
                return CommandResult.Success;

            case "version":
            case "-v":
            case "--version":
                WriteLine($"OnionHop CLI {ResolveVersion()}", ConsoleColor.White);
                return CommandResult.Success;

            case "quit":
            case "exit":
            case "q":
                return CommandResult.Exit;

            default:
                WriteWarning($"Unknown command: {command}");
                WriteInfo("Type `help` for the command reference.");
                return CommandResult.Failure;
        }
    }

    // ----- Connect ----------------------------------------------------------------------------

    private async Task<bool> ConnectAsync(ConnectRequest request, CancellationToken token)
    {
        var snapshot = GetStatusSnapshot();
        if (snapshot.IsConnected || snapshot.IsConnecting)
        {
            WriteWarning("Already connected/connecting.");
            return snapshot.IsConnected;
        }

        if (!string.IsNullOrWhiteSpace(request.ExitNodeFingerprint) && !IsValidExitNodeFingerprint(request.ExitNodeFingerprint))
        {
            WriteWarning("Invalid --exit-fingerprint. Expected 40 hex characters.");
            return false;
        }

        var baseOptions = BuildBaseOptions(request);
        IReadOnlyList<SmartConnectAdvisor.Strategy> strategies;

        if (request.SmartConnect)
        {
            WriteInfo("Smart Connect: analyzing your network...");
            SmartConnectAdvisor.Plan plan;
            try
            {
                plan = await _advisor.BuildPlanAsync(baseOptions, m => { if (_showLogs) WriteLog("smart", m, ConsoleColor.Blue); }, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                WriteWarning($"Smart Connect planning failed: {ex.Message}");
                plan = new SmartConnectAdvisor.Plan(null, null, SmartConnectAdvisor.RiskLevel.Unknown, 0, 0, 0, 0, null, []);
            }

            if (!string.IsNullOrWhiteSpace(plan.CountryCode))
            {
                WriteInfo($"Network: {plan.CountryCode}  risk={plan.Risk}  (score {plan.RestrictionScore:0.00})");
            }

            strategies = plan.Strategies.Count > 0
                ? plan.Strategies
                : SmartConnectAdvisor.BuildStrategiesForRisk(baseOptions, SmartConnectAdvisor.RiskLevel.Unknown);
        }
        else
        {
            strategies = [new SmartConnectAdvisor.Strategy("manual", "Smart Connect disabled.", baseOptions)];
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
                WriteWarning(".onion DNS proxy needs elevation; continuing without it.");
            }

            if (request.SmartConnect)
            {
                WriteInfo($"[{i + 1}/{strategies.Count}] {strategy.Name}: {strategy.Reason}");
            }

            if (!await EnsureAdminRequirementsForConnectAsync(attemptOptions, token))
            {
                return false;
            }

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var spinner = new Spinner(this, $"Connecting via {strategy.Name}");
            try
            {
                await _client.ConnectAsync(attemptOptions, _connectCts.Token);
            }
            catch (OperationCanceledException) { spinner.Stop(); throw; }
            catch (Exception ex)
            {
                lastError = ex;
                spinner.Stop();
                WriteWarning($"Attempt failed: {Shorten(ex.Message)}");
            }

            spinner.Stop();
            snapshot = GetStatusSnapshot();
            if (snapshot.IsConnected)
            {
                _connectedSinceUtc = DateTimeOffset.UtcNow;
                _lastConnectRequest = request;
                WriteSuccess($"Connected.  IP {snapshot.CurrentIp}   SOCKS 127.0.0.1:{snapshot.SocksPort}");
                return true;
            }
        }

        WriteWarning(lastError != null ? $"Connection failed: {Shorten(lastError.Message)}" : "Connection failed: no strategy succeeded.");
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
                WriteInfo("TUN mode: verifying elevated helper...");
                if (!await _client.EnsureAdminHelperAsync())
                {
                    WriteWarning("Admin helper unavailable. Start the terminal as Administrator for TUN mode.");
                    return false;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (_client.CanUseMacNetworkExtension())
                {
                    WriteInfo("Using configured macOS Network Extension for TUN (no sudo needed).");
                    return true;
                }
                WriteWarning("TUN on macOS needs root or a configured Network Extension. Re-run with sudo.");
                return false;
            }
            else
            {
                WriteWarning("TUN mode needs root on this platform. Re-run with sudo.");
                return false;
            }
        }

        return true;
    }

    private async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        await _client.DisconnectAsync();
        _connectedSinceUtc = null;
        WriteInfo("Disconnected.");
    }

    // ----- Bridges / scanner ------------------------------------------------------------------

    private void PrintBridgeTypes()
    {
        var types = _client.GetBridgeTypes();
        var recommended = _client.GetRecommendedBridgeType();
        WriteSection("Bridge types");
        foreach (var t in types)
        {
            var note = string.Equals(t, recommended, StringComparison.OrdinalIgnoreCase) ? "  (recommended)" : string.Empty;
            WriteLine($"  {t}{note}", ConsoleColor.Gray);
        }
        var last = _client.GetLastBridgeDataUpdateUtc();
        WriteLine(last.HasValue ? $"  bridge data last updated: {last.Value.ToLocalTime():yyyy-MM-dd HH:mm}" : "  bridge data: not refreshed yet", ConsoleColor.DarkGray);
    }

    private async Task ScanBridgesAsync(IReadOnlyList<string> rest, CancellationToken token)
    {
        if (rest.Count == 0)
        {
            WriteWarning("Usage: scan <bridge-type> [max-count].  e.g. scan obfs4 150");
            return;
        }

        var bridgeType = NormalizeBridgeType(rest[0]);
        var max = rest.Count > 1 && int.TryParse(rest[1], out var m) ? Math.Clamp(m, 1, 1000) : 200;

        WriteInfo($"Fetching {bridgeType} bridges...");
        var options = BuildBaseOptions(DefaultRequest() with { UseBridges = true, BridgeType = bridgeType, SmartConnect = false });

        IReadOnlyList<string> lines;
        try
        {
            lines = await _client.GetBridgeLinesForTypeAsync(options, token);
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not fetch {bridgeType} bridges: {Shorten(ex.Message)}");
            return;
        }

        if (lines.Count == 0)
        {
            WriteWarning($"No {bridgeType} bridges available to scan.");
            return;
        }

        var candidates = lines.Take(max).ToList();
        WriteInfo($"Probing {candidates.Count} {bridgeType} bridge(s)...");

        var working = 0;
        var done = 0;
        var progress = new Progress<BridgeScanResult>(r =>
        {
            done++;
            if (r.IsWorking) working++;
            if (done % 10 == 0 || done == candidates.Count)
            {
                WriteInline($"\r  scanned {done}/{candidates.Count}  reachable {working}   ", ConsoleColor.DarkCyan);
            }
        });

        var results = await BridgeScanService.ScanAsync(candidates, 24, TimeSpan.FromSeconds(4), progress, token);
        WriteLine(string.Empty, ConsoleColor.Gray);

        var reachable = results.Where(r => r.IsWorking)
            .OrderBy(r => r.PingMs ?? int.MaxValue)
            .ToList();

        if (_jsonMode)
        {
            WriteLine($"{{\"type\":\"{bridgeType}\",\"scanned\":{results.Count},\"reachable\":{reachable.Count}}}", ConsoleColor.Gray);
        }

        WriteSuccess($"{reachable.Count}/{results.Count} {bridgeType} bridges reachable from here (fastest first):");
        foreach (var r in reachable.Take(15))
        {
            var ping = r.PingMs.HasValue ? $"{r.PingMs,5} ms" : "fronted";
            WriteLine($"  {ping}  {r.Host}:{r.Port}", ConsoleColor.Gray);
        }
        if (reachable.Count > 15)
        {
            WriteLine($"  ... and {reachable.Count - 15} more.", ConsoleColor.DarkGray);
        }
        if (reachable.Count == 0)
        {
            WriteWarning("None reachable - this transport may be blocked on your network.");
        }
    }

    private async Task ReachabilityAsync(CancellationToken token)
    {
        var probeable = _client.GetBridgeTypes()
            .Where(t => OnionHopClient.BridgeTypeHasProbeableEndpoint(t)
                        && !OnionHopClient.IsAutomaticBridgeType(t)
                        && t != "custom" && t != "vanilla")
            .ToList();
        if (probeable.Count == 0)
        {
            WriteWarning("No probeable bridge transports available.");
            return;
        }

        WriteInfo($"Racing reachability across: {string.Join(", ", probeable)} ...");
        var options = BuildBaseOptions(DefaultRequest() with { UseBridges = true, SmartConnect = false });

        var map = await _client.ProbeTransportReachabilityAsync(options, probeable, TimeSpan.FromSeconds(12), token);
        WriteSection("Reachability (best first)");
        foreach (var kv in map.OrderByDescending(k => k.Value.ReachableCount).ThenBy(k => k.Value.FastestPingMs ?? int.MaxValue))
        {
            var ping = kv.Value.FastestPingMs.HasValue ? $"fastest {kv.Value.FastestPingMs} ms" : "n/a";
            var color = kv.Value.ReachableCount > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray;
            WriteLine($"  {kv.Key,-10} {kv.Value.ReachableCount,4} reachable   {ping}", color);
        }
        if (map.Count == 0)
        {
            WriteWarning("Reachability probe returned nothing (network or bridge data unavailable).");
        }
    }

    private async Task UpdateBridgesAsync(CancellationToken token)
    {
        WriteInfo("Refreshing bridge data (collector / bridge service)...");
        var options = BuildBaseOptions(_lastConnectRequest ?? DefaultRequest());
        var spinner = new Spinner(this, "Updating bridges");
        try
        {
            var status = await _client.RefreshBridgeDistributionAsync(options, token);
            spinner.Stop();
            if (status.UpdatedTypes > 0)
            {
                WriteSuccess($"Updated {status.UpdatedTypes}/{status.AttemptedTypes} bridge type(s).");
            }
            else
            {
                WriteWarning($"No new bridges fetched ({status.AttemptedTypes} type(s) attempted). Try `logs on` for details.");
            }
        }
        catch (Exception ex)
        {
            spinner.Stop();
            WriteWarning($"Bridge refresh failed: {Shorten(ex.Message)}");
        }
    }

    // ----- Countries / plan -------------------------------------------------------------------

    private async Task ShowCountriesAsync(IReadOnlyList<string> rest, CancellationToken token)
    {
        WriteInfo("Loading country/relay data...");
        var countries = await _client.GetCountryStatsAsync(token).ConfigureAwait(false);
        if (countries.Count == 0)
        {
            WriteWarning("No country data available right now.");
            return;
        }

        var filter = rest.Count > 0 ? rest[0].Trim().ToLowerInvariant() : null;
        var rows = countries
            .Where(c => filter == null || c.CountryCode.ToLowerInvariant().Contains(filter) || c.CountryName.ToLowerInvariant().Contains(filter))
            .OrderByDescending(c => c.ExitNodes)
            .ToList();

        WriteSection($"Countries ({rows.Count})  -  use as --exit / --entry");
        WriteLine($"  {"cc",-2}  {"country",-26} {"entry",6} {"exit",6}", ConsoleColor.DarkGray);
        foreach (var c in rows)
        {
            WriteLine($"  {c.CountryCode.ToUpperInvariant(),-2}  {Trunc(c.CountryName, 26),-26} {c.EntryNodes,6} {c.ExitNodes,6}", ConsoleColor.Gray);
        }
    }

    private async Task ShowPlanAsync(ConnectRequest request, CancellationToken token)
    {
        WriteInfo("Building Smart Connect plan...");
        var baseOptions = BuildBaseOptions(request);
        var plan = await _advisor.BuildPlanAsync(baseOptions, null, token);

        if (_jsonMode)
        {
            WriteLine($"{{\"country\":\"{plan.CountryCode}\",\"risk\":\"{plan.Risk}\",\"score\":{plan.RestrictionScore:0.000},\"strategies\":[{string.Join(",", plan.Strategies.Select(s => $"\"{s.Name}\""))}]}}", ConsoleColor.Gray);
            return;
        }

        WriteSection("Smart Connect plan");
        WriteLine($"  Country : {plan.CountryCode ?? "unknown"}", ConsoleColor.Gray);
        WriteLine($"  IP      : {plan.PublicIp ?? "unknown"}", ConsoleColor.Gray);
        WriteLine($"  Risk    : {plan.Risk}  (score {plan.RestrictionScore:0.000})", RiskColor(plan.Risk));
        if (plan.Strategies.Count == 0) { WriteWarning("  No strategies available."); return; }
        WriteLine("  Strategy order:", ConsoleColor.White);
        for (var i = 0; i < plan.Strategies.Count; i++)
        {
            WriteLine($"    {i + 1}. {plan.Strategies[i].Name,-18} {plan.Strategies[i].Reason}", ConsoleColor.Gray);
        }
    }

    // ----- Snowflake --------------------------------------------------------------------------

    private async Task SnowflakeAsync(IReadOnlyList<string> rest, CancellationToken token)
    {
        var sub = rest.Count > 0 ? rest[0].Trim().ToLowerInvariant() : "status";
        switch (sub)
        {
            case "start":
            {
                uint cap = rest.Count > 1 && uint.TryParse(rest[1], out var c) ? c : 0;
                WriteInfo($"Starting Snowflake proxy (capacity {(cap == 0 ? "unlimited" : cap.ToString())})...");
                var ok = await _client.StartSnowflakeProxyAsync(cap, token);
                WriteInfo(ok ? "Snowflake proxy started. You're now helping censored users reach Tor." : "Snowflake proxy failed to start (see `logs on`).");
                break;
            }
            case "stop":
                _client.StopSnowflakeProxy();
                WriteInfo("Snowflake proxy stopped.");
                break;
            default:
            {
                var st = _client.GetSnowflakeProxyStatus();
                WriteSection("Snowflake proxy");
                WriteLine($"  Running     : {st.IsRunning}", st.IsRunning ? ConsoleColor.Green : ConsoleColor.DarkGray);
                WriteLine($"  NAT type    : {st.NatType}", ConsoleColor.Gray);
                WriteLine($"  Connections : {st.ConnectionsServed}", ConsoleColor.Gray);
                WriteLine($"  Traffic     : {st.TrafficSummary}", ConsoleColor.Gray);
                if (!string.IsNullOrWhiteSpace(st.Message)) WriteLine($"  {st.Message}", ConsoleColor.DarkGray);
                break;
            }
        }
    }

    // ----- Config -----------------------------------------------------------------------------

    private void HandleConfig(IReadOnlyList<string> rest)
    {
        var sub = rest.Count > 0 ? rest[0].Trim().ToLowerInvariant() : "show";
        if (sub == "save")
        {
            var req = _lastConnectRequest ?? DefaultRequest();
            var s = _settingsService.Load() ?? new UserSettings();
            var opts = BuildBaseOptions(req);
            s.SelectedConnectionMode = opts.SelectedConnectionMode;
            s.UseTorBridges = opts.UseTorBridges;
            s.SelectedBridgeType = opts.SelectedBridgeType;
            s.BridgeSourceMode = opts.BridgeSourceMode;
            s.SelectedLocation = opts.SelectedLocation;
            s.SelectedEntryLocation = opts.SelectedEntryLocation;
            s.TorEngineMode = opts.TorEngineMode;
            s.SelectedDnsProvider = opts.SelectedDnsProvider;
            s.ProxyScopeMode = opts.ProxyScopeMode;
            _settingsService.Save(s);
            WriteInfo("Saved current connect options as defaults.");
            return;
        }

        var settings = _settingsService.Load();
        WriteSection("Saved defaults");
        if (settings == null)
        {
            WriteLine("  (none saved - using built-in defaults)", ConsoleColor.DarkGray);
            return;
        }
        WriteLine($"  mode        : {settings.SelectedConnectionMode ?? "proxy"}", ConsoleColor.Gray);
        WriteLine($"  engine      : {settings.TorEngineMode ?? "automatic"}", ConsoleColor.Gray);
        WriteLine($"  bridges     : {(settings.UseTorBridges ? "on" : "off")}  ({settings.SelectedBridgeType ?? "automatic"})", ConsoleColor.Gray);
        WriteLine($"  bridge src  : {settings.BridgeSourceMode ?? "auto"}", ConsoleColor.Gray);
        WriteLine($"  exit/entry  : {settings.SelectedLocation ?? "auto"} / {settings.SelectedEntryLocation ?? "auto"}", ConsoleColor.Gray);
        WriteLine($"  dns         : {settings.SelectedDnsProvider ?? "auto"}", ConsoleColor.Gray);
    }

    // ----- Status / watch dashboard -----------------------------------------------------------

    private async Task PrintStatusAsync(CancellationToken token)
    {
        await TryRefreshIpForStatusAsync(token).ConfigureAwait(false);
        PrintDashboard();
    }

    private async Task WatchAsync(IReadOnlyList<string> rest, CancellationToken token)
    {
        var seconds = rest.Count > 0 && int.TryParse(rest[0], out var s) ? Math.Clamp(s, 1, 60) : 2;
        WriteInfo($"Live monitor (every {seconds}s). Press Ctrl+C to stop.");
        try
        {
            while (!token.IsCancellationRequested)
            {
                long? read = null, written = null;
                var traffic = await _client.TryGetTorTrafficBytesAsync(token).ConfigureAwait(false);
                if (traffic.HasValue) { read = traffic.Value.BytesRead; written = traffic.Value.BytesWritten; }

                Console.Clear();
                PrintDashboard(read, written);
                WriteLine($"  (refreshing every {seconds}s - Ctrl+C to stop)", ConsoleColor.DarkGray);
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PrintDashboard(long? bytesRead = null, long? bytesWritten = null)
    {
        var s = GetStatusSnapshot();
        var dep = GetDependencySnapshot();
        var connColor = s.IsConnected ? ConsoleColor.Green : s.IsConnecting ? ConsoleColor.Yellow : ConsoleColor.DarkGray;

        if (_jsonMode)
        {
            WriteLine($"{{\"connected\":{s.IsConnected.ToString().ToLowerInvariant()},\"status\":\"{s.ConnectionStatus}\",\"ip\":\"{s.CurrentIp}\",\"socks\":{s.SocksPort}}}", ConsoleColor.Gray);
            return;
        }

        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine("  OnionHop", ConsoleColor.Magenta);
        WriteLine($"  {"State",-12}: {s.ConnectionStatus}", connColor);
        WriteLine($"  {"Message",-12}: {Trunc(s.StatusMessage, 60)}", ConsoleColor.Gray);
        if (s.IsConnecting)
        {
            WriteLine($"  {"Progress",-12}: {ProgressBar(s.ConnectionProgress)} {s.ConnectionProgress:P0}", ConsoleColor.Yellow);
        }
        WriteLine($"  {"Public IP",-12}: {s.CurrentIp}", s.IsConnected ? ConsoleColor.Cyan : ConsoleColor.Gray);
        WriteLine($"  {"SOCKS",-12}: 127.0.0.1:{s.SocksPort}", ConsoleColor.Gray);
        WriteLine($"  {"HTTP",-12}: {(s.HttpPort.HasValue ? $"127.0.0.1:{s.HttpPort.Value}" : "disabled")}", ConsoleColor.Gray);
        WriteLine($"  {"System proxy",-12}: {(_client.IsSystemProxyEnabled ? "ON" : "off")}", ConsoleColor.Gray);
        if (_connectedSinceUtc.HasValue && s.IsConnected)
        {
            var up = DateTimeOffset.UtcNow - _connectedSinceUtc.Value;
            WriteLine($"  {"Uptime",-12}: {FormatUptime(up)}", ConsoleColor.Gray);
        }
        if (bytesRead.HasValue || bytesWritten.HasValue)
        {
            WriteLine($"  {"Traffic",-12}: down {FormatBytes(bytesRead ?? 0)}   up {FormatBytes(bytesWritten ?? 0)}", ConsoleColor.Gray);
        }
        var sf = _client.GetSnowflakeProxyStatus();
        if (sf.IsRunning)
        {
            WriteLine($"  {"Snowflake",-12}: serving ({sf.ConnectionsServed} connections, {sf.NatType} NAT)", ConsoleColor.Green);
        }
        if (dep.InProgress)
        {
            WriteLine($"  {"Deps",-12}: {dep.Status} ({dep.Progress:P0})", ConsoleColor.DarkMagenta);
        }
        WriteLine(string.Empty, ConsoleColor.Gray);
    }

    private async Task TryRefreshIpForStatusAsync(CancellationToken token)
    {
        var snapshot = GetStatusSnapshot();
        if (snapshot.IsConnecting || snapshot.IsDisconnecting) return;
        if (IPAddress.TryParse(snapshot.CurrentIp?.Trim(), out _)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try { await _client.RefreshIpAsync(updateStatusMessage: false, cts.Token).ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    // ----- Option building --------------------------------------------------------------------

    private OnionHopConnectOptions BuildBaseOptions(ConnectRequest request)
    {
        var mode = request.Mode.ToLowerInvariant() == "tun"
            ? OnionHopConnectOptions.ConnectionModeTun
            : OnionHopConnectOptions.ConnectionModeProxy;

        var proxyScope = request.ProxyScope.ToLowerInvariant() switch
        {
            "system" => OnionHopConnectOptions.ProxyScopeSystem,
            "local" => OnionHopConnectOptions.ProxyScopeLocalOnly,
            _ => OnionHopConnectOptions.ProxyScopeSystemSocks
        };

        var bridgeType = NormalizeBridgeType(request.BridgeType);
        var useBridges = request.UseBridges || request.BridgeTypeProvided;
        var useCensoredMode = request.UseCensoredMode ?? useBridges;

        return new OnionHopConnectOptions
        {
            SelectedConnectionMode = mode,
            ProxyScopeMode = proxyScope,
            TorEngineMode = NormalizeEngine(request.Engine),
            TunCoreMode = NormalizeTunCoreMode(request.TunCore),
            SelectedLocation = request.ExitLocation,
            SelectedEntryLocation = request.EntryLocation,
            ExitNodeFingerprint = request.ExitNodeFingerprint,
            StrictManualExitNodeFingerprint = request.StrictManualExitNodeFingerprint,
            SelectedBridgeType = bridgeType,
            BridgeSourceMode = NormalizeBridgeSource(request.BridgeSource),
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

    private static ConnectRequest DefaultRequest() => new(
        "proxy", true, false, false, "automatic", false, null,
        "sing-box", "socks", "auto", false, false,
        OnionHopConnectOptions.AutomaticLocationLabel, OnionHopConnectOptions.AutomaticLocationLabel, null, true, "auto", "auto");

    private static string NormalizeBridgeType(string bridgeType) => bridgeType.Trim().ToLowerInvariant() switch
    {
        "obfs4" => "obfs4",
        "snowflake" => "snowflake",
        "conjure" => "conjure",
        "webtunnel" => "webtunnel",
        "dnstt" => "dnstt",
        "meek-azure" or "meek" => "meek-azure",
        "vanilla" => "vanilla",
        "custom" => "custom",
        _ => "automatic"
    };

    private static string NormalizeBridgeSource(string value) => value.Trim().ToLowerInvariant() switch
    {
        "online" or "moat" or "service" => OnionHopConnectOptions.BridgeSourceOnlineOnly,
        "collector" => OnionHopConnectOptions.BridgeSourceCollectorOnly,
        "offline" => OnionHopConnectOptions.BridgeSourceOfflineOnly,
        _ => OnionHopConnectOptions.BridgeSourceAuto
    };

    private static string NormalizeEngine(string value) => value.Trim().ToLowerInvariant() switch
    {
        "artihop" or "fast" or "2-hop" => OnionHopConnectOptions.TorEngineArtiHop,
        "classic" or "tor" => OnionHopConnectOptions.TorEngineClassic,
        "arti" => OnionHopConnectOptions.TorEngineArti,
        _ => OnionHopConnectOptions.TorEngineAutomatic
    };

    private static string NormalizeDnsProvider(string value) => value.Trim().ToLowerInvariant() switch
    {
        "cloudflare" => OnionHopConnectOptions.DnsProviderCloudflare,
        "quad9" => OnionHopConnectOptions.DnsProviderQuad9,
        "adguard" => OnionHopConnectOptions.DnsProviderAdGuard,
        "mullvad" => OnionHopConnectOptions.DnsProviderMullvad,
        "opendns" => OnionHopConnectOptions.DnsProviderOpenDns,
        "google" => OnionHopConnectOptions.DnsProviderGoogle,
        "custom" => OnionHopConnectOptions.DnsProviderCustom,
        _ => OnionHopConnectOptions.DnsProviderAuto
    };

    private static string NormalizeTunCoreMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "xray" => OnionHopConnectOptions.TunCoreXray,
        _ => OnionHopConnectOptions.TunCoreSingBox
    };

    // ----- Status events ----------------------------------------------------------------------

    private OnionHopClient.StatusUpdate GetStatusSnapshot() { lock (_statusLock) { return _status; } }
    private OnionHopClient.DependencyUpdate GetDependencySnapshot() { lock (_statusLock) { return _dependency; } }

    private void OnStatusUpdated(OnionHopClient.StatusUpdate update)
    {
        lock (_statusLock) { _status = update; }
        if (update.IsConnected && _connectedSinceUtc == null) _connectedSinceUtc = DateTimeOffset.UtcNow;
        if (!update.IsConnected && !update.IsConnecting) _connectedSinceUtc = null;
    }

    private void OnDependencyUpdated(OnionHopClient.DependencyUpdate update)
    {
        lock (_statusLock) { _dependency = update; }
        if (update.InProgress && _showLogs) WriteLog("deps", $"{update.Status} ({update.Progress:P0})", ConsoleColor.DarkMagenta);
    }

    // ----- Parsing ----------------------------------------------------------------------------

    private static ConnectRequest ParseConnectRequest(IReadOnlyList<string> args, bool holdDefault)
    {
        var o = ParseOptions(args);
        return new ConnectRequest(
            Mode: GetOption(o, "mode", "proxy"),
            SmartConnect: GetBool(o, "smart", true),
            Hold: GetBool(o, "hold", holdDefault),
            UseBridges: GetBool(o, "bridges", false),
            BridgeType: GetOption(o, "bridge-type", "automatic"),
            BridgeTypeProvided: o.ContainsKey("bridge-type"),
            UseCensoredMode: GetNullableBool(o, "censored"),
            TunCore: GetOption(o, "tun-core", "sing-box"),
            ProxyScope: GetOption(o, "proxy-scope", "socks"),
            DnsProvider: GetOption(o, "dns", "auto"),
            UseOnionDnsProxy: GetBool(o, "onion-dns-proxy", false),
            UseSnowflakeAmp: GetBool(o, "snowflake-amp", false),
            ExitLocation: NormalizeLocationSelection(GetAny(o, OnionHopConnectOptions.AutomaticLocationLabel, "exit", "exit-country", "location", "country")),
            EntryLocation: NormalizeLocationSelection(GetAny(o, OnionHopConnectOptions.AutomaticLocationLabel, "entry", "entry-country")),
            ExitNodeFingerprint: NormalizeExitNodeFingerprint(GetAny(o, string.Empty, "exit-fingerprint", "fingerprint")),
            StrictManualExitNodeFingerprint: GetBool(o, "strict-exit-fingerprint", true),
            Engine: GetOption(o, "engine", "auto"),
            BridgeSource: GetOption(o, "bridge-source", "auto"));
    }

    private static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;
            var span = token.AsSpan(2);
            var eq = span.IndexOf('=');
            if (eq >= 0) { options[span[..eq].ToString()] = span[(eq + 1)..].ToString(); continue; }
            var key = span.ToString();
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) { options[key] = args[++i]; }
            else { options[key] = "true"; }
        }
        return options;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return tokens;
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c)) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string GetOption(IReadOnlyDictionary<string, string> o, string key, string fallback)
        => o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static string GetAny(IReadOnlyDictionary<string, string> o, string fallback, params string[] keys)
    {
        foreach (var key in keys) if (o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return fallback;
    }

    private static string NormalizeLocationSelection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return OnionHopConnectOptions.AutomaticLocationLabel;
        var trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "auto" or "automatic" or "any" or "*" => OnionHopConnectOptions.AutomaticLocationLabel,
            _ => trimmed
        };
    }

    private static string? NormalizeExitNodeFingerprint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (t.StartsWith('$')) t = t[1..];
        return t.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> o, string key, bool fallback)
        => o.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? ParseBoolean(raw, fallback) : fallback;

    private static bool? GetNullableBool(IReadOnlyDictionary<string, string> o, string key)
        => o.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? ParseBoolean(raw, false) : null;

    private static bool ParseBoolean(string raw, bool fallback) => raw.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "yes" or "y" => true,
        "0" or "false" or "off" or "no" or "n" => false,
        _ => fallback
    };

    private static bool IsValidExitNodeFingerprint(string value)
    {
        if (value.Length != 40) return false;
        foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    // ----- Formatting helpers -----------------------------------------------------------------

    private string PromptText() => GetStatusSnapshot().IsConnected ? "onionhop [connected]> " : "onionhop> ";
    private ConsoleColor ConnectedColor() => GetStatusSnapshot().IsConnected ? ConsoleColor.Green : ConsoleColor.Cyan;

    private static ConsoleColor RiskColor(SmartConnectAdvisor.RiskLevel risk) => risk switch
    {
        SmartConnectAdvisor.RiskLevel.Open => ConsoleColor.Green,
        SmartConnectAdvisor.RiskLevel.Moderate => ConsoleColor.Yellow,
        SmartConnectAdvisor.RiskLevel.Restricted => ConsoleColor.DarkYellow,
        SmartConnectAdvisor.RiskLevel.Severe => ConsoleColor.Red,
        _ => ConsoleColor.Gray
    };

    private static string ProgressBar(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        const int width = 20;
        var filled = (int)Math.Round(fraction * width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static string FormatUptime(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    private static string FormatBytes(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string Shorten(string s)
    {
        var first = s.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? s;
        return Trunc(first, 120);
    }

    // ----- Console writers --------------------------------------------------------------------

    public void WriteInfo(string m) => WriteLog("•", m, ConsoleColor.Cyan);
    public void WriteWarning(string m) => WriteLog("!", m, ConsoleColor.Yellow);
    public void WriteSuccess(string m) => WriteLog("✓", m, ConsoleColor.Green);

    private void WriteSection(string title)
    {
        WriteLine(string.Empty, ConsoleColor.Gray);
        WriteLine($"  {title}", ConsoleColor.White);
        WriteLine($"  {new string('─', Math.Min(title.Length, 50))}", ConsoleColor.DarkGray);
    }

    private void WriteHint(string cmd, string desc)
    {
        lock (_consoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(cmd.PadRight(34));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(desc);
            Console.ForegroundColor = prev;
        }
    }

    public void WriteLog(string prefix, string message, ConsoleColor color)
        => WriteLine($"  {prefix} {message}", color);

    public void WriteLine(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = prev;
        }
    }

    public void WriteInline(string text, ConsoleColor color)
    {
        lock (_consoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }
    }

    private static async Task WaitUntilCanceledAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { }
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var core = info.Split('+', 2, StringSplitOptions.TrimEntries)[0];
            if (!string.IsNullOrWhiteSpace(core)) return core;
        }
        return assembly.GetName().Version?.ToString(3) ?? "3.0.0";
    }

    // ----- Lifecycle --------------------------------------------------------------------------

    public async Task ShutdownAsync()
    {
        try { _connectCts?.Cancel(); await _client.DisconnectAsync(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _connectCts?.Dispose();
        _client.Dispose();
    }

    // ----- Spinner ----------------------------------------------------------------------------

    private sealed class Spinner
    {
        private static readonly char[] Frames = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
        private readonly CliHost _host;
        private readonly string _label;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private bool _stopped;

        public Spinner(CliHost host, string label)
        {
            _host = host;
            _label = label;
            _task = Task.Run(async () =>
            {
                var i = 0;
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        _host.WriteInline($"\r  {Frames[i++ % Frames.Length]} {_label}...   ", ConsoleColor.DarkCyan);
                        await Task.Delay(90, _cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _cts.Cancel();
            try { _task.Wait(300); } catch { }
            _host.WriteInline("\r" + new string(' ', _label.Length + 14) + "\r", ConsoleColor.Gray);
        }
    }

    private enum CommandResult { Success, Failure, Exit }

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
        bool StrictManualExitNodeFingerprint,
        string Engine,
        string BridgeSource);
}
