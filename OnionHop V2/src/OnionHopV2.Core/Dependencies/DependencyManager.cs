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
using OnionHopV2.Core.Models;
using OnionHopV2.Core.Services;
using OnionHopV2.Core.Tor;

namespace OnionHopV2.Core.Dependencies;

internal sealed class DependencyManager
{
    private const string TorFallbackVersion = "15.0.5";
    private const string TorBaseUrl = "https://dist.torproject.org/torbrowser";
    private const string TorArchiveBaseUrl = "https://archive.torproject.org/tor-package-archive/torbrowser";
    private const string SingBoxApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";

    public sealed record DependencyUpdate(bool InProgress, string Status, double Progress);

    public async Task<bool> EnsureAsync(
        string baseDir,
        Action<DependencyUpdate> progress,
        Action<string> log,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var torDir = Path.Combine(baseDir, "tor");
        var vpnDir = Path.Combine(baseDir, "vpn");

        var torExe = Path.Combine(torDir, "tor.exe");
        var torGenCert = Path.Combine(torDir, "tor-gencert.exe");
        var geoip = Path.Combine(torDir, "geoip");
        var geoip6 = Path.Combine(torDir, "geoip6");
        var ptDir = Path.Combine(torDir, "pluggable_transports");

        var singBoxExe = Path.Combine(vpnDir, "sing-box.exe");
        var wintunDll = Path.Combine(vpnDir, "wintun.dll");

        var needsTor = !File.Exists(torExe) || !File.Exists(torGenCert) || !File.Exists(geoip) || !File.Exists(geoip6) || !Directory.Exists(ptDir);
        var needsSingBox = !File.Exists(singBoxExe);
        var needsWintun = !File.Exists(wintunDll);

        if (!needsTor && !needsSingBox && !needsWintun)
        {
            var ptConfigPath = Path.Combine(ptDir, "pt_config.json");
            EnsurePluggableTransportConfig(ptConfigPath, log);
            return true;
        }

        progress(new DependencyUpdate(true, "Preparing downloads...", 0));

        var succeeded = false;
        var tempRoot = Path.Combine(Path.GetTempPath(), "OnionHop", "deps");
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
                steps.Add(("Downloading Tor...", () => DownloadTorAsync(client, tempRoot, torExe, ptDir, baseDir)));
            }
            if (needsSingBox)
            {
                steps.Add(("Downloading sing-box...", () => DownloadSingBoxAsync(client, tempRoot, singBoxExe)));
            }
            if (needsWintun)
            {
                steps.Add(("Downloading Wintun...", () => DownloadWintunAsync(client, tempRoot, wintunDll)));
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

            succeeded = true;
            progress(new DependencyUpdate(false, "Components ready.", 1));
            return true;
        }
        catch (Exception ex)
        {
            log($"Dependency download failed: {ex.Message}");
            progress(new DependencyUpdate(false, $"Dependency download failed: {ex.Message}", 0));
            return false;
        }
        finally
        {
            if (!succeeded)
            {
                progress(new DependencyUpdate(false, "Dependency download failed.", 0));
            }

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

            var updated = false;
            config.PluggableTransports ??= new Dictionary<string, string>();

            if (!config.PluggableTransports.ContainsKey("lyrebird"))
            {
                config.PluggableTransports["lyrebird"] =
                    "ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit exec ${pt_path}lyrebird.exe";
                updated = true;
            }

            if (!config.PluggableTransports.TryGetValue("conjure", out var conjureLine)
                || string.IsNullOrWhiteSpace(conjureLine))
            {
                config.PluggableTransports["conjure"] =
                    "ClientTransportPlugin conjure exec ${pt_path}lyrebird.exe";
                updated = true;
            }

            if (!config.PluggableTransports.TryGetValue("snowflake", out var snowflakeLine)
                || string.IsNullOrWhiteSpace(snowflakeLine)
                || !snowflakeLine.Contains("snowflake-client.exe", StringComparison.OrdinalIgnoreCase))
            {
                config.PluggableTransports["snowflake"] =
                    "ClientTransportPlugin snowflake exec ${pt_path}snowflake-client.exe";
                updated = true;
            }

            config.RecommendedDefault ??= "obfs4";

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

    private static async Task DownloadTorAsync(HttpClient client, string tempRoot, string torExePath, string ptDir, string baseDir)
    {
        var version = await GetLatestTorVersionAsync(client).ConfigureAwait(false);
        var torArchivePath = Path.Combine(tempRoot, "tor.tar.gz");
        var candidates = await ResolveTorDownloadCandidatesAsync(client, version).ConfigureAwait(false);

        await DownloadWithFallbackAsync(client, candidates, torArchivePath).ConfigureAwait(false);

        await Task.Run(() =>
        {
            var extractRoot = Path.Combine(tempRoot, "tor_extract");
            if (Directory.Exists(extractRoot))
            {
                Directory.Delete(extractRoot, true);
            }
            Directory.CreateDirectory(extractRoot);
            ExtractTarGz(torArchivePath, extractRoot);

            var extractedTorRoot = Path.Combine(extractRoot, "tor");
            if (!Directory.Exists(extractedTorRoot))
            {
                throw new InvalidOperationException("Tor extraction failed or unexpected structure.");
            }

            var torDir = Path.GetDirectoryName(torExePath) ?? baseDir;
            var torGenCertPath = Path.Combine(torDir, "tor-gencert.exe");
            var geoipPath = Path.Combine(torDir, "geoip");
            var geoip6Path = Path.Combine(torDir, "geoip6");

            File.Copy(Path.Combine(extractedTorRoot, "tor.exe"), torExePath, true);
            File.Copy(Path.Combine(extractedTorRoot, "tor-gencert.exe"), torGenCertPath, true);

            var dataRoot = Path.Combine(extractRoot, "data");
            var geoipSource = Path.Combine(dataRoot, "geoip");
            var geoip6Source = Path.Combine(dataRoot, "geoip6");
            if (File.Exists(geoipSource))
            {
                File.Copy(geoipSource, geoipPath, true);
            }
            if (File.Exists(geoip6Source))
            {
                File.Copy(geoip6Source, geoip6Path, true);
            }

            var extractedPtDir = Path.Combine(extractedTorRoot, "pluggable_transports");
            if (Directory.Exists(extractedPtDir))
            {
                CopyDirectory(extractedPtDir, ptDir, overwrite: true, preserveFileName: "pt_config.json");
            }

            var obfs4proxy = Path.Combine(ptDir, "obfs4proxy.exe");
            var lyrebird = Path.Combine(ptDir, "lyrebird.exe");
            if (!File.Exists(lyrebird) && File.Exists(obfs4proxy))
            {
                File.Move(obfs4proxy, lyrebird);
            }
        }).ConfigureAwait(false);
    }

    private static async Task DownloadSingBoxAsync(HttpClient client, string tempRoot, string singBoxPath)
    {
        using var response = await client.GetAsync(SingBoxApiUrl).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Failed to query sing-box releases.");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        var asset = release?.Assets?.FirstOrDefault(a => a.Name != null
                                                        && a.Name.Contains("windows-amd64.zip", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(asset?.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("No sing-box windows-amd64 asset found.");
        }

        var zipPath = Path.Combine(tempRoot, "sing-box.zip");
        await DownloadToFileAsync(client, asset.BrowserDownloadUrl, zipPath).ConfigureAwait(false);

        await Task.Run(() =>
        {
            var extractDir = Path.Combine(tempRoot, "sing-box");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var exePath = Directory.GetFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exePath == null)
            {
                throw new FileNotFoundException("sing-box.exe not found in archive.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(singBoxPath) ?? AppContext.BaseDirectory);
            File.Copy(exePath, singBoxPath, true);
        }).ConfigureAwait(false);
    }

    private static async Task DownloadWintunAsync(HttpClient client, string tempRoot, string wintunPath)
    {
        var zipPath = Path.Combine(tempRoot, "wintun.zip");
        await DownloadToFileAsync(client, WintunUrl, zipPath).ConfigureAwait(false);

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

    private static async Task DownloadToFileAsync(HttpClient client, string url, string targetPath)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file).ConfigureAwait(false);
    }

    private static async Task DownloadWithFallbackAsync(HttpClient client, IEnumerable<string> urls, string targetPath)
    {
        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadToFileAsync(client, url, targetPath).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
            }
        }

        throw new InvalidOperationException($"Tor download failed: {lastError?.Message}");
    }

