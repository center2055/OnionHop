using System;

namespace OnionHopV2.Core.Networking;

internal readonly record struct DohSettings(string Server, int Port, string Path);

internal static class DohSettingsResolver
{
    public static DohSettings Resolve(OnionHopConnectOptions options)
    {
        var provider = options.SelectedDnsProvider;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = OnionHopConnectOptions.DnsProviderCloudflare;
        }

        var server = provider switch
        {
            OnionHopConnectOptions.DnsProviderGoogle => "dns.google",
            OnionHopConnectOptions.DnsProviderQuad9 => "dns.quad9.net",
            OnionHopConnectOptions.DnsProviderCustom => options.CustomDohHost,
            _ => "cloudflare-dns.com"
        };

        var path = provider == OnionHopConnectOptions.DnsProviderCustom ? options.CustomDohPath : "/dns-query";
        var port = 443;

        if (provider == OnionHopConnectOptions.DnsProviderCustom)
        {
            var normalizedHost = NormalizeDohHost(options.CustomDohHost);
            server = normalizedHost.Server;
            port = normalizedHost.Port;
            path = NormalizeDohPath(options.CustomDohPath);
        }

        if (string.IsNullOrWhiteSpace(server))
        {
            server = "cloudflare-dns.com";
        }

        return new DohSettings(server, port, path ?? "/dns-query");
    }

    private static DohSettings NormalizeDohHost(string? rawHost)
    {
        var host = (rawHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return new DohSettings("cloudflare-dns.com", 443, "/dns-query");
        }

        // Allow users to paste a full URL or host[:port].
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new DohSettings(uri.Host, uri.IsDefaultPort ? 443 : uri.Port, "/dns-query");
        }

        if (Uri.TryCreate($"https://{host}", UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new DohSettings(uri.Host, uri.IsDefaultPort ? 443 : uri.Port, "/dns-query");
        }

        return new DohSettings(host, 443, "/dns-query");
    }

    private static string NormalizeDohPath(string? rawPath)
    {
        var path = (rawPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/dns-query";
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        return path;
    }
}

