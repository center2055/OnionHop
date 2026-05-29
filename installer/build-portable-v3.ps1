param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$FrameworkDependent,
  [switch]$SkipDependencies,
  [switch]$Clean,
  [string]$ArtiPath = ""
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

function Copy-OptionalArtiRuntime {
  param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$ArtiPath = ""
  )

  $targetArtiDir = Join-Path $PublishDir "arti"
  $targetArtiExe = Join-Path $PublishDir "arti.exe"
  Remove-PathWithRetry -Path $targetArtiDir
  if (Test-Path $targetArtiExe) {
    Remove-Item -Force $targetArtiExe
  }

  $explicitArtiPath = if (-not [string]::IsNullOrWhiteSpace($ArtiPath)) { $ArtiPath } else { $env:ONIONHOP_ARTI_PATH }
  if (-not [string]::IsNullOrWhiteSpace($explicitArtiPath) -and (Test-Path $explicitArtiPath -ErrorAction SilentlyContinue)) {
    $item = Get-Item $explicitArtiPath
    if ($item.PSIsContainer) {
      Copy-Item -Path $item.FullName -Destination $targetArtiDir -Recurse -Force
      Write-Host "Arti runtime copied from explicit directory." -ForegroundColor Green
    } else {
      Copy-Item -Path $item.FullName -Destination $targetArtiExe -Force
      Write-Host "Arti runtime copied from explicit path." -ForegroundColor Green
    }
    return
  }

  $bundledArtiDir = Join-Path $RepoRoot "OnionHop\arti"
  if (Test-Path $bundledArtiDir) {
    Copy-Item -Path $bundledArtiDir -Destination $targetArtiDir -Recurse -Force
    Write-Host "Bundled Arti runtime copied from OnionHop\arti." -ForegroundColor Green
    return
  }

  $cargoArti = Join-Path $env:USERPROFILE ".cargo\bin\arti.exe"
  $candidatePaths = @($cargoArti)
  $candidatePaths += ($env:PATH -split [IO.Path]::PathSeparator |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object {
      try {
        [IO.Path]::Combine($_, "arti.exe")
      } catch {
        $null
      }
    })

  $artiCommand = $candidatePaths |
    Where-Object { $_ -and (Test-Path $_ -ErrorAction SilentlyContinue) } |
    Select-Object -First 1
  if ($artiCommand) {
    Copy-Item -Path $artiCommand -Destination $targetArtiExe -Force
    Write-Host "Arti runtime copied from local tool path." -ForegroundColor Green
    return
  }

  Write-Warning "Arti runtime was not found. Pass -ArtiPath, place it in OnionHop\arti, set ONIONHOP_ARTI_PATH, or install it into PATH to bundle Arti mode."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionRoot = Join-Path $repoRoot "OnionHop"
$projectDir = Join-Path $solutionRoot "src\\OnionHopV2.App"
$csproj = Join-Path $projectDir "OnionHopV2.App.csproj"

if (!(Test-Path $csproj)) {
  throw "Could not find OnionHop app project at: $csproj"
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

Write-Host "Publishing OnionHop V3 (portable package)..." -ForegroundColor Cyan

if ($Clean.IsPresent) {
  Write-Host "Cleaning previous build artifacts..." -ForegroundColor Cyan
  Remove-PathWithRetry -Path "$projectDir\\bin"
  Remove-PathWithRetry -Path "$projectDir\\obj"
}

$publishDir = Join-Path $projectDir "bin\\$Configuration\\net9.0\\$Runtime\\publish"
Remove-PathWithRetry -Path $publishDir

& dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=true

if (!(Test-Path $publishDir)) {
  throw "Publish directory not found: $publishDir"
}

Copy-OptionalArtiRuntime -RepoRoot $repoRoot -PublishDir $publishDir -ArtiPath $ArtiPath

$version = "0.0.0"
try {
  $xml = [xml](Get-Content $csproj)
  $pv = $xml.Project.PropertyGroup.Version
  if ($pv) { $version = $pv.Trim() }
} catch {
}

$outDir = Join-Path $PSScriptRoot "output"
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$zipName = "OnionHopV3-Portable-$version-$Runtime.zip"
$zipPath = Join-Path $outDir $zipName

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Done. Portable ZIP is in: $zipPath" -ForegroundColor Green
