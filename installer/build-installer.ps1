param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "OnionHop"
$csproj = Join-Path $projectDir "OnionHop.csproj"

if (!(Test-Path $csproj)) {
  throw "Could not find OnionHop.csproj at: $csproj"
}

$sc = "false"
if ($SelfContained.IsPresent) { $sc = "true" }

Write-Host "Publishing OnionHop..." -ForegroundColor Cyan
& dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc

$publishDir = Join-Path $projectDir "bin\$Configuration\net9.0-windows\$Runtime\publish"
if (!(Test-Path $publishDir)) {
  throw "Publish directory not found: $publishDir"
}

$iss = Join-Path $PSScriptRoot "OnionHop.iss"
if (!(Test-Path $iss)) {
  throw "Missing Inno Setup script: $iss"
}

$version = "1.0.0"
try {
  $xml = [xml](Get-Content $csproj)
  $pv = $xml.Project.PropertyGroup.Version
  if ($pv) { $version = $pv.Trim() }
} catch {
}

# Try to find ISCC.exe
$possible = @(
  "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $possible | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  throw "Inno Setup not found. Install Inno Setup 6 and ensure ISCC.exe exists in Program Files."
}

Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
& $iscc $iss /DMyAppVersion=$version /DPubDir="$publishDir"

Write-Host "Done. Installer is in: $PSScriptRoot\output" -ForegroundColor Green
