param(
    [string]$TorVersion = $env:ONIONHOP_TOR_VERSION,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

# Ensure TLS 1.2+ is used
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

# Configuration
if ([string]::IsNullOrWhiteSpace($TorVersion)) {
    $TorVersion = "latest"
}
$TorFallbackVersion = "15.0.5"
$TorBaseUrl = "https://dist.torproject.org/torbrowser"
$TorArchiveBaseUrl = "https://archive.torproject.org/tor-package-archive/torbrowser"
$SingBoxApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest"
$XrayApiUrl = "https://api.github.com/repos/XTLS/Xray-core/releases/latest"
$WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip"
# Known SHA256 checksum for Wintun 0.14.1
$WintunExpectedHash = "07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51"
$WebTunnelVersion = "v0.0.3"
$WebTunnelSourceUrl = "https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/webtunnel/-/archive/$WebTunnelVersion/webtunnel-$WebTunnelVersion.tar.gz"

$RepoRoot = $PSScriptRoot
$TorDir = Join-Path $RepoRoot "OnionHop\tor"
$VpnDir = Join-Path $RepoRoot "OnionHop\vpn"
$ArtiHopDir = Join-Path $RepoRoot "OnionHop\artihop"
$SnowflakeDir = Join-Path $RepoRoot "OnionHop\snowflake"
$PtDir = Join-Path $TorDir "pluggable_transports"
$TempDir = Join-Path $RepoRoot "temp_deps"
$ArtiHopRepoUrl = "https://github.com/center2055/ArtiHop.git"
$SnowflakeProxyPackage = "gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/snowflake/v2/proxy@latest"

# Helper to ensure directory exists
function Ensure-Dir($path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

# Helper to fetch remote text in a way that works on Windows PowerShell and PowerShell 7
function Get-RemoteText($url) {
    try {
        # PowerShell 5.x supports -UseBasicParsing and avoids legacy IE parser crashes.
        return (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
    } catch {
        try {
            return (Invoke-WebRequest -Uri $url).Content
        } catch {
            $webClient = [System.Net.WebClient]::new()
            try {
                return $webClient.DownloadString($url)
            } finally {
                $webClient.Dispose()
            }
        }
    }
}

# Helper to resolve latest Tor version with fallback
function Get-LatestTorVersion($baseUrl, $fallbackVersion) {
    try {
        $indexContent = Get-RemoteText -url $baseUrl
        $versionMatches = [regex]::Matches($indexContent, 'href\s*=\s*["''](?<ver>\d+(?:\.\d+)+)/["'']', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $versions = @($versionMatches | ForEach-Object { $_.Groups["ver"].Value } | Sort-Object -Unique)
        if ($versions.Count -eq 0) {
            Write-Warning "No Tor versions found at $baseUrl. Falling back to $fallbackVersion."
            return $fallbackVersion
        }

        $parsedVersions = @(
            $versions |
                ForEach-Object {
                    try {
                        [PSCustomObject]@{
                            Raw = $_
                            Parsed = [Version]$_
                        }
                    } catch {
                        $null
                    }
                } |
                Where-Object { $_ -ne $null } |
                Sort-Object Parsed -Descending
        )

        if ($parsedVersions.Count -gt 0) {
            return $parsedVersions[0].Raw
        }

        Write-Warning "Found Tor version entries but none were parseable. Falling back to $fallbackVersion."
    } catch {
        Write-Warning "Failed to query Tor versions from ${baseUrl}: $($_.Exception.Message). Falling back to $fallbackVersion."
    }
    return $fallbackVersion
}

function Get-TorBundleFileNameFromIndex($versionBaseUrl) {
    try {
        $content = Get-RemoteText -url $versionBaseUrl
        $fileMatches = [regex]::Matches($content, 'href\s*=\s*["''](?<file>tor-expert-bundle-windows-[^/"'']+\.tar\.gz)["'']', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $files = @($fileMatches | ForEach-Object { $_.Groups["file"].Value } | Sort-Object -Unique)
        if ($files.Count -eq 0) {
            return $null
        }

        $amd64 = $files | Where-Object { $_ -match 'x86_64|amd64' } | Select-Object -First 1
        if ($amd64) {
            return $amd64
        }

        return $files[0]
    } catch {
        return $null
    }
}

function Resolve-TorDownloadCandidates($version) {
    $candidates = New-Object System.Collections.Generic.List[string]
    $defaultName = "tor-expert-bundle-windows-x86_64-$version.tar.gz"
    $baseDirs = @(
        "$TorBaseUrl/$version",
        "$TorArchiveBaseUrl/$version"
    )

    foreach ($base in $baseDirs) {
        $candidates.Add("$base/$defaultName")
    }

    foreach ($base in $baseDirs) {
        $fileName = Get-TorBundleFileNameFromIndex -versionBaseUrl "$base/"
        if (-not [string]::IsNullOrWhiteSpace($fileName)) {
            $candidates.Add("$base/$fileName")
        }
    }

    return @($candidates | Select-Object -Unique)
}

function Get-BridgeSourceUrls($transport) {
    $encoded = [System.Uri]::EscapeDataString($transport)
    $urls = @(
        "https://bridges.torproject.org/bridges?transport=$encoded&format=plain",
        "https://bridges.torproject.org/bridges?transport=$encoded"
    )
    if ($transport -ieq "webtunnel") {
        $urls += @(
            "https://bridges.torproject.org/bridges?transport=$encoded&ipv6=yes&format=plain",
            "https://bridges.torproject.org/bridges?transport=$encoded&ipv6=yes"
        )
    }
    return @($urls | Select-Object -Unique)
}

function Get-WebTunnelBridgeLines([string[]]$urls) {
    $lines = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    $fingerprintRegex = '^[A-Fa-f0-9]{40}$'
    $fallbackPattern = 'webtunnel\s+[^\s<]+:\d{1,5}\s+[A-Fa-f0-9]{40}\s+url=[^\s<]+(?:\s+[^\s<]+=[^\s<]+)*'

    foreach ($url in $urls) {
        try {
            $content = Get-RemoteText -url $url
            if ([string]::IsNullOrWhiteSpace($content)) {
                continue
            }

            $normalized = [System.Net.WebUtility]::HtmlDecode($content)
            $normalized = [regex]::Replace($normalized, '<br\s*/?>', "`n", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            $normalized = [regex]::Replace($normalized, '</(p|div|li|h1|h2|h3|h4|h5|h6)>', "`n", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            $normalized = [regex]::Replace($normalized, '<[^>]+>', ' ')

            foreach ($raw in ($normalized -split "`r?`n")) {
                $line = if ($null -eq $raw) { "" } else { $raw.Trim() }
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                $idx = $line.IndexOf("webtunnel ", [System.StringComparison]::OrdinalIgnoreCase)
                if ($idx -lt 0) {
                    continue
                }

                $line = $line.Substring($idx)
                if ($line.StartsWith("Bridge ", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $line = $line.Substring(7).Trim()
                }

                $line = [regex]::Replace($line, '\s+', ' ').Trim().TrimEnd(',', ';', '.')
                if (-not $line.StartsWith("webtunnel ", [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                if ($line.IndexOf(" url=", [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                    continue
                }

                $parts = $line -split '\s+'
                if ($parts.Count -lt 4) {
                    continue
                }
                if ($parts[1].IndexOf(':') -lt 0) {
                    continue
                }
                if ($parts[2] -notmatch $fingerprintRegex) {
                    continue
                }

                if ($seen.Add($line)) {
                    $lines.Add($line)
                }
            }

            foreach ($match in [regex]::Matches($content, $fallbackPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                $line = [System.Net.WebUtility]::HtmlDecode($match.Value)
                $line = [regex]::Replace($line, '\s+', ' ').Trim().TrimEnd(',', ';', '.')
                if ($seen.Add($line)) {
                    $lines.Add($line)
                }
            }

            if ($lines.Count -gt 0) {
                break
            }
        } catch {
            Write-Warning "Failed to fetch webtunnel bridge lines from ${url}: $($_.Exception.Message)"
        }
    }

    return @($lines)
}

# Helper to verify file checksum (SHA256)
function Verify-FileHash($filePath, $expectedHash) {
    if ([string]::IsNullOrWhiteSpace($expectedHash)) {
        Write-Warning "No checksum provided for verification. Skipping..."
        return $true
    }
    
    $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash
    if ($actualHash -eq $expectedHash) {
        Write-Host "  Checksum verified: $actualHash" -ForegroundColor Green
        return $true
    } else {
        Write-Warning "Checksum mismatch!"
        Write-Warning "  Expected: $expectedHash"
        Write-Warning "  Actual:   $actualHash"
        return $false
    }
}

# Build the ArtiHop 2-hop engine from its own public repo and bundle only the binary.
# The ArtiHop source is intentionally NOT vendored into OnionHop — it is fetched at build time.
function Build-ArtiHop($tempDir, $artiHopDir, $repoUrl) {
    $exeName = "artihop.exe"
    $output = Join-Path $artiHopDir $exeName

    $cargo = Get-Command "cargo" -ErrorAction SilentlyContinue
    $git = Get-Command "git" -ErrorAction SilentlyContinue
    if (-not $cargo) {
        Write-Warning "cargo (Rust toolchain) not found. Skipping ArtiHop build. Install Rust from https://rustup.rs to bundle the ArtiHop 2-hop engine, or set ONIONHOP_ARTIHOP_PATH at runtime."
        return
    }
    if (-not $git) {
        Write-Warning "git not found. Skipping ArtiHop build."
        return
    }

    $cloneDir = Join-Path $tempDir "ArtiHop"
    try {
        if (Test-Path $cloneDir) { Remove-Item $cloneDir -Recurse -Force }
        Write-Host "Cloning ArtiHop source from $repoUrl ..."
        & git clone --depth 1 $repoUrl $cloneDir
        if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)." }

        Write-Host "Building ArtiHop (cargo build --release). This compiles Arti and can take several minutes..."
        Push-Location $cloneDir
        try {
            & cargo build --release
            if ($LASTEXITCODE -ne 0) { throw "cargo build failed (exit $LASTEXITCODE)." }
        } finally {
            Pop-Location
        }

        $built = Join-Path $cloneDir "target\release\$exeName"
        if (-not (Test-Path $built)) { throw "Built artihop.exe not found at $built." }

        Copy-Item $built $output -Force
        Write-Host "ArtiHop engine built and bundled: $output" -ForegroundColor Green
    } catch {
        Write-Warning "ArtiHop build failed: $($_.Exception.Message). The ArtiHop engine will be unavailable; users can still set ONIONHOP_ARTIHOP_PATH or use the Classic/Arti engines."
    }
}

# Build the standalone Snowflake proxy so users can volunteer as a Snowflake bridge.
# Uses `go install` (via the Go module proxy) so we don't depend on direct gitlab access.
function Build-SnowflakeProxy($tempDir, $snowflakeDir, $package) {
    $exeName = "snowflake-proxy.exe"
    $output = Join-Path $snowflakeDir $exeName

    $go = Get-Command "go" -ErrorAction SilentlyContinue
    if (-not $go) {
        Write-Warning "Go not found. Skipping Snowflake proxy build. Install Go (https://go.dev/dl) to bundle the Snowflake volunteer proxy, or set ONIONHOP_SNOWFLAKE_PROXY_PATH at runtime."
        return
    }

    $gobin = Join-Path $tempDir "gobin"
    Ensure-Dir $gobin
    $prevGobin = $env:GOBIN
    $prevCgo = $env:CGO_ENABLED
    try {
        $env:GOBIN = $gobin
        $env:CGO_ENABLED = "0"
        Write-Host "Building Snowflake proxy (go install $package)... this can take a few minutes."
        & go install $package
        if ($LASTEXITCODE -ne 0) { throw "go install failed (exit $LASTEXITCODE)." }

        # `go install .../proxy@latest` emits a binary named after the package dir ("proxy").
        $built = Join-Path $gobin "proxy.exe"
        if (-not (Test-Path $built)) {
            $built = Get-ChildItem -Path $gobin -Filter "*.exe" | Select-Object -First 1 | ForEach-Object { $_.FullName }
        }
        if (-not $built -or -not (Test-Path $built)) { throw "Built proxy binary not found in $gobin." }

        Copy-Item $built $output -Force
        Write-Host "Snowflake proxy built and bundled: $output" -ForegroundColor Green
    } catch {
        Write-Warning "Snowflake proxy build failed: $($_.Exception.Message). The Snowflake volunteer feature will be unavailable; users can set ONIONHOP_SNOWFLAKE_PROXY_PATH."
    } finally {
        $env:GOBIN = $prevGobin
        $env:CGO_ENABLED = $prevCgo
    }
}

# Helper to pause on exit
function Exit-WithPause($code) {
    if (-not $NoPause -and $Host.Name -eq "ConsoleHost") {
        Write-Host "`nPress any key to exit..."
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
    exit $code
}

try {
    Write-Host "=== OnionHop Dependency Downloader ==="

    # Cleanup temp (best effort; stale locks should not abort a full dependency update)
    if (Test-Path $TempDir) {
        try {
            Remove-Item $TempDir -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Warning "Could not fully remove temp directory '$TempDir' before start: $($_.Exception.Message)"
        }
    }
    Ensure-Dir $TempDir
    Ensure-Dir $TorDir
    Ensure-Dir $VpnDir
    Ensure-Dir $ArtiHopDir
    Ensure-Dir $SnowflakeDir
    Ensure-Dir $PtDir

    if ($TorVersion -eq "latest") {
        $TorVersion = Get-LatestTorVersion -baseUrl $TorBaseUrl -fallbackVersion $TorFallbackVersion
    }
    $TorCandidates = Resolve-TorDownloadCandidates -version $TorVersion

    # --- 1. Tor Expert Bundle ---
    Write-Host "`n[1/4] Downloading Tor Expert Bundle ($TorVersion)..."
    $TorArchive = Join-Path $TempDir "tor.tar.gz"
    $TorDownloaded = $false
    foreach ($candidate in $TorCandidates) {
        try {
            Invoke-WebRequest -Uri $candidate -OutFile $TorArchive
            $TorDownloaded = $true
            break
        } catch {
            if (Test-Path $TorArchive) { Remove-Item $TorArchive -Force }
            Write-Warning "Failed to download Tor from ${candidate}: $($_.Exception.Message)"
        }
    }
    if (-not $TorDownloaded) {
        throw "Failed to download Tor from known URLs."
    }

    Write-Host "Extracting Tor..."
    if (Get-Command "tar" -ErrorAction SilentlyContinue) {
        tar -xf $TorArchive -C $TempDir
    } else {
        throw "The 'tar' command is required to extract the Tor Expert Bundle but was not found."
    }

    # Check structure
    $ExtractedTorRoot = Join-Path $TempDir "tor"
    if (-not (Test-Path $ExtractedTorRoot)) {
        Write-Warning "Expected 'tor' directory in archive not found. Listing temp contents:"
        Get-ChildItem $TempDir | ForEach-Object { Write-Host " - $($_.Name)" }
        throw "Tor extraction failed or unexpected structure."
    }

    Write-Host "Installing Tor binaries..."
    Copy-Item (Join-Path $ExtractedTorRoot "tor.exe") $TorDir -Force
    Copy-Item (Join-Path $ExtractedTorRoot "tor-gencert.exe") $TorDir -Force

    $ExtractedDataRoot = Join-Path $TempDir "data"
    if (Test-Path $ExtractedDataRoot) {
        Copy-Item (Join-Path $ExtractedDataRoot "geoip") $TorDir -Force
        Copy-Item (Join-Path $ExtractedDataRoot "geoip6") $TorDir -Force
    } else {
        Write-Warning "Could not find 'data' directory for geoip files."
    }

    Write-Host "Installing Pluggable Transports..."
    $ExtractedPtDir = Join-Path $ExtractedTorRoot "pluggable_transports"
    if (Test-Path $ExtractedPtDir) {
        Copy-Item "$ExtractedPtDir\*" $PtDir -Recurse -Force
    }

    # Handle renamed binaries
    if (-not (Test-Path (Join-Path $PtDir "lyrebird.exe")) -and (Test-Path (Join-Path $PtDir "obfs4proxy.exe"))) {
        Write-Host "Renaming obfs4proxy.exe to lyrebird.exe..."
        Rename-Item -Path (Join-Path $PtDir "obfs4proxy.exe") -NewName "lyrebird.exe" -Force
    }

    # --- 2. Webtunnel client ---
    Write-Host "`n[2/4] Preparing webtunnel-client..."
    # Build webtunnel-client from source if Go is available; otherwise try Tor Browser copy.
    $WebTunnelClientName = "webtunnel-client.exe"
    $WebTunnelOutput = Join-Path $PtDir $WebTunnelClientName
    $WebTunnelBuilt = $false
    $GoCommand = Get-Command "go" -ErrorAction SilentlyContinue
    if ($GoCommand) {
        try {
            Write-Host "Building webtunnel-client from source ($WebTunnelVersion)..."
            $WebTunnelArchive = Join-Path $TempDir "webtunnel.tar.gz"
            Invoke-WebRequest -Uri $WebTunnelSourceUrl -OutFile $WebTunnelArchive
            tar -xf $WebTunnelArchive -C $TempDir
            $WebTunnelDir = Get-ChildItem -Path $TempDir -Directory | Where-Object { $_.Name -like "webtunnel-*" } | Select-Object -First 1
            if (-not $WebTunnelDir) { throw "WebTunnel extraction failed." }

            $prevCgo = $env:CGO_ENABLED
            $prevGoos = $env:GOOS
            $prevGoarch = $env:GOARCH
            $env:CGO_ENABLED = "0"
            $env:GOOS = "windows"
            $env:GOARCH = "amd64"
            Push-Location $WebTunnelDir.FullName
            go build -ldflags "-s -w" -o $WebTunnelOutput ".\\main\\client"
            Pop-Location
            $env:CGO_ENABLED = $prevCgo
            $env:GOOS = $prevGoos
            $env:GOARCH = $prevGoarch

            if (Test-Path $WebTunnelOutput) {
                Write-Host "Built webtunnel-client.exe."
                $WebTunnelBuilt = $true
            }
        } catch {
            Write-Warning "Webtunnel build failed: $($_.Exception.Message)"
        }
    } else {
        Write-Warning "Go not found. Skipping webtunnel build."
    }

    if (-not $WebTunnelBuilt -and -not (Test-Path $WebTunnelOutput)) {
        $WebTunnelCandidates = @(
            (Join-Path $env:LOCALAPPDATA "Tor Browser\Browser\TorBrowser\Tor\PluggableTransports\$WebTunnelClientName"),
            (Join-Path ${env:ProgramFiles} "Tor Browser\Browser\TorBrowser\Tor\PluggableTransports\$WebTunnelClientName"),
            (Join-Path ${env:ProgramFiles(x86)} "Tor Browser\Browser\TorBrowser\Tor\PluggableTransports\$WebTunnelClientName")
        ) | Where-Object { $_ -and (Test-Path $_) }

        $WebTunnelSource = $WebTunnelCandidates | Select-Object -First 1
        if ($WebTunnelSource) {
            Copy-Item $WebTunnelSource $PtDir -Force
            Write-Host "Copied webtunnel-client.exe from Tor Browser."
        } else {
            Write-Warning "webtunnel-client.exe not found. Install Go to build it, or install Tor Browser and copy it into $PtDir."
        }
    }

    Write-Host "`n[2b/4] Preparing dnstt-client (DNS-tunnel transport)..."
    # dnstt-client is a standalone DNS-tunnel forwarder (not a Tor PT); OnionHop launches it and
    # points Tor at its local listener. Built from David Fifield's dnstt. Optional: if Go is missing
    # or the build fails, the app still runs but dnstt connect mode is unavailable.
    $DnsttOutput = Join-Path $PtDir "dnstt-client.exe"
    $DnsttGo = Get-Command "go" -ErrorAction SilentlyContinue
    if (Test-Path $DnsttOutput) {
        Write-Host "dnstt-client.exe already present."
    } elseif (-not $DnsttGo) {
        Write-Warning "Go not found. Skipping dnstt-client build; dnstt connect mode will be unavailable."
    } else {
        try {
            $DnsttClone = Join-Path $TempDir "dnstt"
            if (Test-Path $DnsttClone) { Remove-Item -Recurse -Force $DnsttClone }
            Write-Host "Cloning dnstt (David Fifield) and building dnstt-client from source..."
            & git clone --depth 1 https://www.bamsoftware.com/git/dnstt.git $DnsttClone
            if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)." }

            $prevCgo = $env:CGO_ENABLED; $prevGoos = $env:GOOS; $prevGoarch = $env:GOARCH
            $env:CGO_ENABLED = "0"; $env:GOOS = "windows"; $env:GOARCH = "amd64"
            Push-Location (Join-Path $DnsttClone "dnstt-client")
            go build -ldflags "-s -w" -o $DnsttOutput "."
            Pop-Location
            $env:CGO_ENABLED = $prevCgo; $env:GOOS = $prevGoos; $env:GOARCH = $prevGoarch

            if (Test-Path $DnsttOutput) {
                Write-Host "Built dnstt-client.exe."
            } else {
                Write-Warning "dnstt-client build produced no binary; dnstt connect mode will be unavailable."
            }
        } catch {
            Write-Warning "dnstt-client build failed: $($_.Exception.Message). dnstt connect mode will be unavailable until built."
        }
    }

    $PtConfigPath = Join-Path $PtDir "pt_config.json"
    if (Test-Path $PtConfigPath) {
        $ptConfig = Get-Content $PtConfigPath -Raw | ConvertFrom-Json
        if ($null -ne $ptConfig.pluggableTransports) {
            if ($ptConfig.pluggableTransports -is [System.Collections.IDictionary]) {
                $ptConfig.pluggableTransports["lyrebird"] = "ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit exec `${pt_path}lyrebird.exe"
                $ptConfig.pluggableTransports["conjure"] = "ClientTransportPlugin conjure exec `${pt_path}lyrebird.exe"
                $ptConfig.pluggableTransports["webtunnel"] = "ClientTransportPlugin webtunnel exec `${pt_path}webtunnel-client.exe"
            } else {
                $ptConfig.pluggableTransports | Add-Member -NotePropertyName "lyrebird" -NotePropertyValue "ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit exec `${pt_path}lyrebird.exe" -Force
                $ptConfig.pluggableTransports | Add-Member -NotePropertyName "conjure" -NotePropertyValue "ClientTransportPlugin conjure exec `${pt_path}lyrebird.exe" -Force
                $ptConfig.pluggableTransports | Add-Member -NotePropertyName "webtunnel" -NotePropertyValue "ClientTransportPlugin webtunnel exec `${pt_path}webtunnel-client.exe" -Force
            }
        }

        if ($null -eq $ptConfig.bridges) {
            $ptConfig | Add-Member -NotePropertyName "bridges" -NotePropertyValue @{} -Force
        }

        $existingWebTunnel = @()
        if ($ptConfig.bridges -is [System.Collections.IDictionary] -and $ptConfig.bridges.Contains("webtunnel")) {
            $existingWebTunnel = @($ptConfig.bridges["webtunnel"])
        } elseif ($ptConfig.bridges.PSObject.Properties.Name -contains "webtunnel") {
            $existingWebTunnel = @($ptConfig.bridges.webtunnel)
        }

        $usableExisting = @($existingWebTunnel | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($usableExisting.Count -eq 0) {
            $fetchedWebTunnel = Get-WebTunnelBridgeLines -urls (Get-BridgeSourceUrls -transport "webtunnel")
            if ($fetchedWebTunnel.Count -gt 0) {
                if ($ptConfig.bridges -is [System.Collections.IDictionary]) {
                    $ptConfig.bridges["webtunnel"] = @($fetchedWebTunnel)
                } else {
                    $ptConfig.bridges | Add-Member -NotePropertyName "webtunnel" -NotePropertyValue @($fetchedWebTunnel) -Force
                }
                Write-Host "Added $($fetchedWebTunnel.Count) webtunnel bridge line(s) to pt_config.json."
            } else {
                Write-Warning "No usable webtunnel bridge lines fetched; users may need to paste custom webtunnel bridges."
            }
        }

        $ptConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $PtConfigPath
        Write-Host "Updated pt_config.json for webtunnel-client."
    }

    # --- 3-5. Sing-box, xray and Wintun (parallel download) ---
    Write-Host "`n[3-5/5] Downloading Sing-box, xray and Wintun in parallel..."
    
    # Get sing-box URL first (API call)
    $SbRelease = Invoke-RestMethod -Uri $SingBoxApiUrl
    $SbAsset = $SbRelease.assets | Where-Object { $_.name -like "*windows-amd64.zip" } | Select-Object -First 1
    if (-not $SbAsset) { throw "No windows-amd64 asset found." }
    $SbUrl = $SbAsset.browser_download_url

    # Get xray URL
    $XrayRelease = Invoke-RestMethod -Uri $XrayApiUrl
    $XrayAsset = $XrayRelease.assets | Where-Object { $_.name -like "*windows-64.zip" -or $_.name -like "*win7-64.zip" } | Select-Object -First 1
    if (-not $XrayAsset) { throw "No xray windows-64 asset found." }
    $XrayUrl = $XrayAsset.browser_download_url
    
    $SbArchive = Join-Path $TempDir "sing-box.zip"
    $XrayArchive = Join-Path $TempDir "xray.zip"
    $WintunArchive = Join-Path $TempDir "wintun.zip"
    
    # Download all archives in parallel using background jobs
    Write-Host "  Starting parallel downloads..."
    $downloadJobs = @(
        Start-Job -ScriptBlock {
            param($url, $outFile)
            Invoke-WebRequest -Uri $url -OutFile $outFile
        } -ArgumentList $SbUrl, $SbArchive

        Start-Job -ScriptBlock {
            param($url, $outFile)
            Invoke-WebRequest -Uri $url -OutFile $outFile
        } -ArgumentList $XrayUrl, $XrayArchive
        
        Start-Job -ScriptBlock {
            param($url, $outFile)
            Invoke-WebRequest -Uri $url -OutFile $outFile
        } -ArgumentList $WintunUrl, $WintunArchive
    )
    
    Write-Host "  Waiting for downloads to complete..."
    $downloadJobs | Wait-Job | Out-Null
    
    # Check for errors
    foreach ($job in $downloadJobs) {
        if ($job.State -eq "Failed") {
            $errorMsg = $job | Receive-Job -ErrorAction SilentlyContinue
            $downloadJobs | Remove-Job -Force
            throw "Download failed: $errorMsg"
        }
    }
    $downloadJobs | Remove-Job -Force
    Write-Host "  Downloads completed." -ForegroundColor Green
    
    # Verify Wintun checksum
    Write-Host "Verifying Wintun checksum..."
    if (-not (Verify-FileHash -filePath $WintunArchive -expectedHash $WintunExpectedHash)) {
        throw "Wintun download failed checksum verification. The file may be corrupted or tampered with."
    }
    
    # Extract both archives
    Write-Host "Extracting Sing-box..."
    Expand-Archive -Path $SbArchive -DestinationPath $TempDir -Force
    $SbExtractedDir = Get-ChildItem -Path $TempDir -Directory | Where-Object { $_.Name -like "sing-box-*" } | Select-Object -First 1
    Copy-Item (Join-Path $SbExtractedDir.FullName "sing-box.exe") $VpnDir -Force

    Write-Host "Extracting xray..."
    $XrayExtractDir = Join-Path $TempDir "xray"
    Expand-Archive -Path $XrayArchive -DestinationPath $XrayExtractDir -Force
    $XrayExe = Get-ChildItem -Path $XrayExtractDir -Recurse -Filter "xray.exe" | Select-Object -First 1
    if (-not $XrayExe) { throw "xray.exe not found in downloaded archive." }
    Copy-Item $XrayExe.FullName $VpnDir -Force
    
    Write-Host "Extracting Wintun..."
    Expand-Archive -Path $WintunArchive -DestinationPath (Join-Path $TempDir "wintun") -Force
    Copy-Item (Join-Path $TempDir "wintun\wintun\bin\amd64\wintun.dll") $VpnDir -Force

    # --- 6. ArtiHop 2-hop engine (built from source; optional) ---
    Write-Host "`n[6] Building ArtiHop 2-hop engine..."
    Build-ArtiHop -tempDir $TempDir -artiHopDir $ArtiHopDir -repoUrl $ArtiHopRepoUrl

    # --- 7. Snowflake volunteer proxy (built from source; optional) ---
    Write-Host "`n[7] Building Snowflake volunteer proxy..."
    Build-SnowflakeProxy -tempDir $TempDir -snowflakeDir $SnowflakeDir -package $SnowflakeProxyPackage

    # Cleanup (best effort)
    if (Test-Path $TempDir) {
        try {
            Remove-Item $TempDir -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Warning "Could not fully remove temp directory '$TempDir' after completion: $($_.Exception.Message)"
        }
    }

    Write-Host "`nDone! Binaries updated." -ForegroundColor Green
    Exit-WithPause 0

} catch {
    Write-Error "An error occurred: $_"
    Exit-WithPause 1
}
