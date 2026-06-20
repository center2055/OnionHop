using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;

namespace OnionHopV3.Core.Services;

/// <summary>
/// Builds sing-box JSON config for VPN routing. Internal for testability.
/// </summary>
internal static class VpnConfigBuilder
{
    public static string BuildJson(
        bool hybridRouting,
        bool secureDns,
        int socksPort,
        IReadOnlyList<string> torAppProcessNames,
        IReadOnlyList<string> bypassAppProcessNames,
        bool routeAllWebTrafficThroughTor,
        bool blockQuicForTorApps,
        bool blockUdpTraffic,
        string? dohServer,
        int dohServerPort,
        string? dohPath,
        string? tunStack,
        int? tunMtu,
        bool tunStrictRoute,
        string? interfaceName = null,
        IReadOnlyList<string>? bypassRoutingEntries = null,
        IReadOnlyList<string>? blockRoutingEntries = null)
    {
        // Important: Tor's pluggable transports must bypass the tunnel ("tor" outbound),
        // otherwise they can end up routed back into Tor, causing a bootstrap loop and bridge failures.
        var torRelatedProcessNames = BuildTorRelatedProcessNames();

        var rules = new List<object>
        {
            new { action = "sniff" },
            new { process_name = torRelatedProcessNames, outbound = "direct" },
            new { ip_is_private = true, outbound = "direct" }
        };

        // User routing rules (issue #55): send chosen domains / IP ranges direct (bypass Tor) or block
        // them outright. Added before the hybrid / full-tunnel catch-alls so they take precedence in
        // both modes. Block is listed before bypass so a domain in both is blocked. NOTE: anything set
        // to "direct" leaves Tor with the real IP - the UI makes that explicit.
        var (blockDomains, blockIps) = ClassifyRoutingEntries(blockRoutingEntries);
        var (bypassDomains, bypassIps) = ClassifyRoutingEntries(bypassRoutingEntries);
        if (blockDomains.Count > 0) { rules.Add(new { domain_suffix = blockDomains, outbound = "block" }); }
        if (blockIps.Count > 0) { rules.Add(new { ip_cidr = blockIps, outbound = "block" }); }
        if (bypassDomains.Count > 0) { rules.Add(new { domain_suffix = bypassDomains, outbound = "direct" }); }
        if (bypassIps.Count > 0) { rules.Add(new { ip_cidr = bypassIps, outbound = "direct" }); }

        if (!hybridRouting)
        {
            // Insert at index 2 (after sniff + the pluggable-transport direct rule), NOT index 1.
            // hijack-dns must come AFTER the PT-direct rule so a transport's own DNS lookups (e.g.
            // snowflake resolving its broker/front domain, webtunnel resolving its url host) go out
            // directly instead of being captured and sent to the Tor-detoured resolver - which can't
            // answer until Tor is bootstrapped, the very thing the transport is trying to do. Putting
            // it before PT-direct deadlocks snowflake with "broker failure: no answer".
            rules.Insert(2, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { network = "udp", outbound = "block" });
        }
        else
        {
            // Same ordering rule as above: PT DNS must bypass the hijack so transports can bootstrap.
            rules.Insert(2, new { protocol = "dns", action = "hijack-dns" });

            if (bypassAppProcessNames.Count > 0)
            {
                // Allow bypassing Tor even if "route all web traffic" is enabled.
                rules.Add(new { process_name = bypassAppProcessNames, outbound = "direct" });
            }

            if (blockUdpTraffic)
            {
                // Tor does not carry UDP. Block it in hybrid mode instead of allowing silent direct bypasses.
                rules.Add(new { network = "udp", outbound = "block" });
            }
            else if (blockQuicForTorApps && torAppProcessNames.Count > 0)
            {
                // Prevent QUIC/UDP bypass for apps intended to go over Tor.
                rules.Add(new { process_name = torAppProcessNames, network = "udp", port = 443, outbound = "block" });
                rules.Add(new { process_name = torAppProcessNames, network = "udp", outbound = "block" });
            }

            if (torAppProcessNames.Count > 0)
            {
                rules.Add(new { process_name = torAppProcessNames, outbound = "tor" });
            }

            if (routeAllWebTrafficThroughTor)
            {
                rules.Add(new { network = "tcp", port = new[] { 80, 443 }, outbound = "tor" });
            }
        }

        var resolvedDohServer = string.IsNullOrWhiteSpace(dohServer) ? "cloudflare-dns.com" : dohServer.Trim();
        var resolvedDohPath = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : dohPath.Trim();
        if (!resolvedDohPath.StartsWith("/", StringComparison.Ordinal))
        {
            resolvedDohPath = "/" + resolvedDohPath;
        }
        var resolvedDohPort = dohServerPort is > 0 and <= 65535 ? dohServerPort : 443;
        var resolvedTunStack = NormalizeTunStack(tunStack);
        var resolvedTunMtu = tunMtu is >= 576 and <= 9000 ? tunMtu : null;

        var tunInbound = new Dictionary<string, object?>
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = string.IsNullOrWhiteSpace(interfaceName)
                ? (OperatingSystem.IsMacOS() ? "utun99" : "OnionHop")
                : interfaceName,
            ["address"] = new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" },
            ["auto_route"] = true,
            ["strict_route"] = tunStrictRoute,
            ["stack"] = resolvedTunStack
        };
        if (resolvedTunMtu.HasValue)
        {
            tunInbound["mtu"] = resolvedTunMtu.Value;
        }

