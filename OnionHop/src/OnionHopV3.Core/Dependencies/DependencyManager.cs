using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OnionHopV3.Core.Models;
using OnionHopV3.Core.Platform;
using OnionHopV3.Core.Services;
using OnionHopV3.Core.Tor;

namespace OnionHopV3.Core.Dependencies;

internal sealed class DependencyManager
{
    private const string TorFallbackVersion = "15.0.7";
    private const string TorBaseUrl = "https://dist.torproject.org/torbrowser";
    private const string TorArchiveBaseUrl = "https://archive.torproject.org/tor-package-archive/torbrowser";
    private const string TorChecksumFileName = "sha256sums-signed-build.txt";

    // The tunnel cores are pinned to the versions bundled with the app. These downloads only happen
    // when the bundled copy is missing, so "latest" never kept anyone up to date; it only meant a
    // fresh install could get a core the generated configs had never been tested against, and
    // sing-box has broken existing configs between minor releases before. When bumping the bundled
    // cores, update the versions and the hashes below together.
    private const string SingBoxVersion = "1.13.13";
    private const string XrayVersion = "26.3.27";
    private const string SingBoxApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/tags/v" + SingBoxVersion;
    private const string XrayApiUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/tags/v" + XrayVersion;

    // SHA-256 of every asset above that OnionHop may download, as published on GitHub for those tags.
    // Pinned here so the check does not depend on the same API that hands out the download link.
    private static readonly IReadOnlyDictionary<string, string> PinnedCoreSha256 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sing-box-1.13.13-windows-amd64.zip"] = "aea1fa983134a2e2d0600581d1178e98bd6bb93ae12ad8c333eaacae68a1694c",
        ["sing-box-1.13.13-darwin-amd64.tar.gz"] = "477afd64ad7751214f01338ba244265ecc223966ddb58214963f526dca7f424e",
        ["sing-box-1.13.13-darwin-arm64.tar.gz"] = "4ac414d4ede9ec21bc79d8ccf40b4679429203b9e06ad96d2d8d34c0fe940558",
        ["sing-box-1.13.13-linux-amd64.tar.gz"] = "bb99cabf47694625db421ee17898f36cdc1f9c2cb5decf65b12bac8d8437e842",
        ["sing-box-1.13.13-linux-arm64.tar.gz"] = "d7fab87b921933eb281d8ee7bd5377cdd8228089f1f7c807c9363a6a2329286c",
        ["Xray-windows-64.zip"] = "d004c39288ce9ada487c6f398c7c545f7d749e44bdfdd59dbc9f865afba4e1ad",
        ["Xray-macos-64.zip"] = "f5b0471d3459eff1b82e48af0aeac186abcc3298210070afbbbd8437a4e8b203",
        ["Xray-macos-arm64-v8a.zip"] = "2e93a67e8aa1936ecefb307e120830fcbd4c643ab9b1c46a2d0838d5f8409eaf",
        ["Xray-linux-64.zip"] = "23cd9af937744d97776ee35ecad4972cf4b2109d1e0fe6be9930467608f7c8ae",
        ["Xray-linux-arm64-v8a.zip"] = "4d30283ae614e3057f730f67cd088a42be6fdf91f8639d82cb69e48cde80413c",
    };

    private const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
    // The URL above is a fixed version, so its checksum can be fixed too (as published on wintun.net).
    // Wintun is a kernel driver loaded by a core running as administrator: it is the last file that
    // should be trusted on TLS alone.
    private const string WintunSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

    public sealed record DependencyUpdate(bool InProgress, string Status, double Progress);

    public async Task<bool> EnsureAsync(
        string baseDir,
        bool requireVpnDependencies,
        Action<DependencyUpdate> progress,
        Action<string> log,
        CancellationToken token)
    {
        var torDir = Path.Combine(baseDir, "tor");
        var vpnDir = Path.Combine(baseDir, "vpn");
        var ptDir = Path.Combine(torDir, "pluggable_transports");

        TrySeedBundledRuntime(baseDir, requireVpnDependencies, log);

        var torPath = Path.Combine(torDir, PlatformHelper.TorBinaryName);
        var geoip = Path.Combine(torDir, "geoip");
        var geoip6 = Path.Combine(torDir, "geoip6");

        var singBoxPath = Path.Combine(vpnDir, PlatformHelper.SingBoxBinaryName);
        var xrayPath = Path.Combine(vpnDir, PlatformHelper.XrayBinaryName);
        var wintunPath = Path.Combine(vpnDir, PlatformHelper.WintunLibraryName);

        // Recent Tor expert bundles on macOS do not always include tor-gencert.
        // tor-gencert is optional for runtime, so do not block dependency readiness on it.
        var needsTor = !File.Exists(torPath) || !File.Exists(geoip) || !File.Exists(geoip6) || !Directory.Exists(ptDir);
        var needsSingBox = requireVpnDependencies && !File.Exists(singBoxPath);
        var needsXray = requireVpnDependencies && !File.Exists(xrayPath);
        var needsWintun = requireVpnDependencies && PlatformHelper.NeedsWintun && !File.Exists(wintunPath);

        if (!needsTor && !needsSingBox && !needsXray && !needsWintun)
        {
            var ptConfigPath = Path.Combine(ptDir, "pt_config.json");
            EnsurePluggableTransportConfig(ptConfigPath, log);
            return true;
        }

        progress(new DependencyUpdate(true, "Preparing downloads...", 0));

        // Use a unique temp directory per run to avoid permission conflicts when the
        // app relaunches as root (root-owned leftovers would block the normal user).
        var tempRoot = Path.Combine(Path.GetTempPath(), "OnionHop", $"deps-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(torDir);
            Directory.CreateDirectory(vpnDir);
            Directory.CreateDirectory(ptDir);

            var client = HttpClientFactory.LongTimeout;
            var steps = new List<(string Label, Func<Task> Action)>();

            if (needsTor)
            {
                steps.Add(("Downloading Tor...", () => DownloadTorAsync(client, tempRoot, torDir, ptDir, log, token)));
            }

            if (needsSingBox)
            {
                steps.Add(("Downloading sing-box...", () => DownloadSingBoxAsync(client, tempRoot, singBoxPath, token)));
            }

            if (needsXray)
            {
                steps.Add(("Downloading xray...", () => DownloadXrayAsync(client, tempRoot, xrayPath, token)));
            }

            if (needsWintun)
            {
                steps.Add(("Downloading Wintun...", () => DownloadWintunAsync(client, tempRoot, wintunPath, token)));
            }

            for (var i = 0; i < steps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                progress(new DependencyUpdate(true, steps[i].Label, i / (double)steps.Count));
                await steps[i].Action().ConfigureAwait(false);
                progress(new DependencyUpdate(true, steps[i].Label, (i + 1) / (double)steps.Count));
            }

            var ptConfigPath = Path.Combine(ptDir, "pt_config.json");
            EnsurePluggableTransportConfig(ptConfigPath, log);

            progress(new DependencyUpdate(false, "Components ready.", 1));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            log("Dependency download cancelled.");
            progress(new DependencyUpdate(false, "Dependency download cancelled.", 0));
            throw;
        }
        catch (Exception ex)
        {
            log($"Dependency download failed: {ex.Message}");
            progress(new DependencyUpdate(false, $"Dependency download failed: {ex.Message}", 0));
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
            catch
            {
            }
        }
    }

    public static PluggableTransportConfig? TryLoadPluggableTransportConfig(string baseDir, Action<string> log)
    {
        var configPath = Path.Combine(baseDir, "tor", "pluggable_transports", "pt_config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<PluggableTransportConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            log($"Bridge config load failed: {ex.Message}");
            return null;
        }
    }

    public static void EnsurePluggableTransportConfig(string configPath, Action<string> log)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<PluggableTransportConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (config == null)
            {
                return;
            }

            var lyrebirdName = PlatformHelper.LyrebirdBinaryName;
            var snowflakeName = PlatformHelper.SnowflakeClientBinaryName;
            var webtunnelName = PlatformHelper.WebTunnelClientBinaryName;
            var conjureName = PlatformHelper.ConjureClientBinaryName;

            var updated = false;
            config.PluggableTransports ??= new Dictionary<string, string>();

            if (!config.PluggableTransports.TryGetValue("lyrebird", out var lyrebirdLine)
                || string.IsNullOrWhiteSpace(lyrebirdLine)
                || !TransportLineReferencesBinary(lyrebirdLine, lyrebirdName))
            {
                config.PluggableTransports["lyrebird"] =
                    $"ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit exec ${{pt_path}}{lyrebirdName}";
                updated = true;
            }

            // conjure needs its own conjure-client binary (with the refraction registration URL),
            // NOT lyrebird, which does not speak conjure. The config historically pointed conjure at
            // lyrebird.exe, so conjure never worked on any platform (#64). Rewrite it to the shipped
            // conjure-client whenever the line does not already reference that exact binary.
            if (!config.PluggableTransports.TryGetValue("conjure", out var conjureLine)
                || string.IsNullOrWhiteSpace(conjureLine)
                || !TransportLineReferencesBinary(conjureLine, conjureName))
            {
                config.PluggableTransports["conjure"] =
                    $"ClientTransportPlugin conjure exec ${{pt_path}}{conjureName} -registerURL https://registration.refraction.network/api";
                updated = true;
            }

            if (!config.PluggableTransports.TryGetValue("snowflake", out var snowflakeLine)
                || string.IsNullOrWhiteSpace(snowflakeLine)
                || (!TransportLineReferencesBinary(snowflakeLine, lyrebirdName)
                    && !TransportLineReferencesBinary(snowflakeLine, snowflakeName)))
            {
                // Lyrebird 0.8+ natively supports the snowflake transport.
                config.PluggableTransports["snowflake"] =
                    $"ClientTransportPlugin snowflake exec ${{pt_path}}{lyrebirdName}";
                updated = true;
            }

            if (!config.PluggableTransports.TryGetValue("webtunnel", out var webTunnelLine)
                || string.IsNullOrWhiteSpace(webTunnelLine)
                || !TransportLineReferencesBinary(webTunnelLine, webtunnelName))
            {
                config.PluggableTransports["webtunnel"] =
                    $"ClientTransportPlugin webtunnel exec ${{pt_path}}{webtunnelName}";
                updated = true;
            }

            config.RecommendedDefault ??= "obfs4";

            // Merge bridge types present in the bundled config but missing from this (possibly older,
            // cached) runtime config. New transports like dnstt ship in the bundle; the runtime
            // pt_config lives under LocalAppData and is only seeded "if missing", so without this an
            // upgraded install keeps a stale config and the new type never appears in the UI. We only
            // ADD missing/empty types, so fetched/updated bridge lines for existing types are kept.
            try
            {
                var bundledPath = Path.Combine(AppContext.BaseDirectory, "tor", "pluggable_transports", "pt_config.json");
                if (File.Exists(bundledPath) &&
                    !string.Equals(Path.GetFullPath(bundledPath), Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase))
                {
                    var bundled = JsonSerializer.Deserialize<PluggableTransportConfig>(
                        File.ReadAllText(bundledPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (bundled?.Bridges is { Count: > 0 })
                    {
                        config.Bridges ??= new Dictionary<string, List<string>>();
                        foreach (var kvp in bundled.Bridges)
                        {
                            var hasUsable = config.Bridges.TryGetValue(kvp.Key, out var existing) && existing is { Count: > 0 };
                            if (!hasUsable && kvp.Value is { Count: > 0 })
                            {
                                config.Bridges[kvp.Key] = new List<string>(kvp.Value);
                                updated = true;
                                log($"pt_config: added bundled bridge type '{kvp.Key}' to the runtime config.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log($"pt_config bridge-type merge skipped: {ex.Message}");
            }

            if (updated)
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            log($"Failed to ensure pt_config: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true only when <paramref name="line"/> references <paramref name="binaryName"/> as a
    /// complete final filename token (the next char is not part of a longer filename). A plain
    /// Contains() is wrong here: pt_config.json ships Windows names, so on macOS/Linux a stale
    /// "lyrebird.exe" line contains "lyrebird" and used to pass the check, leaving obfs4/conjure/
    /// snowflake pointing at a nonexistent ".exe" binary so the managed proxy died and bridges never
    /// connected (issues #42 and #65). Matching the whole token forces the platform-correct rewrite.
    /// </summary>
    internal static bool TransportLineReferencesBinary(string? line, string binaryName)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(binaryName))
        {
            return false;
        }

        var index = line.IndexOf(binaryName, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var afterIndex = index + binaryName.Length;
            var next = afterIndex < line.Length ? line[afterIndex] : '\0';
            // Reject prefix-only matches like "lyrebird" inside "lyrebird.exe": the following char
            // would still be part of the filename (letter, digit, '.', '-' or '_').
            if (next == '\0' || !(char.IsLetterOrDigit(next) || next == '.' || next == '-' || next == '_'))
            {
                return true;
            }

            index = line.IndexOf(binaryName, afterIndex, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task DownloadTorAsync(HttpClient client, string tempRoot, string torDir, string ptDir, Action<string> log, CancellationToken token)
    {
        var torPath = Path.Combine(torDir, PlatformHelper.TorBinaryName);
        var torGenCertPath = Path.Combine(torDir, PlatformHelper.TorGenCertBinaryName);
        var torArchivePath = Path.Combine(tempRoot, "tor.tar.gz");
        var versionCandidates = await GetTorVersionCandidatesAsync(client, token).ConfigureAwait(false);
        var suffixCandidates = GetTorSuffixCandidates();

        Exception? lastVersionError = null;
        string? resolvedVersion = null;
        foreach (var version in versionCandidates)
        {
            try
            {
                var candidates = await ResolveTorDownloadCandidatesAsync(client, version, suffixCandidates, token).ConfigureAwait(false);
                log($"Tor download: trying version {version} with {candidates.Count} candidate URL(s).");
                var downloadedFrom = await DownloadWithFallbackAsync(client, candidates, torArchivePath, token).ConfigureAwait(false);
                await VerifyTorBundleAsync(client, downloadedFrom, torArchivePath, token).ConfigureAwait(false);
                resolvedVersion = version;
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastVersionError = ex;
                log($"Tor download attempt failed for version {version}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedVersion))
        {
            throw new InvalidOperationException(
                $"Tor download failed for all version candidates ({string.Join(", ", versionCandidates)}). Last error: {lastVersionError?.Message}");
        }

        log($"Tor download: using version {resolvedVersion}.");

        await Task.Run(() =>
        {
            var extractRoot = Path.Combine(tempRoot, "tor_extract");
            if (Directory.Exists(extractRoot))
            {
                Directory.Delete(extractRoot, true);
            }
            Directory.CreateDirectory(extractRoot);
            ExtractTarGz(torArchivePath, extractRoot);

            var extractedTorPath = FindFirstFileByName(extractRoot, PlatformHelper.TorBinaryName);
            var extractedTorGenCertPath = FindFirstFileByName(extractRoot, PlatformHelper.TorGenCertBinaryName);
            if (string.IsNullOrWhiteSpace(extractedTorPath))
            {
                throw new InvalidOperationException("Tor extraction failed or unexpected structure.");
            }

            File.Copy(extractedTorPath, torPath, true);
            if (!string.IsNullOrWhiteSpace(extractedTorGenCertPath))
            {
                File.Copy(extractedTorGenCertPath, torGenCertPath, true);
            }

            // Copy shared libraries (e.g. libevent) that live alongside the tor binary
            var torSourceDir = Path.GetDirectoryName(extractedTorPath);
            if (torSourceDir != null)
            {
                foreach (var dylib in Directory.GetFiles(torSourceDir, "*.dylib"))
                {
                    File.Copy(dylib, Path.Combine(torDir, Path.GetFileName(dylib)), true);
                }
                foreach (var soFile in Directory.GetFiles(torSourceDir, "*.so*"))
                {
                    File.Copy(soFile, Path.Combine(torDir, Path.GetFileName(soFile)), true);
                }
            }

            var geoipSource = FindFirstFileByName(extractRoot, "geoip");
            var geoip6Source = FindFirstFileByName(extractRoot, "geoip6");
            if (geoipSource != null)
            {
                File.Copy(geoipSource, Path.Combine(torDir, "geoip"), true);
            }

            if (geoip6Source != null)
            {
                File.Copy(geoip6Source, Path.Combine(torDir, "geoip6"), true);
            }

            var extractedPtDir = FindDirectoryByName(extractRoot, "pluggable_transports");
            if (!string.IsNullOrWhiteSpace(extractedPtDir))
            {
                CopyDirectory(extractedPtDir, ptDir, overwrite: true, preserveFileName: "pt_config.json");
            }

            var obfs4ProxyPath = Path.Combine(ptDir, PlatformHelper.Obfs4ProxyBinaryName);
            var lyrebirdPath = Path.Combine(ptDir, PlatformHelper.LyrebirdBinaryName);
            if (!File.Exists(lyrebirdPath) && File.Exists(obfs4ProxyPath))
            {
                File.Move(obfs4ProxyPath, lyrebirdPath);
            }

            EnsureUnixExecutable(torPath);
            EnsureUnixExecutable(torGenCertPath);
            EnsureUnixExecutable(Path.Combine(ptDir, PlatformHelper.LyrebirdBinaryName));
            EnsureUnixExecutable(Path.Combine(ptDir, PlatformHelper.SnowflakeClientBinaryName));
            EnsureUnixExecutable(Path.Combine(ptDir, PlatformHelper.WebTunnelClientBinaryName));
            EnsureUnixExecutable(Path.Combine(ptDir, "conjure-client"));

            // On macOS, remove quarantine attributes and ad-hoc codesign downloaded
            // binaries so Gatekeeper allows execution without manual xattr -cr.
            PlatformHelper.RemoveQuarantineOnMacOS(torDir);
            PlatformHelper.RemoveQuarantineOnMacOS(ptDir);
            AdHocCodesignOnMacOS(torPath);
            AdHocCodesignOnMacOS(torGenCertPath);
            AdHocCodesignOnMacOS(Path.Combine(ptDir, PlatformHelper.LyrebirdBinaryName));
            AdHocCodesignOnMacOS(Path.Combine(ptDir, PlatformHelper.SnowflakeClientBinaryName));
            AdHocCodesignOnMacOS(Path.Combine(ptDir, PlatformHelper.WebTunnelClientBinaryName));
            AdHocCodesignOnMacOS(Path.Combine(ptDir, "conjure-client"));
            foreach (var lib in Directory.GetFiles(torDir, "*.dylib"))
            {
                AdHocCodesignOnMacOS(lib);
            }
        }).ConfigureAwait(false);
    }

    private static async Task DownloadSingBoxAsync(HttpClient client, string tempRoot, string singBoxPath, CancellationToken token)
    {
        using var response = await client.GetAsync(SingBoxApiUrl, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Failed to query sing-box releases.");
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        var assetName = $"sing-box-{SingBoxVersion}-{PlatformHelper.SingBoxPlatformAssetFilter}";
        var asset = release?.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl) || string.IsNullOrWhiteSpace(asset.Name))
        {
            throw new InvalidOperationException($"No sing-box asset named {assetName} in release v{SingBoxVersion}.");
        }

        var archivePath = Path.Combine(tempRoot, asset.Name);
        await DownloadToFileAsync(client, asset.BrowserDownloadUrl, archivePath, token).ConfigureAwait(false);
        await VerifyCoreAssetAsync(archivePath, asset, token).ConfigureAwait(false);
        await ExtractAndCopyBinaryAsync(tempRoot, archivePath, PlatformHelper.SingBoxBinaryName, singBoxPath).ConfigureAwait(false);
    }

    private static async Task DownloadXrayAsync(HttpClient client, string tempRoot, string xrayPath, CancellationToken token)
    {
        using var response = await client.GetAsync(XrayApiUrl, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Failed to query xray releases.");
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        // Exact name, not keyword hints: "windows" + "64" + ".zip" also matched the .dgst checksum
        // file and the ARM64 build, and only the order GitHub happened to list assets in picked the
        // right one.
        var assetName = PlatformHelper.XrayAssetName;
        var asset = release?.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));

        if (asset == null)
        {
            throw new InvalidOperationException($"No xray asset named {assetName} in release v{XrayVersion}.");
        }

        var archivePath = Path.Combine(tempRoot, asset.Name!);
        await DownloadToFileAsync(client, asset.BrowserDownloadUrl!, archivePath, token).ConfigureAwait(false);
        await VerifyCoreAssetAsync(archivePath, asset, token).ConfigureAwait(false);
        await ExtractAndCopyBinaryAsync(tempRoot, archivePath, PlatformHelper.XrayBinaryName, xrayPath).ConfigureAwait(false);
    }

    private static async Task DownloadWintunAsync(HttpClient client, string tempRoot, string wintunPath, CancellationToken token)
    {
        var zipPath = Path.Combine(tempRoot, "wintun.zip");
        await DownloadToFileAsync(client, WintunUrl, zipPath, token).ConfigureAwait(false);
        await VerifySha256Async(zipPath, WintunSha256, "wintun", token).ConfigureAwait(false);

        await Task.Run(() =>
        {
            var extractDir = Path.Combine(tempRoot, "wintun");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var dllPath = Directory.GetFiles(extractDir, "wintun.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dllPath == null)
            {
                throw new FileNotFoundException("wintun.dll not found in archive.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(wintunPath) ?? AppContext.BaseDirectory);
            File.Copy(dllPath, wintunPath, true);
        }).ConfigureAwait(false);
    }

    private static async Task ExtractAndCopyBinaryAsync(string tempRoot, string archivePath, string binaryName, string destinationPath)
    {
        await Task.Run(() =>
        {
            var extractDir = Path.Combine(tempRoot, Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            Directory.CreateDirectory(extractDir);

            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir, true);
            }
            else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarGz(archivePath, extractDir);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported archive format: {archivePath}");
            }

            var binaryPath = FindFirstFileByName(extractDir, binaryName);
            if (string.IsNullOrWhiteSpace(binaryPath))
            {
                throw new FileNotFoundException($"{binaryName} not found in archive.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
            File.Copy(binaryPath, destinationPath, true);
            EnsureUnixExecutable(destinationPath);
            PlatformHelper.RemoveQuarantineOnMacOS(destinationPath);
            AdHocCodesignOnMacOS(destinationPath);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The tunnel cores are executed with administrator rights, and used to be run straight from
    /// whatever the download returned. Check the archive before extracting anything: against the hash
    /// pinned in this file, or failing that the SHA-256 GitHub publishes for the asset. With neither,
    /// nothing vouches for the file, so it is not used.
    /// </summary>
    private static Task VerifyCoreAssetAsync(string archivePath, GitHubAsset asset, CancellationToken token)
    {
        var name = asset.Name ?? Path.GetFileName(archivePath);
        var expected = ExpectedCoreSha256(name, asset.Digest)
            ?? throw new InvalidOperationException($"No checksum is known for {name}, so it was not used.");
        return VerifySha256Async(archivePath, expected, name, token);
    }

    /// <summary>The pinned hash for a core asset, else GitHub's published digest, else null.</summary>
    internal static string? ExpectedCoreSha256(string assetName, string? gitHubDigest) =>
        PinnedCoreSha256.TryGetValue(assetName, out var pinned) ? pinned : ParseGitHubDigest(gitHubDigest);

    /// <summary>
    /// The Tor Project publishes a SHA-256 for every expert bundle in a sums file beside it. The bundle
    /// is checked against it before anything is extracted. The sums file comes from the same Tor
    /// Project server over HTTPS, so this catches a broken or altered download, not a compromise of
    /// torproject.org itself; that would take checking the OpenPGP signature published next to it.
    /// </summary>
    private static async Task VerifyTorBundleAsync(HttpClient client, string bundleUrl, string archivePath, CancellationToken token)
    {
        var fileName = Path.GetFileName(new Uri(bundleUrl).AbsolutePath);
        var sumsUrl = bundleUrl[..(bundleUrl.LastIndexOf('/') + 1)] + TorChecksumFileName;
        var sums = await client.GetStringAsync(sumsUrl, token).ConfigureAwait(false);
        var expected = FindSha256InSumsFile(sums, fileName)
            ?? throw new InvalidOperationException($"{TorChecksumFileName} lists no checksum for {fileName}, so it was not used.");
        await VerifySha256Async(archivePath, expected, fileName, token).ConfigureAwait(false);
    }

    /// <summary>The SHA-256 listed for <paramref name="fileName"/> in a sha256sum-style file, or null.</summary>
    internal static string? FindSha256InSumsFile(string? sums, string fileName)
    {
        if (string.IsNullOrEmpty(sums) || string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        foreach (var rawLine in sums.Split('\n'))
        {
            // "<64 hex>  <name>", or "<64 hex> *<name>" when written in binary mode.
            var line = rawLine.Trim();
            if (line.Length < 66 || line[64] != ' ')
            {
                continue;
            }

            var hex = line[..64];
            var name = line[65..].TrimStart(' ', '*');
            if (string.Equals(name, fileName, StringComparison.Ordinal) && hex.All(Uri.IsHexDigit))
            {
                return hex.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>"sha256:ABC..." to "abc...", or null for anything that is not a SHA-256 digest.</summary>
    internal static string? ParseGitHubDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>Throw, and delete the file, if its SHA-256 is not the expected one.</summary>
    internal static async Task VerifySha256Async(string path, string expectedHex, string what, CancellationToken token)
    {
        string actual;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                .ToLowerInvariant();
        }

        if (!string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException(
                $"The downloaded {what} failed its integrity check (expected SHA-256 {expectedHex}, got {actual}). " +
                "It was deleted and not used. This can be a broken download or a tampered file; try again later.");
        }
    }

    private static async Task DownloadToFileAsync(HttpClient client, string url, string targetPath, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, token).ConfigureAwait(false);
    }

    /// <summary>Downloads from the first URL that works and returns that URL.</summary>
    private static async Task<string> DownloadWithFallbackAsync(HttpClient client, IEnumerable<string> urls, string targetPath, CancellationToken token)
    {
        Exception? lastError = null;
        string? lastUrl = null;
        var attemptCount = 0;
        foreach (var url in urls)
        {
            attemptCount++;
            try
            {
                await DownloadToFileAsync(client, url, targetPath, token).ConfigureAwait(false);
                return url;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastUrl = url;
                lastError = ex;
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
            }
        }

        throw new InvalidOperationException(
            $"Tor download failed after {attemptCount} URL attempt(s). Last URL: {lastUrl ?? "n/a"}. Error: {lastError?.Message}");
    }

    private static async Task<IReadOnlyList<string>> GetTorVersionCandidatesAsync(HttpClient client, CancellationToken token)
    {
        var versionCandidates = new List<string>();
        try
        {
            var html = await client.GetStringAsync(TorBaseUrl, token).ConfigureAwait(false);
            var matches = Regex.Matches(html, "href=\"(?<ver>\\d+\\.\\d+(\\.\\d+)*)/\"");
            var versions = new List<Version>();
            foreach (Match match in matches)
            {
                if (Version.TryParse(match.Groups["ver"].Value, out var version))
                {
                    versions.Add(version);
                }
            }

            if (versions.Count > 0)
            {
                versionCandidates.AddRange(
                    versions
                        .OrderByDescending(v => v)
                        .Distinct()
                        .Take(10)
                        .Select(v => v.ToString()));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Failed to fetch latest Tor version: {ex.Message}. Using fallback.");
        }

        if (!versionCandidates.Contains(TorFallbackVersion, StringComparer.OrdinalIgnoreCase))
        {
            versionCandidates.Add(TorFallbackVersion);
        }

        return versionCandidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TrySeedBundledRuntime(
        string baseDir,
        bool includeVpnDependencies,
        Action<string> log,
        IEnumerable<string>? probeDirectories = null)
    {
        var copiedAny = false;
        var torDir = Path.Combine(baseDir, "tor");
        var vpnDir = Path.Combine(baseDir, "vpn");

        foreach (var probeDir in EnumerateBundledRuntimeProbeDirectories(probeDirectories))
        {
            copiedAny |= CopyBundledRuntimeDirectoryIfMissing(
                Path.Combine(probeDir, "tor"),
                torDir,
                preserveFileName: "pt_config.json",
                log);

            if (includeVpnDependencies)
            {
                copiedAny |= CopyBundledRuntimeDirectoryIfMissing(
                    Path.Combine(probeDir, "vpn"),
                    vpnDir,
                    preserveFileName: null,
                    log);
            }
        }

        return copiedAny;
    }

    private static IReadOnlyList<string> GetTorSuffixCandidates()
    {
        var suffixes = new List<string> { PlatformHelper.TorExpertBundlePlatformSuffix };
        if (OperatingSystem.IsMacOS())
        {
            if (suffixes.Any(s => s.Contains("aarch64", StringComparison.OrdinalIgnoreCase)))
            {
                suffixes.Add("macos-arm64");
            }
            else if (suffixes.Any(s => s.Contains("x86_64", StringComparison.OrdinalIgnoreCase)))
            {
                suffixes.Add("macos-amd64");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (suffixes.Any(s => s.Contains("aarch64", StringComparison.OrdinalIgnoreCase)))
            {
                suffixes.Add("linux-arm64");
            }
            else if (suffixes.Any(s => s.Contains("x86_64", StringComparison.OrdinalIgnoreCase)))
            {
                suffixes.Add("linux-amd64");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            suffixes.Add("windows-amd64");
        }

        return suffixes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> ResolveTorDownloadCandidatesAsync(HttpClient client, string version, IReadOnlyList<string> suffixes, CancellationToken token)
    {
        var candidates = new List<string>();
        var bases = new[]
        {
            $"{TorBaseUrl}/{version}",
            $"{TorArchiveBaseUrl}/{version}"
        };

        foreach (var suffix in suffixes)
        {
            var fileName = $"tor-expert-bundle-{suffix}-{version}.tar.gz";
            foreach (var baseUrl in bases)
            {
                candidates.Add($"{baseUrl}/{fileName}");
            }
        }

        foreach (var baseUrl in bases)
        {
            var indexedFiles = await GetTorBundleFileNamesFromIndexAsync(client, baseUrl, suffixes, token).ConfigureAwait(false);
            foreach (var indexedFile in indexedFiles)
            {
                candidates.Add($"{baseUrl}/{indexedFile}");
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> GetTorBundleFileNamesFromIndexAsync(HttpClient client, string versionBaseUrl, IReadOnlyList<string> suffixes, CancellationToken token)
    {
        try
        {
            var html = await client.GetStringAsync(versionBaseUrl.TrimEnd('/') + "/", token).ConfigureAwait(false);
            var escapedSuffixes = suffixes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(Regex.Escape)
                .ToList();
            if (escapedSuffixes.Count == 0)
            {
                return [];
            }

            var suffixPattern = string.Join("|", escapedSuffixes);
            var pattern = $"href\\s*=\\s*[\"'](?<file>tor-expert-bundle-(?:{suffixPattern})[^/\"']*\\.tar\\.gz)[\"']";
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);

            var files = matches
                .Select(match => match.Groups["file"].Value)
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                return [];
            }

            var preferred = files.FirstOrDefault(file =>
                file.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                || file.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                || file.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
                || file.Contains("arm64", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                files.RemoveAll(file => string.Equals(file, preferred, StringComparison.OrdinalIgnoreCase));
                files.Insert(0, preferred);
            }

            return files;
        }
        catch
        {
            return [];
        }
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static void EnsureUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        PlatformHelper.RunCommandSuccess("chmod", $"+x \"{path}\"");
    }

    private static void AdHocCodesignOnMacOS(string path)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(path))
        {
            return;
        }

        try
        {
            PlatformHelper.RunCommandSuccess("codesign", $"--force --sign - \"{path}\"");
        }
        catch
        {
            // Best-effort: codesign may not be available or may fail on some files
        }
    }

    private static string? FindFirstFileByName(string root, string fileName)
    {
        return Directory
            .GetFiles(root, fileName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static string? FindDirectoryByName(string root, string directoryName)
    {
        return Directory
            .GetDirectories(root, directoryName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateBundledRuntimeProbeDirectories(IEnumerable<string>? probeDirectories)
    {
        if (probeDirectories != null)
        {
            return probeDirectories
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var candidates = new List<string> { AppContext.BaseDirectory };
        var processPath = Environment.ProcessPath;
        var processDir = !string.IsNullOrWhiteSpace(processPath) ? Path.GetDirectoryName(processPath) : null;
        if (!string.IsNullOrWhiteSpace(processDir))
        {
            candidates.Add(processDir);
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool CopyBundledRuntimeDirectoryIfMissing(
        string sourceDir,
        string destinationDir,
        string? preserveFileName,
        Action<string> log)
    {
        if (!Directory.Exists(sourceDir))
        {
            return false;
        }

        var copiedFiles = 0;
        Directory.CreateDirectory(destinationDir);

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDir, relativePath);
            var destinationName = Path.GetFileName(destinationPath);

            if (File.Exists(destinationPath))
            {
                // pt_config.json is mutated at runtime (bridge cache, dnstt seeding) - never clobber it.
                if (!string.IsNullOrWhiteSpace(preserveFileName)
                    && string.Equals(destinationName, preserveFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The bundled bridge lists (bridges-*.txt) are static app data, not runtime-mutated. On
                // an upgrade the AppData copy must NOT shadow a newer bundled set - that's the bug that
                // left users connecting through a months-old, mostly-dead bridge list. Refresh them when
                // the bundled copy differs; leave all other existing runtime files untouched.
                var isBundledBridgeList = destinationName.StartsWith("bridges-", StringComparison.OrdinalIgnoreCase)
                    && destinationName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                if (!isBundledBridgeList || FilesHaveSameContent(filePath, destinationPath))
                {
                    continue;
                }
                // else: bundled bridge list changed - fall through and overwrite it.
            }

            var targetFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
            }

            File.Copy(filePath, destinationPath, overwrite: true);
            copiedFiles++;
        }

        if (copiedFiles > 0)
        {
            log($"Seeded {copiedFiles} bundled runtime file(s) from {sourceDir}.");
            return true;
        }

        return false;
    }

    private static bool FilesHaveSameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length)
            {
                return false;
            }

            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            var bufA = new byte[64 * 1024];
            var bufB = new byte[64 * 1024];
            int read;
            while ((read = sa.Read(bufA, 0, bufA.Length)) > 0)
            {
                var offset = 0;
                while (offset < read)
                {
                    var r = sb.Read(bufB, offset, read - offset);
                    if (r <= 0)
                    {
                        return false;
                    }
                    offset += r;
                }
                if (!bufA.AsSpan(0, read).SequenceEqual(bufB.AsSpan(0, read)))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // If we can't compare, assume different so the bundled copy wins (safe for static data).
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite, string? preserveFileName = null)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var destPath = filePath.Replace(sourceDir, destinationDir, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(preserveFileName)
                && string.Equals(Path.GetFileName(filePath), preserveFileName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(destPath))
            {
                continue;
            }

            var destFolder = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destFolder))
            {
                Directory.CreateDirectory(destFolder);
            }

            File.Copy(filePath, destPath, overwrite);
        }
    }
}
