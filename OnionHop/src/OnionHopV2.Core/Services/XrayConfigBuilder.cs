using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OnionHopV2.Core.Services;

/// <summary>
/// Builds Xray JSON config for VPN routing. Internal for testability.
/// </summary>
internal static class XrayConfigBuilder
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
        string? dohPath,
        int? tunMtu)
    {
        // Keep Tor bootstrap/pluggable transport traffic out of the tunnel path.
        var torRelatedProcessNames = new[]
        {
            "tor.exe",
            "snowflake-client.exe",
            "lyrebird.exe",
            "obfs4proxy.exe",
            "conjure-client.exe",
            "webtunnel-client.exe"
        };

        var effectiveTorApps = MergeProcessNames(torRelatedProcessNames, torAppProcessNames);
        var effectiveBypassApps = NormalizeProcessNames(bypassAppProcessNames);

        var rules = new List<object>
        {
            new
            {
                type = "field",
                process = torRelatedProcessNames,
                outboundTag = "direct"
            },
            new
            {
                type = "field",
                ip = new[] { "geoip:private" },
                outboundTag = "direct"
            }
        };

        if (!hybridRouting)
        {
            rules.Add(new
            {
                type = "field",
                protocol = new[] { "dns" },
                outboundTag = secureDns ? "tor" : "direct"
            });
            rules.Add(new
            {
                type = "field",
                network = "udp",
                outboundTag = "block"
            });
            rules.Add(new
            {
                type = "field",
                network = "tcp,udp",
                outboundTag = "tor"
            });
        }
        else
        {
            if (effectiveBypassApps.Count > 0)
            {
                rules.Add(new
                {
                    type = "field",
                    process = effectiveBypassApps,
                    outboundTag = "direct"
                });
            }

            if (blockQuicForTorApps && effectiveTorApps.Count > 0)
            {
                rules.Add(new
                {
                    type = "field",
                    process = effectiveTorApps,
                    network = "udp",
                    port = "443",
                    outboundTag = "block"
                });
                rules.Add(new
                {
                    type = "field",
                    process = effectiveTorApps,
                    network = "udp",
                    outboundTag = "block"
                });
            }

            if (effectiveTorApps.Count > 0)
            {
                rules.Add(new
                {
                    type = "field",
                    process = effectiveTorApps,
                    outboundTag = "tor"
                });
            }

            if (routeAllWebTrafficThroughTor)
            {
                rules.Add(new
                {
                    type = "field",
                    network = "tcp",
                    port = "80,443",
                    outboundTag = "tor"
                });
            }
        }

        var resolvedTunMtu = tunMtu is >= 576 and <= 9000 ? tunMtu : null;
        var tunSettings = new Dictionary<string, object?>
        {
            ["name"] = "OnionHop"
        };
        if (resolvedTunMtu.HasValue)
        {
            tunSettings["mtu"] = resolvedTunMtu.Value;
        }

        var dnsServers = BuildDnsServers(secureDns, dohServer, dohServerPort, dohPath);

        var outbounds = hybridRouting
            ? new object[]
            {
                new
                {
                    protocol = "freedom",
                    tag = "direct"
                },
                new
                {
                    protocol = "socks",
                    tag = "tor",
                    settings = new
                    {
                        servers = new[]
                        {
                            new
                            {
                                address = "127.0.0.1",
                                port = socksPort
                            }
                        }
                    }
                },
                new
                {
                    protocol = "blackhole",
                    tag = "block"
                }
            }
            : new object[]
            {
                new
                {
                    protocol = "socks",
                    tag = "tor",
                    settings = new
                    {
                        servers = new[]
                        {
                            new
                            {
                                address = "127.0.0.1",
                                port = socksPort
                            }
                        }
                    }
                },
                new
                {
                    protocol = "freedom",
                    tag = "direct"
                },
                new
                {
                    protocol = "blackhole",
                    tag = "block"
                }
            };

        var config = new
        {
            log = new
            {
                loglevel = secureDns ? "info" : "warning"
            },
            dns = new
            {
                servers = dnsServers
            },
            inbounds = new object[]
            {
                new
                {
                    tag = "tun-in",
                    protocol = "tun",
                    settings = tunSettings,
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http", "tls", "quic" }
                    }
                }
            },
            outbounds = outbounds,
            routing = new
            {
                domainStrategy = "AsIs",
                rules = rules
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<string> MergeProcessNames(IEnumerable<string> baseline, IReadOnlyList<string> additional)
    {
        var merged = new List<string>();
        merged.AddRange(baseline);
        merged.AddRange(additional);
        return NormalizeProcessNames(merged);
    }

    private static IReadOnlyList<string> NormalizeProcessNames(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildDnsServers(bool secureDns, string? dohServer, int dohServerPort, string? dohPath)
    {
        if (!secureDns)
        {
            return new[] { "1.1.1.1", "8.8.8.8" };
        }

        var resolvedHost = string.IsNullOrWhiteSpace(dohServer) ? "cloudflare-dns.com" : dohServer.Trim();
        var resolvedPath = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : dohPath.Trim();
        if (!resolvedPath.StartsWith("/", StringComparison.Ordinal))
        {
            resolvedPath = "/" + resolvedPath;
        }

        if (resolvedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { resolvedHost };
        }

        if (resolvedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "https://" + resolvedHost.Substring("http://".Length) };
        }

        var resolvedPort = dohServerPort is > 0 and <= 65535 ? dohServerPort : 443;
        return new[] { $"https://{resolvedHost}:{resolvedPort}{resolvedPath}" };
    }
}
