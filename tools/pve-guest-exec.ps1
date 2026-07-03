<#
.SYNOPSIS
  Run a shell command INSIDE a Proxmox test VM via qemu-guest-agent (no SSH).

.DESCRIPTION
  Companion to testvm-control.ps1 (which does power only). Uses the same scoped
  DPAPI-encrypted API token (.pve-api-token.xml) to POST /agent/exec and poll
  /agent/exec-status. SSH-key-independent, so it survives VM reprovisions (host-key
  changes / wiped authorized_keys) — the durable way to drive the Linux test VM
  (vmid 101, debian-xfce). Requires the token to have PVEVMAdmin on the target
  /vms/<id> and qemu-guest-agent installed+running in the VM (it is, on 101).

.EXAMPLE
  tools/pve-guest-exec.ps1 -VmId 101 -Cmd '/opt/vpnrouter/sing-box version | head -1'

.NOTES
  GUI apps: sudo -u tester env DISPLAY=:0 LIBGL_ALWAYS_SOFTWARE=1 nohup <app> &  (see [[linux-test-vm]]).
  Big file in: serve from the Mac (python3 -m http.server) and curl from the guest.
#>
param(
  [int]$VmId = 101,
  [string]$Node = 'pve-ninitux',
  [string]$PveHost = '192.168.0.169',
  [string]$Cmd = 'id',
  [int]$TimeoutSec = 90,
  [string]$TokenFile = "$PSScriptRoot\..\.pve-api-token.xml"
)
$ErrorActionPreference = 'Stop'
if (-not ('TrustAllCertsPolicy' -as [type])) {
  Add-Type @"
using System.Net; using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy { public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int problem){return true;} }
"@
}
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

if (-not (Test-Path $TokenFile)) { throw "No Proxmox API token at $TokenFile (see testvm-control.ps1 header)." }
$sec = Import-Clixml $TokenFile
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try { $tok = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if ($tok -notmatch '!') { $tok = "root@pam!claude-testvm=$tok" }
$headers = @{ Authorization = "PVEAPIToken=$tok" }
$base = "https://${PveHost}:8006/api2/json/nodes/$Node/qemu/$VmId/agent"

$parts = @('/bin/bash', '-c', $Cmd)
$body = ($parts | ForEach-Object { "command=" + [uri]::EscapeDataString($_) }) -join '&'
$start = Invoke-RestMethod -Method POST -Uri "$base/exec" -Headers $headers -Body $body -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 25
$gpid = $start.data.pid
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 700
  $st = Invoke-RestMethod -Method GET -Uri "$base/exec-status?pid=$gpid" -Headers $headers -TimeoutSec 20
  if ($st.data.exited -eq 1) {
    if ($st.data.'out-data') { Write-Output $st.data.'out-data'.TrimEnd() }
    if ($st.data.'err-data') { Write-Output ("[stderr] " + $st.data.'err-data'.TrimEnd()) }
    Write-Output ("[exit] " + $st.data.exitcode)
    exit [int]$st.data.exitcode
  }
}
Write-Output "[timeout after ${TimeoutSec}s waiting for guest exec]"; exit 99
