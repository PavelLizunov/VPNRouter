<#
.SYNOPSIS
  Autonomous Proxmox control of the windows-brat test VM (vmid 100) for live
  verification, WITHOUT handling the Proxmox root password in plaintext.

.DESCRIPTION
  Claude cannot enter the Proxmox root password (safety rule: no handling
  passwords in plaintext). Instead this uses a SCOPED Proxmox API TOKEN: the
  same pattern as GH_TOKEN for GitHub, a stored, revocable, least-privilege
  automation credential used programmatically, never a password typed into a
  field. Once the token exists, every start/stop/await is autonomous.

  ONE-TIME SETUP (you, ~2 min). Unavoidable bootstrap: Claude cannot create the
  token itself because creating it requires authenticating with the root
  password it is forbidden to handle.

    1. Proxmox UI -> Datacenter -> Permissions -> API Tokens -> Add
         User                 = root@pam
         Token ID             = claude-testvm
         Privilege Separation = CHECKED  (so the token is scoped, not full root)
       Copy the secret (shown ONCE).

    2. Proxmox UI -> Datacenter -> Permissions -> Add -> API Token Permission
         Path  = /vms/100            (scopes the token to ONLY the test VM)
         Token = root@pam!claude-testvm
         Role  = PVEVMAdmin          (power + config on VM 100 only; cannot
                                       delete the VM or touch the cluster)

    3. Store it encrypted (run in the repo root; you paste, Claude never sees it):
         powershell -ExecutionPolicy Bypass -File tools/testvm-control.ps1 -Action store-token
       Paste exactly:  root@pam!claude-testvm=<secret-uuid>
       It is DPAPI-encrypted to .pve-api-token.xml (gitignored, decryptable only
       by your Windows user) and the command immediately verifies it can read
       VM 100's status.

  THEN AUTONOMOUS (Claude, every live test):
       tools/testvm-control.ps1 -Action status        # power state
       tools/testvm-control.ps1 -Action ensure-ready  # start if off + wait WinRM
       tools/testvm-control.ps1 -Action stop           # graceful shutdown

  To revoke at any time: delete the token in the Proxmox UI (the file becomes
  inert) or delete .pve-api-token.xml.
#>
[CmdletBinding()]
param(
    [ValidateSet('store-token', 'status', 'start', 'stop', 'ensure-ready')]
    [string]$Action = 'status',
    [string]$PveHost = '192.168.0.169',
    [string]$Node = 'pve-ninitux',
    [int]$VmId = 100,
    [string]$VmIp = '192.168.0.106',
    [int]$WinRmTimeoutSec = 240
)

$ErrorActionPreference = 'Stop'
$TokenFile = Join-Path $PSScriptRoot '..\.pve-api-token.xml'

# Proxmox ships a self-signed cert; Windows PowerShell 5.1 has no
# -SkipCertificateCheck, so install a trust-all policy (scoped to this process).
if (-not ('TrustAllCertsPolicy' -as [type])) {
    Add-Type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy {
    public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int problem) { return true; }
}
"@
}
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

function Get-PveToken {
    if (-not (Test-Path $TokenFile)) {
        throw "No Proxmox API token at $TokenFile. One-time setup is in this script's header; then run -Action store-token."
    }
    $sec = Import-Clixml $TokenFile
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$TokenId = 'root@pam!claude-testvm'

function Invoke-Pve {
    param([string]$Method, [string]$Path)
    # Accept either the full 'user@realm!tokenid=secret' form OR a bare secret
    # UUID (the common store-token paste mistake) — prepend the known token id.
    $tok = Get-PveToken
    if ($tok -notmatch '!') { $tok = "${TokenId}=$tok" }
    $headers = @{ Authorization = "PVEAPIToken=$tok" }
    Invoke-RestMethod -Method $Method -Uri "https://${PveHost}:8006/api2/json$Path" -Headers $headers -TimeoutSec 20
}

function Get-VmStatus { (Invoke-Pve -Method GET -Path "/nodes/$Node/qemu/$VmId/status/current").data.status }

switch ($Action) {
    'store-token' {
        Write-Host "Paste the Proxmox API token in the form  root@pam!claude-testvm=<secret-uuid>"
        $sec = Read-Host -AsSecureString "Token"
        $sec | Export-Clixml $TokenFile
        Write-Host "Stored (DPAPI-encrypted, this Windows-user only) at $TokenFile (gitignored)."
        Write-Host "Verifying the token can read VM ${VmId}..."
        Write-Host ("VM {0} status: {1}" -f $VmId, (Get-VmStatus))
        Write-Host "OK. Claude can now power-manage VM $VmId autonomously."
    }
    'status' { Write-Host ("VM {0} status: {1}" -f $VmId, (Get-VmStatus)) }
    'start' {
        if ((Get-VmStatus) -eq 'running') { Write-Host "VM $VmId already running"; return }
        Invoke-Pve -Method POST -Path "/nodes/$Node/qemu/$VmId/status/start" | Out-Null
        Write-Host "VM $VmId start issued"
    }
    'stop' {
        Invoke-Pve -Method POST -Path "/nodes/$Node/qemu/$VmId/status/shutdown" | Out-Null
        Write-Host "VM $VmId graceful shutdown issued"
    }
    'ensure-ready' {
        if ((Get-VmStatus) -ne 'running') {
            Invoke-Pve -Method POST -Path "/nodes/$Node/qemu/$VmId/status/start" | Out-Null
            Write-Host "VM $VmId starting..."
        }
        else { Write-Host "VM $VmId already running" }
        $deadline = (Get-Date).AddSeconds($WinRmTimeoutSec)
        while ((Get-Date) -lt $deadline) {
            if (Test-NetConnection -ComputerName $VmIp -Port 5985 -WarningAction SilentlyContinue -InformationLevel Quiet) {
                Write-Host "WinRM reachable at ${VmIp}:5985. VM ready."
                exit 0
            }
            Start-Sleep -Seconds 5
        }
        Write-Error "WinRM not reachable within ${WinRmTimeoutSec}s. VM may still be booting."
        exit 1
    }
}
