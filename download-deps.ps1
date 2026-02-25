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
$PtDir = Join-Path $TorDir "pluggable_transports"
$TempDir = Join-Path $RepoRoot "temp_deps"

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
