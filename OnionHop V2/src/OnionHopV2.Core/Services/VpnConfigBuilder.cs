using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;

namespace OnionHopV2.Core.Services;

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
        string? dohServer,
        int dohServerPort,
        string? dohPath)
    {
        // Important: Tor's pluggable transports must bypass the tunnel ("tor" outbound),
        // otherwise they can end up routed back into Tor, causing a bootstrap loop and bridge failures.
        var torRelatedProcessNames = new[]
        {
            "tor.exe",
            "snowflake-client.exe",
            "lyrebird.exe",
            "obfs4proxy.exe",
            "conjure-client.exe",
            "webtunnel-client.exe"
        };

        var rules = new List<object>
        {
            new { action = "sniff" },
            new { process_name = torRelatedProcessNames, outbound = "direct" },
            new { ip_is_private = true, outbound = "direct" }
        };

        if (!hybridRouting)
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });
            rules.Add(new { network = "udp", outbound = "block" });
        }
        else
        {
            rules.Insert(1, new { protocol = "dns", action = "hijack-dns" });

            if (bypassAppProcessNames.Count > 0)
            {
                // Allow bypassing Tor even if "route all web traffic" is enabled.
                rules.Add(new { process_name = bypassAppProcessNames, outbound = "direct" });
            }

            if (blockQuicForTorApps && torAppProcessNames.Count > 0)
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
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = "OnionHop",
                    address = new[] { "172.19.0.1/30" },
                    auto_route = true,
                    strict_route = true
                }
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
}
