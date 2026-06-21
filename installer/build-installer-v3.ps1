param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$FrameworkDependent,
  [switch]$SelfContained,
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
$projectDir = Join-Path $solutionRoot "src\OnionHopV3.App"
$csproj = Join-Path $projectDir "OnionHopV3.App.csproj"

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

if ($FrameworkDependent.IsPresent -and $SelfContained.IsPresent) {
  throw "Use either -FrameworkDependent or -SelfContained, not both. Installer builds are self-contained by default."
}

$sc = if ($FrameworkDependent.IsPresent) { "false" } else { "true" }

$publishMode = if ($sc -eq "true") { "self-contained" } else { "framework-dependent" }
Write-Host "Publish mode: $publishMode" -ForegroundColor Cyan

Write-Host "Publishing OnionHop V3..." -ForegroundColor Cyan

if ($Clean.IsPresent) {
  Write-Host "Cleaning previous build artifacts..." -ForegroundColor Cyan
  Remove-PathWithRetry -Path "$projectDir\bin"
  Remove-PathWithRetry -Path "$projectDir\obj"
}

$publishDir = Join-Path $projectDir "bin\$Configuration\net9.0\$Runtime\publish"
Remove-PathWithRetry -Path $publishDir

& dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=false

if (!(Test-Path $publishDir)) {
  throw "Publish directory not found: $publishDir"
}

Copy-OptionalArtiRuntime -RepoRoot $repoRoot -PublishDir $publishDir -ArtiPath $ArtiPath

# Guard against the intermittent "dropped apphost" race: real-time AV can briefly lock the freshly
# published OnionHopV3.exe so it is absent when Inno packages the folder, producing an installer that
# is missing the main launcher -> users get "CreateProcess failed; code 2 - file not found" and the app
# never starts (this actually shipped in v3.0.2). Verify the apphost is present; retry publish once if a
# transient scan ate it, then fail loudly rather than ever shipping a launcher-less installer.
$appHost = Join-Path $publishDir "OnionHopV3.exe"
if (!(Test-Path $appHost)) {
  Write-Warning "OnionHopV3.exe is missing from the publish output (likely an AV/file-lock race). Re-running publish once..."
  Start-Sleep -Seconds 2
  & dotnet publish $csproj -c $Configuration -r $Runtime --self-contained $sc /p:PublishSingleFile=false /p:PublishReadyToRun=false
}
if (!(Test-Path $appHost)) {
  throw "Build aborted: OnionHopV3.exe (the app launcher) is missing from '$publishDir'. The installer would be broken (CreateProcess failed; code 2). Add a Microsoft Defender exclusion for the build output folder, then rebuild."
}
Write-Host "Verified app launcher present: $appHost" -ForegroundColor Green

$iss = Join-Path $PSScriptRoot "OnionHopV3.iss"
if (!(Test-Path $iss)) {
  throw "Missing Inno Setup script: $iss"
}

$version = "3.4.1"
try {
  $xml = [xml](Get-Content $csproj)
  $pv = $xml.Project.PropertyGroup.Version
  if ($pv) { $version = $pv.Trim() }
} catch {
}

# Try to find ISCC.exe
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

Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
& $iscc $iss /DMyAppVersion=$version /DPubDir="$publishDir"

Write-Host "Done. Installer is in: $PSScriptRoot\output" -ForegroundColor Green
