using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OnionHopV3.Core.Services;

/// <summary>
/// What a Smart Connect connect does around the advisor's plan. It orders the attempts: in a censored
/// network a quick check of which transports answer at all, then what failed on this network recently
/// goes last and what worked here before goes first. And it remembers how each attempt went. The app
/// and the CLI both connect through one of these; the CLI used to take the bare plan and remember
/// nothing, so it retried the same failing transports on every connect (#89).
/// </summary>
public sealed class SmartConnectSession
{
    // Probes run side by side, so this is roughly one round of probing, not the sum over transports.
    private static readonly TimeSpan ReachabilityRaceBudget = TimeSpan.FromSeconds(10);

    private readonly OnionHopClient _client;
    private readonly SmartConnectAdvisor _advisor;
    private readonly SmartConnectMemory _memory;

    // The network (country + address prefix) the current attempts run on, so a success is remembered
    // and stale memory forgotten against the right one. Null until a plan knows the network.
    private string? _networkKey;

    public SmartConnectSession(OnionHopClient client, SmartConnectAdvisor advisor, SmartConnectMemory memory)
    {
        _client = client;
        _advisor = advisor;
        _memory = memory;
    }

    /// <summary>The advisor's plan behind the last <see cref="BuildStrategiesAsync"/>; null when planning failed.</summary>
    public SmartConnectAdvisor.Plan? Plan { get; private set; }

    /// <summary>What attempts are remembered under on this network, e.g. "IR/5.120" or "IR/gw:192.168.8.1".</summary>
    public string? NetworkKey => _networkKey;

    /// <summary>
    /// The strategies to try, in order. Only real cancellation throws: when planning fails, a generic
    /// profile is returned instead.
    /// </summary>
    /// <param name="log">Every detail, for the log.</param>
    /// <param name="notice">The ordering decisions, worth showing to the user.</param>
    /// <param name="progress">Called when the reachability check starts, which can take a few seconds.</param>
    /// <param name="planned">Called with the plan as soon as it is ready, before the attempts are ordered.</param>
    public async Task<IReadOnlyList<SmartConnectAdvisor.Strategy>> BuildStrategiesAsync(
        OnionHopConnectOptions baseOptions,
        Action<string> log,
        Action<string> notice,
        Action<string>? progress,
        CancellationToken token,
        Action<SmartConnectAdvisor.Plan>? planned = null)
    {
        // Set again below once the plan knows the network; until then nothing may be remembered
        // against the previous connect's network.
        _networkKey = null;
        Plan = null;

        try
        {
            var plan = await _advisor.BuildPlanAsync(baseOptions, log, token).ConfigureAwait(false);
            Plan = plan;
            planned?.Invoke(plan);
            if (plan.Strategies.Count > 0)
            {
                var strategies = plan.Strategies;

                // Reachability racing: in restricted/severe networks, concurrently probe which bridge
                // transports get through at all and try the dead ones last, instead of spending a whole
                // attempt on each. Cheap probes up front, then a single clean connect (never several
                // live Tor processes at once).
                if (plan.Risk is SmartConnectAdvisor.RiskLevel.Restricted or SmartConnectAdvisor.RiskLevel.Severe)
                {
                    strategies = await ApplyReachabilityRacingAsync(strategies, log, notice, progress, token).ConfigureAwait(false);
                }

                // Failure memory: what failed on this network in the last two days goes last.
                _networkKey = SmartConnectMemory.BuildNetworkKey(
                    plan.CountryCode,
                    plan.PublicIp,
                    plan.PublicIp == null ? Networking.LanAddressFinder.GetDefaultGatewayIPv4() : null);
                var recentlyFailed = _memory.GetRecentlyFailedTransports(_networkKey);
                var demoted = SmartConnectAdvisor.DemoteRecentlyFailed(strategies, recentlyFailed);
                if (!demoted.SequenceEqual(strategies))
                {
                    var tryLast = demoted
                        .Select(SmartConnectAdvisor.GetStrategyTransport)
                        .Where(recentlyFailed.Contains);
                    notice($"Smart Connect: trying {string.Join(", ", tryLast)} last (failed on this network recently).");
                    strategies = demoted;
                }

                // Success memory: what worked on this network before goes first; the rest of the ladder
                // stays as the fallback. If everything fails, the memory is forgotten (RecordAllFailed).
                var remembered = _memory.TryGet(_networkKey);
                if (remembered != null)
                {
                    var promoted = SmartConnectAdvisor.PromoteRememberedStrategy(strategies, remembered.Transport);
                    if (promoted.Count > 0 &&
                        !string.Equals(promoted[0].Name, strategies[0].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        notice($"Smart Connect: trying '{remembered.Transport}' first (worked on this network before).");
                    }

                    return promoted;
                }

                return strategies;
            }

            _networkKey = null;
            notice("Smart Connect planner returned no strategies. Falling back to generic profile.");
        }
        // Only real cancellation aborts the connect. HttpClient timeouts inside the planner throw
        // TaskCanceledException too; those must fall through to the generic profile below (#65).
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            notice($"Smart Connect planner failed: {ex.Message}. Falling back to generic profile.");
        }

        var genericFallback = SmartConnectAdvisor.BuildStrategiesForRisk(baseOptions, SmartConnectAdvisor.RiskLevel.Unknown);
        if (genericFallback.Count > 0)
        {
            return genericFallback;
        }

        return [new SmartConnectAdvisor.Strategy("manual", "Fallback to current settings.", baseOptions)];
    }

