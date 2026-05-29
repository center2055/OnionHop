using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OnionHopV2.Core.Platform;

namespace OnionHopV2.Core.Services;

/// <summary>
/// Builds Xray JSON config for VPN routing. Internal for testability.
/// </summary>
internal static class XrayConfigBuilder
{
    private static readonly string[] PrivateNetworkCidrs =
    {
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "100.64.0.0/10",
        "fc00::/7",
        "fe80::/10",
        "::1/128"
    };

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
        int? tunMtu,
        string? directOutboundSourceAddress)
    {
        // Keep Tor bootstrap/pluggable transport traffic out of the tunnel path.
        var torRelatedProcessNames = BuildTorRelatedProcessNames();

        var effectiveTorApps = MergeProcessNames(torRelatedProcessNames, torAppProcessNames);
        var effectiveBypassApps = NormalizeProcessNames(bypassAppProcessNames);

        var rules = new List<object>
        {
            new
            {
                type = "field",
                process = new[] { "self/", "xray/" },
                outboundTag = "direct"
            },
            new
            {
                type = "field",
                process = torRelatedProcessNames,
                outboundTag = "direct"
            },
            new
            {
                type = "field",
                ip = PrivateNetworkCidrs,
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

            if (blockUdpTraffic)
            {
                rules.Add(new
                {
                    type = "field",
                    network = "udp",
                    outboundTag = "block"
                });
            }
            else if (blockQuicForTorApps && effectiveTorApps.Count > 0)
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
                    network = "udp",
                    port = "443",
                    outboundTag = "block"
                });
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
        // Xray TUN inbound uses different field names from sing-box.
        // Only "name" and "MTU" (uppercase) are recognized by xray-core.
        var tunSettings = new Dictionary<string, object?>
        {
            ["name"] = OperatingSystem.IsMacOS() ? "utun99" : "OnionHop"
        };
        if (resolvedTunMtu.HasValue)
        {
            tunSettings["MTU"] = resolvedTunMtu.Value;
        }

        var dnsServers = BuildDnsServers(secureDns, socksPort, hybridRouting, dohServer, dohServerPort, dohPath);

        // Detect the default network interface so xray can bind outbound sockets to it
        // via IP_BOUND_IF, preventing a routing loop when TUN routes capture all traffic.
        var defaultInterface = DetectDefaultInterface();

        var directOutbound = BuildDirectOutbound(directOutboundSourceAddress, defaultInterface);
        var torOutbound = new Dictionary<string, object?>
        {
            ["protocol"] = "socks",
            ["tag"] = "tor",
            ["settings"] = new
            {
                servers = new[]
                {
                    new { address = "127.0.0.1", port = socksPort }
                }
            }
        };
        var blockOutbound = new Dictionary<string, object?> { ["protocol"] = "blackhole", ["tag"] = "block" };

        // Bind only the "direct" outbound to the real network interface (e.g. en0) via IP_BOUND_IF.
        // This prevents xray's direct outbound traffic from being captured by the TUN routes.
        // Do NOT bind "tor" — it connects to 127.0.0.1 which is only reachable via loopback.
        var outbounds = hybridRouting
            ? new object[] { directOutbound, torOutbound, blockOutbound }
            : new object[] { torOutbound, directOutbound, blockOutbound };

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

    private static Dictionary<string, object?> BuildDirectOutbound(string? sourceAddress, string? defaultInterface)
    {
        var outbound = new Dictionary<string, object?>
        {
            ["protocol"] = "freedom",
            ["tag"] = "direct"
        };

        if (!string.IsNullOrWhiteSpace(sourceAddress))
        {
            outbound["sendThrough"] = sourceAddress.Trim();
        }

        if (!string.IsNullOrWhiteSpace(defaultInterface))
        {
            outbound["streamSettings"] = new { sockopt = new { @interface = defaultInterface } };
        }

        return outbound;
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

    private static IReadOnlyList<object> BuildDnsServers(bool secureDns, int socksPort, bool hybridRouting, string? dohServer, int dohServerPort, string? dohPath)
    {
        if (!secureDns)
        {
            return new object[] { "1.1.1.1", "8.8.8.8" };
        }

        var resolvedHost = string.IsNullOrWhiteSpace(dohServer) ? "cloudflare-dns.com" : dohServer.Trim();
        var resolvedPath = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : dohPath.Trim();
        if (!resolvedPath.StartsWith("/", StringComparison.Ordinal))
        {
            resolvedPath = "/" + resolvedPath;
        }

        string dohUrl;
        if (resolvedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            dohUrl = resolvedHost;
        }
        else if (resolvedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            dohUrl = "https://" + resolvedHost.Substring("http://".Length);
        }
        else
        {
            var resolvedPort = dohServerPort is > 0 and <= 65535 ? dohServerPort : 443;
            dohUrl = $"https://{resolvedHost}:{resolvedPort}{resolvedPath}";
        }

        var servers = new List<object>();

        // Xray needs a bootstrap plaintext DNS to resolve the DoH hostname.
        // Without this, xray crashes when the DoH server is a hostname (e.g. cloudflare-dns.com)
        // because it has no way to resolve the initial DNS connection, especially with bridges.
        var hostIsIp = System.Net.IPAddress.TryParse(resolvedHost, out _);
        if (!hostIsIp)
        {
            servers.Add(new
            {
                address = "1.1.1.1",
                domains = new[] { resolvedHost }
            });
        }

        servers.Add(dohUrl);

        return servers;
    }

    private static string? DetectDefaultInterface()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var output = PlatformHelper.RunCommand("route", "-n get default");
                if (output != null)
                {
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
                        {
                            var iface = trimmed.Substring("interface:".Length).Trim();
                            if (!string.IsNullOrEmpty(iface))
                            {
                                return iface;
                            }
                        }
                    }
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                var output = PlatformHelper.RunCommand("ip", "route show default");
                if (output != null)
                {
                    // "default via X.X.X.X dev eth0 ..."
                    var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < parts.Length - 1; i++)
                    {
                        if (string.Equals(parts[i], "dev", StringComparison.Ordinal))
                        {
                            return parts[i + 1];
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string[] BuildTorRelatedProcessNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                "tor.exe",
                "xray.exe",
                "snowflake-client.exe",
                "lyrebird.exe",
                "obfs4proxy.exe",
                "conjure-client.exe",
                "webtunnel-client.exe"
            ];
        }

        return
        [
            "tor",
            "xray",
            "snowflake-client",
            "lyrebird",
            "obfs4proxy",
            "conjure-client",
            "webtunnel-client"
        ];
    }
}
