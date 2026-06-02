param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$FrameworkDependent,
  [switch]$SkipDependencies
)

$ErrorActionPreference = "Stop"

function Remove-PathWithRetry {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$Retries = 6,
    [int]$DelayMs = 300
  )

  if (!(Test-Path $Path)) {
    return
  }

  for ($attempt = 1; $attempt -le $Retries; $attempt++) {
    try {
      Remove-Item -Recurse -Force $Path -ErrorAction Stop
      return
    } catch {
      if (!(Test-Path $Path)) {
        return
      }
      if ($attempt -eq $Retries) {
        throw
      }
      Start-Sleep -Milliseconds $DelayMs
    }
  }
}

function Assert-RequiredRuntimeDependencies {
  param(
    [Parameter(Mandatory = $true)][string]$RepoRoot
  )

  $required = @(
    "OnionHop\\tor\\tor.exe",
    "OnionHop\\tor\\geoip",
    "OnionHop\\tor\\geoip6",
    "OnionHop\\tor\\pluggable_transports\\pt_config.json",
    "OnionHop\\tor\\pluggable_transports\\lyrebird.exe",
    "OnionHop\\tor\\pluggable_transports\\snowflake-client.exe",
    "OnionHop\\tor\\pluggable_transports\\conjure-client.exe",
    "OnionHop\\tor\\pluggable_transports\\webtunnel-client.exe",
    "OnionHop\\vpn\\sing-box.exe",
    "OnionHop\\vpn\\xray.exe",
    "OnionHop\\vpn\\wintun.dll"
  ) | ForEach-Object { Join-Path $RepoRoot $_ }

  $missing = $required | Where-Object { -not (Test-Path $_) }
  if ($missing.Count -gt 0) {
    throw ("Required runtime files are missing:`n - " + ($missing -join "`n - "))
  }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionRoot = Join-Path $repoRoot "OnionHop"
$projectDir = Join-Path $solutionRoot "src\OnionHopV3.Cli"
$csproj = Join-Path $projectDir "OnionHopV3.Cli.csproj"

if (!(Test-Path $csproj)) {
  throw "Could not find OnionHopV3.Cli.csproj at: $csproj"
}

$depsScript = Join-Path $repoRoot "download-deps.ps1"
if (-not $SkipDependencies) {
  if (!(Test-Path $depsScript)) {
    throw "Could not find download-deps.ps1 at: $depsScript"
  }

  Write-Host "Downloading dependencies..." -ForegroundColor Cyan
  & powershell -NoProfile -ExecutionPolicy Bypass -File $depsScript -NoPause
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "Dependency download failed (exit code $LASTEXITCODE). Attempting to continue if dependencies are already present..."
  }
}

Assert-RequiredRuntimeDependencies -RepoRoot $repoRoot
Write-Host "Runtime dependencies verified." -ForegroundColor Green

$sc = if ($FrameworkDependent.IsPresent) { "false" } else { "true" }

Write-Host "Cleaning and Publishing OnionHop CLI..." -ForegroundColor Cyan

Remove-PathWithRetry -Path "$projectDir\bin"
Remove-PathWithRetry -Path "$projectDir\obj"

& dotnet clean $solutionRoot -c $Configuration
& dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=false

$publishDir = Join-Path $projectDir "bin\$Configuration\net9.0\$Runtime\publish"
if (!(Test-Path $publishDir)) {
  throw "Publish directory not found: $publishDir"
}

# Guard against the intermittent "dropped apphost" race (real-time AV briefly locking the freshly
# published exe so it is absent when Inno packages the folder -> launcher-less installer). Retry once,
# then fail loudly rather than ship a CLI installer that can't start.
$cliHost = Join-Path $publishDir "OnionHopV3.Cli.exe"
if (!(Test-Path $cliHost)) {
  Write-Warning "OnionHopV3.Cli.exe is missing from the publish output (likely an AV/file-lock race). Re-running publish once..."
  Start-Sleep -Seconds 2
  & dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=false
}
if (!(Test-Path $cliHost)) {
  throw "Build aborted: OnionHopV3.Cli.exe (the CLI launcher) is missing from '$publishDir'. The installer would be broken (CreateProcess failed; code 2)."
}

$iss = Join-Path $PSScriptRoot "OnionHopV3.Cli.iss"
if (!(Test-Path $iss)) {
  throw "Missing Inno Setup script: $iss"
}

$version = "2.4.0"
try {
  $xml = [xml](Get-Content $csproj)
  $pv = $xml.Project.PropertyGroup.Version
  if ($pv) { $version = $pv.Trim() }
} catch {
}

$possible = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

# Also honor the install location recorded in the registry so a non-default install drive
# (e.g. D:\Programs\Inno Setup 6) or a winget/user-scope install is still found.
try {
  $uninstallKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
  )
  Get-ItemProperty $uninstallKeys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "Inno Setup*" -and $_.InstallLocation } |
    ForEach-Object { $possible += (Join-Path $_.InstallLocation "ISCC.exe") }
} catch {
}

$iscc = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  throw "Inno Setup not found. Install Inno Setup 6 and ensure ISCC.exe exists in Program Files."
}

Write-Host "Building CLI installer with Inno Setup..." -ForegroundColor Cyan
& $iscc $iss /DMyAppVersion=$version /DPubDir="$publishDir"

Write-Host "Done. CLI installer is in: $PSScriptRoot\output" -ForegroundColor Green
