param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$FrameworkDependent,
  [switch]$SelfContained,
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
$projectDir = Join-Path $solutionRoot "src\OnionHopV2.App"
$csproj = Join-Path $projectDir "OnionHopV2.App.csproj"

if (!(Test-Path $csproj)) {
  throw "Could not find OnionHopV2.App.csproj at: $csproj"
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

if ($FrameworkDependent.IsPresent -and $SelfContained.IsPresent) {
  throw "Use either -FrameworkDependent or -SelfContained, not both. Installer builds are self-contained by default."
}

$sc = if ($FrameworkDependent.IsPresent) { "false" } else { "true" }

$publishMode = if ($sc -eq "true") { "self-contained" } else { "framework-dependent" }
Write-Host "Publish mode: $publishMode" -ForegroundColor Cyan

Write-Host "Cleaning and Publishing OnionHop V2..." -ForegroundColor Cyan

# Remove old build artifacts to ensure a fresh publish
Remove-PathWithRetry -Path "$projectDir\bin"
Remove-PathWithRetry -Path "$projectDir\obj"

& dotnet clean $solutionRoot -c $Configuration
& dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=false

$publishDir = Join-Path $projectDir "bin\$Configuration\net9.0\$Runtime\publish"
if (!(Test-Path $publishDir)) {
  throw "Publish directory not found: $publishDir"
}

$iss = Join-Path $PSScriptRoot "OnionHopV2.iss"
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

# Try to find ISCC.exe
$possible = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  throw "Inno Setup not found. Install Inno Setup 6 and ensure ISCC.exe exists in Program Files."
}

Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
& $iscc $iss /DMyAppVersion=$version /DPubDir="$publishDir"

Write-Host "Done. Installer is in: $PSScriptRoot\output" -ForegroundColor Green
