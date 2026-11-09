# End-to-end test of Tor-only mode (#94) on a throwaway Windows runner, as Administrator: with the
# mode on, Windows Firewall lets nothing out but OnionHop's Tor, the CLI still connects and Tor works
# through its SOCKS port; with it off, the firewall and every other program's rule are back as before.
#
# Usage: windows-tor-only-test.ps1 -Cli <path to OnionHopV3.Cli.exe>
param([Parameter(Mandatory = $true)][string]$Cli)

$ErrorActionPreference = 'Stop'
$group = 'OnionHop Tor-only mode'
$runnerGroup = 'CI runner (test only)'

function Fail([string]$message) {
    Write-Host "::error::$message"
    # Never leave the runner cut off, whatever failed.
    & $Cli tor-only off | Out-Host
    Get-NetFirewallRule -Group $runnerGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    exit 1
}

function Ok([string]$message) { Write-Host "ok  $message" }

# A program OnionHop does not know, so not in Tor-only mode's allow list.
function Direct-Works {
    $out = & curl.exe --silent --show-error --max-time 15 https://check.torproject.org/api/ip 2>&1
    return ($LASTEXITCODE -eq 0 -and "$out" -match '"IsTor"')
}

function Outbound-Defaults {
    return (Get-NetFirewallProfile -PolicyStore PersistentStore | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.DefaultOutboundAction)" }) -join ' '
}

function Enabled-Allow-Rules {
    return @(Get-NetFirewallRule -PolicyStore PersistentStore -Direction Outbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
        Where-Object { $_.Group -ne $group -and $_.Group -ne $runnerGroup } | ForEach-Object { $_.Name } | Sort-Object)
}

$Cli = (Resolve-Path $Cli).Path
$defaultsBefore = Outbound-Defaults
$rulesBefore = Enabled-Allow-Rules
Write-Host "before: $defaultsBefore; $($rulesBefore.Count) other outbound allow rules enabled"
if (-not (Direct-Works)) { Fail 'the runner cannot reach the internet before the test' }
Ok 'a direct connection works before Tor-only mode'

& $Cli tor-only on | Out-Host
if ($LASTEXITCODE -ne 0) { Fail "tor-only on failed (exit $LASTEXITCODE)" }

# Tor-only mode turned off every other program's exception, and the runner's own service is one of
# them: let it report back to GitHub again. Added after the mode, so the mode leaves it alone.
foreach ($p in @(Get-Process -Name 'Runner.Listener', 'Runner.Worker' -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Sort-Object -Unique)) {
    New-NetFirewallRule -Group $runnerGroup -DisplayName "CI runner: $(Split-Path $p -Leaf)" -Direction Outbound -Action Allow -Program $p | Out-Null
}

$status = (& $Cli tor-only status) -join "`n"
if ($status -notmatch 'Tor-only mode is ON') { Fail "status does not say ON: $status" }
Ok 'status: ON'

$defaults = Outbound-Defaults
if ($defaults -ne 'Domain=Block Private=Block Public=Block') { Fail "outbound defaults are $defaults" }
Ok "outbound defaults: $defaults"

$ours = @(Get-NetFirewallRule -Group $group | ForEach-Object { $_.DisplayName })
Write-Host ("Tor-only rules:`n  " + ($ours -join "`n  "))
if (-not ($ours -match 'tor\.exe')) { Fail 'no rule lets tor.exe out' }
if ($ours -match 'snowflake-proxy') { Fail 'the Snowflake volunteer proxy was let out' }

$left = Enabled-Allow-Rules
if ($left.Count -gt 0) { Fail "other programs' outbound exceptions are still on: $($left -join ', ')" }
Ok "all $($rulesBefore.Count) other outbound exceptions are off"

if (Direct-Works) { Fail 'a direct connection still works in Tor-only mode' }
Ok 'a direct connection is blocked'

# Tor gets out, and other programs can use it through the SOCKS port: loopback stays open.
$log = Join-Path $env:RUNNER_TEMP 'tor-only-connect.log'
$proc = Start-Process -FilePath $Cli -ArgumentList 'connect', '--smart', 'off', '--proxy-scope', 'local' -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
$connected = $false
for ($i = 0; $i -lt 150 -and -not $proc.HasExited; $i++) {
    Start-Sleep -Seconds 2
    if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'disconnect and exit') { $connected = $true; break }
}
Get-Content $log -Tail 15 | Out-Host
if (-not $connected) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Fail 'the CLI did not connect in Tor-only mode' }
Ok 'connected in Tor-only mode'

$viaTor = & curl.exe --silent --show-error --max-time 60 --socks5-hostname 127.0.0.1:9050 https://check.torproject.org/api/ip 2>&1
Write-Host "through the SOCKS port: $viaTor"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
if ("$viaTor" -notmatch '"IsTor":\s*true') { Fail 'Tor did not carry traffic in Tor-only mode' }
Ok 'traffic through Tor works in Tor-only mode'

& $Cli tor-only off | Out-Host
if ($LASTEXITCODE -ne 0) { Fail "tor-only off failed (exit $LASTEXITCODE)" }
Get-NetFirewallRule -Group $runnerGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$defaultsAfter = Outbound-Defaults
if ($defaultsAfter -ne $defaultsBefore) { Fail "outbound defaults are $defaultsAfter, were $defaultsBefore" }
Ok "outbound defaults restored: $defaultsAfter"

$rulesAfter = Enabled-Allow-Rules
$diff = Compare-Object $rulesBefore $rulesAfter
if ($diff) { Fail "other programs' rules differ from before: $($diff | Out-String)" }
Ok "all $($rulesAfter.Count) other outbound exceptions are on again"

if (@(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue).Count -gt 0) { Fail 'Tor-only rules were left behind' }
if (Test-Path (Join-Path $env:ProgramData 'OnionHop\tor-only-mode.json')) { Fail 'the Tor-only record was left behind' }
if (-not (Direct-Works)) { Fail 'a direct connection does not work after Tor-only mode' }
Ok 'a direct connection works again'

& $Cli tor-only off | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'turning it off again failed' }
if ((Outbound-Defaults) -ne $defaultsBefore) { Fail 'turning it off when off changed the firewall' }
Ok 'turning it off when it is off changes nothing'
exit 0
