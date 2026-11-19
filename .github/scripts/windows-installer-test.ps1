# Installs both Windows installers silently on the runner, checks what they put on disk, runs the
# installed CLI once and uninstalls them again. With -Signed it also checks the signatures of the
# installed programs and of the uninstallers: an uninstaller only exists once its installer has
# run, so this is the one place where its signature can be checked.
param(
  [switch]$Signed
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outDir = Join-Path $repoRoot "installer\output"
$base = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$workDir = Join-Path $base "onionhop-installer-test"

if ($Signed) {
  . (Join-Path $repoRoot "installer\signing.ps1")
}

function Test-Installer {
  param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string[]]$Programs,
    [string[]]$RunArguments = @()
  )

  $dir = Join-Path $workDir $Name
  $log = "$dir.install.log"
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  New-Item -ItemType Directory -Force -Path $workDir | Out-Null

  if ($Signed) {
    Assert-OnionHopSigned -Files @($Installer)
  }

  Write-Host "Installing $(Split-Path -Leaf $Installer) into $dir..." -ForegroundColor Cyan
  $setup = Start-Process -FilePath $Installer -Wait -PassThru -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=`"$dir`"", "/LOG=`"$log`"")
  if ($setup.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log -Tail 40 }
    throw "${Name}: the installer ended with exit code $($setup.ExitCode)."
  }

  $uninstaller = Join-Path $dir "unins000.exe"
  $installed = @($Programs | ForEach-Object { Join-Path $dir $_ }) + $uninstaller
  $missing = @($installed | Where-Object { -not (Test-Path -LiteralPath $_) })
  if ($missing.Count -gt 0) {
    throw ("${Name}: the installer did not install:`n - " + ($missing -join "`n - "))
  }
  Write-Host "${Name}: $(@(Get-ChildItem -Recurse -File $dir).Count) files installed." -ForegroundColor Green

  if ($Signed) {
    $artiHop = Join-Path $dir "artihop\artihop.exe"
    if (Test-Path -LiteralPath $artiHop) { $installed += $artiHop }
    Assert-OnionHopSigned -Files $installed
  }

  if ($RunArguments.Count -gt 0) {
    $program = Join-Path $dir $Programs[0]
    $output = & $program @RunArguments
    if ($LASTEXITCODE -ne 0) {
      throw "${Name}: '$($Programs[0]) $($RunArguments -join ' ')' ended with exit code $LASTEXITCODE."
    }
    $output | ForEach-Object { Write-Host "   $_" }
  }

  Write-Host "Uninstalling $Name..." -ForegroundColor Cyan
  $removal = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
  if ($removal.ExitCode -ne 0) {
    throw "${Name}: the uninstaller ended with exit code $($removal.ExitCode)."
  }
  # The uninstaller finishes its work from a copy of itself, shortly after it returns.
  $program = Join-Path $dir $Programs[0]
  for ($i = 0; $i -lt 60 -and (Test-Path -LiteralPath $program); $i++) { Start-Sleep -Seconds 1 }
  if (Test-Path -LiteralPath $program) {
    throw "${Name}: '$program' is still there after uninstalling."
  }
  Write-Host "${Name}: installed, checked and uninstalled." -ForegroundColor Green
}

$appInstaller = Join-Path $outDir "OnionHop-Setup-v3.exe"
$cliInstaller = @(Get-Item (Join-Path $outDir "OnionHop-CLI-Setup-*.exe"))
if (-not (Test-Path $appInstaller)) { throw "Missing $appInstaller" }
if ($cliInstaller.Count -ne 1) { throw "Expected one OnionHop-CLI-Setup-*.exe in $outDir, found $($cliInstaller.Count)." }

Test-Installer -Installer $appInstaller -Name "app" `
  -Programs @("OnionHopV3.exe", "OnionHopV3.dll", "OnionHopV3.Core.dll")
Test-Installer -Installer $cliInstaller[0].FullName -Name "cli" `
  -Programs @("OnionHopV3.Cli.exe", "OnionHopV3.Cli.dll", "OnionHopV3.Core.dll") -RunArguments @("--version")