        // sing-box requires a domain resolver when a server is specified by hostname (e.g. cloudflare-dns.com).
        // Provide a bootstrap resolver and set route.default_domain_resolver to avoid startup failures.
        var dnsServers = new List<object>();

        var dohIsIp = IPAddress.TryParse(resolvedDohServer, out _);
        if (secureDns && !dohIsIp)
        {
            dnsServers.Add(hybridRouting
                ? new Dictionary<string, object?>
                {
                    ["tag"] = "bootstrap",
                    ["type"] = "udp",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 53
                }
                : new Dictionary<string, object?>
                {
                    ["tag"] = "bootstrap",
                    ["type"] = "tcp",
                    ["server"] = "1.1.1.1",
                    ["server_port"] = 53,
                    ["detour"] = "tor"
                });
        }

        var remoteDnsServer = new Dictionary<string, object?>
        {
            ["tag"] = "remote"
        };

        if (secureDns)
        {
            remoteDnsServer["type"] = "https";
            remoteDnsServer["server"] = resolvedDohServer;
            remoteDnsServer["server_port"] = resolvedDohPort;
            remoteDnsServer["path"] = resolvedDohPath;

            if (!hybridRouting)
            {
                remoteDnsServer["detour"] = "tor";
            }

            if (!dohIsIp)
            {
                remoteDnsServer["domain_resolver"] = "bootstrap";
            }
        }
        else
        {
            if (hybridRouting)
            {
                remoteDnsServer["type"] = "udp";
                remoteDnsServer["server"] = "1.1.1.1";
                remoteDnsServer["server_port"] = 53;
            }
            else
            {
                remoteDnsServer["type"] = "tcp";
                remoteDnsServer["server"] = "1.1.1.1";
                remoteDnsServer["server_port"] = 53;
                remoteDnsServer["detour"] = "tor";
            }
        }

        dnsServers.Add(remoteDnsServer);

        var config = new
        {
            log = new
            {
                level = secureDns ? "debug" : "info",
                timestamp = true
            },
            dns = new
            {
                servers = dnsServers,
                final = "remote"
            },
            inbounds = new object[]
            {
                tunInbound
            },
            outbounds = new object[]
            {
                new
                {
                    type = "socks",
                    tag = "tor",
                    server = "127.0.0.1",
                    server_port = socksPort,
                    version = "5"
                },
                new
                {
                    type = "direct",
                    tag = "direct"
                },
                new
                {
                    type = "block",
                    tag = "block"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                default_domain_resolver = "remote",
                rules = rules,
                final = hybridRouting ? "direct" : "tor"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    // Splits raw user routing entries (one per line/comma) into domain suffixes vs IP/CIDR ranges so
    // each can become the right kind of sing-box rule. Blank lines and '#' comments are ignored.
    internal static (List<string> Domains, List<string> IpCidrs) ClassifyRoutingEntries(IReadOnlyList<string>? entries)
    {
        var domains = new List<string>();
        var ipCidrs = new List<string>();
        if (entries == null)
        {
            return (domains, ipCidrs);
        }

        foreach (var raw in entries)
        {
            var e = raw?.Trim();
            if (string.IsNullOrEmpty(e) || e.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (LooksLikeIpOrCidr(e))
            {
                ipCidrs.Add(e);
            }
            else
            {
                var domain = NormalizeRoutingDomain(e);
                if (!string.IsNullOrEmpty(domain))
                {
                    domains.Add(domain);
                }
            }
        }

        return (domains, ipCidrs);
    }

    private static bool LooksLikeIpOrCidr(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash > 0)
        {
            return IPAddress.TryParse(entry[..slash], out _) && int.TryParse(entry[(slash + 1)..], out _);
        }

        return IPAddress.TryParse(entry, out _);
    }

    private static string NormalizeRoutingDomain(string entry)
    {
        var d = entry;
        var scheme = d.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            d = d[(scheme + 3)..];
        }

        var slash = d.IndexOf('/');
        if (slash >= 0)
        {
            d = d[..slash];
        }

        return d.TrimStart('.').Trim().ToLowerInvariant();
    }

    private static string NormalizeTunStack(string? stack)
    {
        if (string.Equals(stack, "system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        if (string.Equals(stack, "gvisor", StringComparison.OrdinalIgnoreCase))
        {
            return "gvisor";
        }

        return "mixed";
    }

    private static string[] BuildTorRelatedProcessNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                "tor.exe",
                // Arti engines: they make their own outbound connections (directly to guards when not
                // using bridges, or to the local PT when using bridges). Without these in the bypass
                // list, an Arti/ArtiHop TUN connection routes its own traffic back into the tunnel and
                // deadlocks before it can bootstrap.
                "arti.exe",
                "artihop.exe",
                "snowflake-client.exe",
                "lyrebird.exe",
                "obfs4proxy.exe",
                "conjure-client.exe",
                "webtunnel-client.exe",
                "dnstt-client.exe"
            ];
        }

        return
        [
            "tor",
            "arti",
            "artihop",
            "snowflake-client",
            "lyrebird",
            "obfs4proxy",
            "conjure-client",
            "webtunnel-client",
            "dnstt-client"
        ];
    }
}
