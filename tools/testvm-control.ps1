[CmdletBinding()]
param(
    [ValidateSet('store-token', 'status', 'start', 'stop', 'ensure-ready')]
    [string]$Action = 'status',
    [string]$PveHost = '192.168.0.169',
    [string]$Node = 'pve-ninitux',
    [int]$VmId = 100,
    [string]$VmIp = '100.115.182.0',
    [int]$WinRmTimeoutSec = 240
)

$ErrorActionPreference = 'Stop'

function Resolve-CredentialFile {
    param(
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string]$LocalRoot
    )
    $local = Join-Path $LocalRoot $FileName
    if (Test-Path $local) { return $local }
    $commonDir = $null
    try { $commonDir = git -C $LocalRoot rev-parse --git-common-dir 2>$null } catch { }
    if ($commonDir) {
        $commonDir = @($commonDir)[0].ToString().Trim()
        if ($commonDir -and $commonDir -ne '.') {
            if (-not [System.IO.Path]::IsPathRooted($commonDir)) {
                $commonDir = Join-Path $LocalRoot $commonDir
            }
            $primaryRoot = Split-Path $commonDir -Parent
            if ($primaryRoot) {
                $primary = Join-Path $primaryRoot $FileName
                if (Test-Path $primary) { return $primary }
            }
        }
    }
    return $local
}

$TokenFile = Resolve-CredentialFile -FileName '.pve-api-token.xml' -LocalRoot (Split-Path $PSScriptRoot -Parent)

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
        if (Test-NetConnection -ComputerName $VmIp -Port 5985 -WarningAction SilentlyContinue -InformationLevel Quiet) {
            Write-Host "WinRM reachable at ${VmIp}:5985. VM ready."
            exit 0
        }
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
