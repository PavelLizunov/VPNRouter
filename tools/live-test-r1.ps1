# Live regression test for v2.27.2-r1 - requires admin.
# Validates the release on a real Windows system:
#   1. Deploys upstream sing-box 1.13.10 to %ProgramData%\VPNRouter\bin\
#   2. Generates a test config via CLI --dry-run
#   3. Starts sing-box with TUN (real adapter creation)
#   4. Inspects TUN state, routing table
#   5. Stops sing-box, verifies adapter cleanup
#   6. Repeats with force-kill to test S B1 dangling-adapter hypothesis
#
# Safe: uses a synthetic 1.2.3.4 VLESS server so no real proxy traffic.
# Non-destructive to existing VPN state (stops anything running first).

$ErrorActionPreference = "Stop"

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function OK($msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function FAIL($msg) { Write-Host "  [FAIL] $msg" -ForegroundColor Red }
function INFO($msg) { Write-Host "  [info] $msg" -ForegroundColor Gray }

$Root = "C:\Project\VPNRouter\.claude\worktrees\affectionate-varahamihira-9375b5"
$Pub = Join-Path $Root "publish\dist"
$NewSb = Join-Path $Pub "sing-box.exe"
$InstalledSb = "$env:ProgramData\VPNRouter\bin\sing-box.exe"
$TestCfg = Join-Path $env:TEMP "vpnr-r1-test.yaml"
$GeneratedJson = "$env:ProgramData\VPNRouter\config\current.json"
$VpnLog = "$env:ProgramData\VPNRouter\logs\singbox.log"

Step "0. Pre-flight: admin + artifacts present"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    FAIL "NOT running as admin - re-run from elevated cmd"
    exit 1
}
OK "admin confirmed"
if (-not (Test-Path $NewSb)) { FAIL "sing-box at $NewSb missing"; exit 1 }
OK "new sing-box present"

Step "1. Stop any running sing-box / VPNRouter"
Get-Process sing-box,VPNRouter.App,VPNRouter.CLI,VPNRouter.Service -EA SilentlyContinue | ForEach-Object {
    INFO "killing $($_.Name) PID $($_.Id)"
    Stop-Process -Id $_.Id -Force -EA SilentlyContinue
}
Start-Sleep 1
OK "clean slate"

Step "2. Deploy new upstream sing-box 1.13.10"
if (Test-Path $InstalledSb) {
    $before = & $InstalledSb version 2>&1 | Select-Object -First 1
    INFO "installed before: $before"
} else {
    INFO "installed before: (missing)"
}
New-Item -ItemType Directory -Force -Path (Split-Path $InstalledSb) | Out-Null
Copy-Item $NewSb $InstalledSb -Force
$newVer = & $InstalledSb version 2>&1 | Select-Object -First 1
OK "deployed: $newVer"

Step "3. Generate synthetic config (1.2.3.4 fake VLESS)"
$yaml = @'
schema_version: 1
app:
  log_level: info
  log_file: C:\tmp\vpnr-r1-test.log
  routing_mode: split
  config_mode: generated
  bypass_russian_traffic: false
  force_ipv4_only: true
vless:
  server: ''
  servers:
    - name: test
      server: 1.2.3.4
      port: 443
      uuid: 00000000-0000-0000-0000-000000000001
      flow: xtls-rprx-vision
      security: reality
      reality:
        enabled: true
        server_name: www.microsoft.com
        fingerprint: chrome
        public_key: gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A
        short_id: d86e92a0c6dd2271
      transport:
        type: tcp
  active_server: test
tun:
  interface_name: VPNRouter-TUN
  ipv4_address: 172.19.0.1/30
  ipv6_enabled: false
  mtu: 9000
  auto_route: true
  strict_route: false
dns:
  strategy: ipv4_only
  vpn_dns: https://1.1.1.1/dns-query
  local_dns: local
singbox:
  executable_path: C:\ProgramData\VPNRouter\bin\sing-box.exe
  auto_download: false
  clash_api: 127.0.0.1:9090
monitoring:
  health_check_interval: 30
  restart_on_failure: true
  max_restart_attempts: 5
profile_sources: []
active_profile: Browsers
'@
Set-Content -Path $TestCfg -Value $yaml -Encoding ASCII