    /// <summary>
    /// Remembers how an attempt went, once <see cref="OnionHopClient.ConnectAsync"/> has returned for it,
    /// and writes the outcome with its duration to the startup log. Returns whether it connected. Not
    /// for attempts the user canceled: those say nothing about the network.
    /// </summary>
    public bool RecordAttempt(SmartConnectAdvisor.Strategy strategy, TimeSpan elapsed)
    {
        var status = _client.CurrentStatus;
        var transport = SmartConnectAdvisor.GetStrategyTransport(strategy);
        if (status.IsConnected)
        {
            _memory.RecordSuccess(_networkKey, transport, strategy.Options.UseTorBridges);
            StartupLogger.Write($"Smart Connect: {strategy.Name} connected after {elapsed.TotalSeconds:0} s.");
            return true;
        }

        _memory.RecordFailure(_networkKey, transport);
        StartupLogger.Write($"Smart Connect: {strategy.Name} failed after {elapsed.TotalSeconds:0} s: {status.StatusMessage}");
        return false;
    }

    /// <summary>
    /// Every strategy failed: whatever was remembered for this network no longer works (censors move),
    /// so forget it and work it out afresh next time.
    /// </summary>
    public void RecordAllFailed() => _memory.Invalidate(_networkKey);

    /// <summary>
    /// Concurrently measure which bridge transports get through on this network, each with the options
    /// (and so the bridge lines) its attempt will use, and move the ones where nothing answered to the
    /// back of the ladder. Best-effort: a failure or timeout leaves the ladder unchanged.
    /// </summary>
    private async Task<IReadOnlyList<SmartConnectAdvisor.Strategy>> ApplyReachabilityRacingAsync(
        IReadOnlyList<SmartConnectAdvisor.Strategy> strategies,
        Action<string> log,
        Action<string> notice,
        Action<string>? progress,
        CancellationToken token)
    {
        try
        {
            // Probe each distinct bridge transport in the ladder with its own strategy's options:
            // those carry Tor's lines for the user's country, which are what its attempt will use.
            var candidates = strategies
                .Where(s => s.Options.UseTorBridges)
                .Select(s => (Transport: SmartConnectAdvisor.GetStrategyTransport(s), s.Options))
                .Where(c => !string.IsNullOrWhiteSpace(c.Transport) && !OnionHopClient.IsAutomaticBridgeType(c.Transport))
                .DistinctBy(c => c.Transport, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count < 2)
            {
                return strategies;
            }

            progress?.Invoke("Smart Connect is checking which bridges are reachable...");
            var reachability = await _client.ProbeTransportReachabilityAsync(
                candidates, ReachabilityRaceBudget, token).ConfigureAwait(false);

            if (reachability.Count == 0)
            {
                return strategies;
            }

            foreach (var kvp in reachability)
            {
                log($"Smart Connect reachability: {kvp.Key} -> {kvp.Value.ReachableCount} reachable" +
                    (kvp.Value.FastestPingMs.HasValue ? $" (fastest {kvp.Value.FastestPingMs} ms)." : "."));
            }

            var reordered = SmartConnectAdvisor.ReorderByReachability(strategies, reachability);
            var unreachable = reachability.Where(kvp => kvp.Value.ReachableCount <= 0).Select(kvp => kvp.Key).ToList();
            if (unreachable.Count > 0)
            {
                notice($"Smart Connect: trying {string.Join(", ", unreachable)} last (nothing of it reachable here).");
            }

            return reordered;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Smart Connect: reachability race skipped ({ex.Message}).");
            return strategies;
        }
    }
}