    private static async Task<string> GetLatestTorVersionAsync(HttpClient client)
    {
        try
        {
            var html = await client.GetStringAsync(TorBaseUrl).ConfigureAwait(false);
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
                return versions.OrderByDescending(v => v).First().ToString();
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Write($"Failed to fetch latest Tor version: {ex.Message}. Using fallback.");
        }

        return TorFallbackVersion;
    }

    private static async Task<IReadOnlyList<string>> ResolveTorDownloadCandidatesAsync(HttpClient client, string version)
    {
        var candidates = new List<string>();
        var fileName = $"tor-expert-bundle-windows-x86_64-{version}.tar.gz";
        var bases = new[]
        {
            $"{TorBaseUrl}/{version}",
            $"{TorArchiveBaseUrl}/{version}"
        };

        foreach (var baseUrl in bases)
        {
            candidates.Add($"{baseUrl}/{fileName}");
        }

        foreach (var baseUrl in bases)
        {
            var indexedFile = await GetTorBundleFileNameFromIndexAsync(client, baseUrl).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(indexedFile))
            {
                candidates.Add($"{baseUrl}/{indexedFile}");
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string?> GetTorBundleFileNameFromIndexAsync(HttpClient client, string versionBaseUrl)
    {
        try
        {
            var html = await client.GetStringAsync(versionBaseUrl.TrimEnd('/') + "/").ConfigureAwait(false);
            var matches = Regex.Matches(
                html,
                "href\\s*=\\s*[\"'](?<file>tor-expert-bundle-windows-[^/\"']+\\.tar\\.gz)[\"']",
                RegexOptions.IgnoreCase);

            var files = matches
                .Select(match => match.Groups["file"].Value)
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                return null;
            }

            var preferred = files.FirstOrDefault(file =>
                file.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                || file.Contains("amd64", StringComparison.OrdinalIgnoreCase));

            return preferred ?? files[0];
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
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