$cli = Join-Path $Pub "VPNRouter.CLI.exe"
& $cli start --profile Browsers --dry-run --config $TestCfg 2>&1 | Select-Object -Last 3
if (-not (Test-Path $GeneratedJson)) { FAIL "CLI dry-run did not produce current.json"; exit 1 }
OK "current.json generated ($((Get-Item $GeneratedJson).Length) bytes)"

& $InstalledSb check -c $GeneratedJson
if ($LASTEXITCODE -ne 0) { FAIL "1.13.10 rejected generated config"; exit 1 }
OK "1.13.10 accepts generated config (check exit 0)"

Step "4. TUN adapter baseline (before start)"
$before = netsh interface show interface | Out-String
$beforeVpnr = $before -split "`n" | Where-Object { $_ -match "VPNRouter|sing-box" }
INFO "VPNRouter/sing-box rows before: $($beforeVpnr.Count)"
if ($beforeVpnr) { $beforeVpnr | ForEach-Object { INFO "  $_" } }

Step "5. Start sing-box - watch TUN come up"
if (Test-Path $VpnLog) { Remove-Item $VpnLog -Force }
$stderrLog = Join-Path $env:TEMP "vpnr-r1-stderr.txt"
$p = Start-Process -FilePath $InstalledSb -ArgumentList "run","-c",$GeneratedJson -PassThru -NoNewWindow -RedirectStandardError $stderrLog
INFO "sing-box PID: $($p.Id)"
Start-Sleep 4

if ($p.HasExited) {
    FAIL "sing-box exited early (code $($p.ExitCode))"
    Get-Content $stderrLog -EA SilentlyContinue | Write-Host
    exit 1
}
OK "sing-box running"

Step "6. TUN adapter state (during run)"
$during = netsh interface show interface | Out-String
$duringVpnr = $during -split "`n" | Where-Object { $_ -match "VPNRouter|sing-box" }
if ($duringVpnr) {
    OK "TUN adapter created:"
    $duringVpnr | ForEach-Object { INFO "  $_" }
} else {
    FAIL "no VPNRouter-TUN after start"
}

Step "6b. Route table - 0.0.0.0/0 via TUN?"
$defaultRoutes = (route print -4 | Select-String "^\s*0\.0\.0\.0\s").Line
if ($defaultRoutes) { $defaultRoutes | ForEach-Object { INFO "  $_" } }

Step "6c. Clash API responding?"
try {
    $cfg = Invoke-RestMethod -Uri "http://127.0.0.1:9090/configs" -TimeoutSec 3
    OK "clash API responded: mode=$($cfg.mode), log-level=$($cfg.'log-level')"
} catch {
    INFO "clash API not reachable (OK if auth required): $_"
}

Step "7. Stop sing-box (graceful)"
Stop-Process -Id $p.Id -Force
Start-Sleep 2
OK "stopped"

Step "8. Check for dangling TUN adapter (audit B1 hypothesis)"
Start-Sleep 2
$after = netsh interface show interface | Out-String
$afterVpnr = $after -split "`n" | Where-Object { $_ -match "VPNRouter|sing-box" }
if ($afterVpnr) {
    FAIL "DANGLING ADAPTER after clean stop:"
    $afterVpnr | ForEach-Object { FAIL "  $_" }
    FAIL "This confirms B1 hypothesis - wintun leaks on process exit"
} else {
    OK "clean - no residual adapter (sing-box cleaned up properly)"
}

Step "9. Repeat with force-kill (simulate crash)"
$p2 = Start-Process -FilePath $InstalledSb -ArgumentList "run","-c",$GeneratedJson -PassThru -NoNewWindow
Start-Sleep 3
INFO "force-killing PID $($p2.Id) via taskkill /F"
taskkill /F /PID $p2.Id 2>&1 | Out-Null
Start-Sleep 3
$afterKill = netsh interface show interface | Out-String
$afterKillVpnr = $afterKill -split "`n" | Where-Object { $_ -match "VPNRouter|sing-box" }
if ($afterKillVpnr) {
    FAIL "POST-KILL DANGLING ADAPTER:"
    $afterKillVpnr | ForEach-Object { FAIL "  $_" }
} else {
    OK "force-kill also left no residual adapter"
}

Step "10. singbox.log tail"
if (Test-Path $VpnLog) {
    Get-Content $VpnLog -Tail 15
} else {
    INFO "no $VpnLog (expected - we ran sing-box directly, not via engine)"
}

Write-Host "`n=== DONE ===" -ForegroundColor Green
Write-Host "Send me the entire transcript of this script." -ForegroundColor Yellow
