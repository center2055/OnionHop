# TUN mode under Tor-only mode (#94) on a throwaway Windows runner, as Administrator. In TUN mode
# apps reach Tor through the tunnel adapter, which gets a new name on every connect, and Tor's own
# connections leave through sing-box's direct route. Both need their Tor-only rules: a plain curl,
# with no proxy set, must come out as Tor.
#
# Usage: windows-tor-only-tun-test.ps1 -Cli <path to OnionHopV3.Cli.exe>
param([Parameter(Mandatory = $true)][string]$Cli)

$ErrorActionPreference = 'Stop'
$runnerGroup = 'CI runner (test only)'
$Cli = (Resolve-Path $Cli).Path
$proc = $null

function Cleanup {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    # A killed CLI cannot take the tunnel down itself; the adapter and its routes go with sing-box.
    Stop-Process -Name 'sing-box', 'tor' -Force -ErrorAction SilentlyContinue
    & $Cli tor-only off | Out-Host
    Get-NetFirewallRule -Group $runnerGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

function Fail([string]$message) {
    Write-Host "::error::$message"
    Cleanup
    exit 1
}

function Ok([string]$message) { Write-Host "ok  $message" }

& $Cli tor-only on | Out-Host
if ($LASTEXITCODE -ne 0) { Fail "tor-only on failed (exit $LASTEXITCODE)" }
foreach ($p in @(Get-Process -Name 'Runner.Listener', 'Runner.Worker' -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Sort-Object -Unique)) {
    New-NetFirewallRule -Group $runnerGroup -DisplayName "CI runner: $(Split-Path $p -Leaf)" -Direction Outbound -Action Allow -Program $p | Out-Null
}
Ok 'Tor-only mode on'

$log = Join-Path $env:RUNNER_TEMP 'tor-only-tun.log'
$proc = Start-Process -FilePath $Cli -ArgumentList 'connect', '--mode', 'tun', '--smart', 'off', '--proxy-scope', 'local' -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
$connected = $false
for ($i = 0; $i -lt 150 -and -not $proc.HasExited; $i++) {
    Start-Sleep -Seconds 2
    if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'disconnect and exit') { $connected = $true; break }
}
Get-Content $log -Tail 20 | Out-Host
Get-Content "$log.err" -Tail 10 -ErrorAction SilentlyContinue | Out-Host
if (-not $connected) { Fail 'the CLI did not connect in TUN mode under Tor-only mode' }
Ok 'connected in TUN mode under Tor-only mode'

$tunRule = @(Get-NetFirewallRule -DisplayName 'OnionHop Tor-only mode: TUN adapter' -ErrorAction SilentlyContinue)
if ($tunRule.Count -eq 0) { Fail 'no Tor-only rule for the tunnel adapter' }
$alias = ($tunRule[0] | Get-NetFirewallInterfaceFilter).InterfaceAlias
Ok "the tunnel adapter is allowed: $alias"

# No proxy: the tunnel takes this to Tor.
$out = ''
for ($i = 0; $i -lt 3; $i++) {
    $out = & curl.exe --silent --show-error --max-time 60 https://check.torproject.org/api/ip 2>&1
    if ("$out" -match '"IsTor":\s*true') { break }
    Start-Sleep -Seconds 5
}
Write-Host "through the tunnel: $out"
if ("$out" -notmatch '"IsTor":\s*true') { Fail 'traffic through the tunnel did not come out as Tor' }
Ok 'an app without proxy settings comes out as Tor through the tunnel'

Cleanup
Ok 'disconnected and Tor-only mode off'
exit 0
