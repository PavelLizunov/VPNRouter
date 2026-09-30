#requires -Version 5.1
<#
.SYNOPSIS
  Stops VPNRouter before an install, upgrade or uninstall, touching only what runs from the given folders.

.DESCRIPTION
  Used by the Inno Setup installer (packaging/windows/vpnrouter.iss). It never stops anything by name alone, so a second
  copy of VPNRouter installed elsewhere (or a scratch install used for testing) is left alone:
    * the Windows service "VPNRouter", only if its binary lives inside -InstallDir (and deleted with -RemoveService);
    * every process whose executable lives inside -InstallDir or -DataDir (app, CLI, GUI stub, sing-box copy, winws from
      Zapret), except the installer/uninstaller itself.
  Prints what it did. Exit code 10 means "the service was running and this script stopped it" (the installer starts it
  again after an upgrade); every other outcome exits 0.
#>
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$DataDir = '',
    [switch]$RemoveService
)

$ErrorActionPreference = 'SilentlyContinue'

$roots = @(($InstallDir.TrimEnd('\') + '\'))
if ($DataDir) { $roots += ($DataDir.TrimEnd('\') + '\') }

function Test-InRoots([string]$path) {
    if (-not $path) { return $false }
    foreach ($r in $roots) {
        if ($path.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-ServiceBinary([string]$pathName) {
    if (-not $pathName) { return '' }
    if ($pathName.StartsWith('"')) {
        $end = $pathName.IndexOf('"', 1)
        if ($end -gt 1) { return $pathName.Substring(1, $end - 1) }
    }
    return ($pathName -split ' ')[0]
}

$serviceStopped = $false
$svc = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'"
if ($svc -and (Test-InRoots (Get-ServiceBinary $svc.PathName))) {
    if ($svc.State -ne 'Stopped') {
        Write-Output 'Stopping the VPNRouter service'
        Stop-Service -Name VPNRouter -Force
        for ($i = 0; $i -lt 20; $i++) {
            $state = (Get-CimInstance Win32_Service -Filter "Name='VPNRouter'").State
            if ($state -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }
        $serviceStopped = $true
    }
}

$self = $PID
$targets = Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $self -and
    $_.Name -notmatch '^(unins\d*|VPNRouter-Setup.*)\.(exe|tmp)$' -and
    (Test-InRoots $_.ExecutablePath)
}

foreach ($p in $targets) {
    Write-Output ("Stopping {0} (PID {1})" -f $p.Name, $p.ProcessId)
    Stop-Process -Id $p.ProcessId -Force
}

if ($targets) { Start-Sleep -Milliseconds 800 }

if ($RemoveService -and $svc -and (Test-InRoots (Get-ServiceBinary $svc.PathName))) {
    Write-Output 'Deleting the VPNRouter service'
    & (Join-Path $env:SystemRoot 'System32\sc.exe') delete VPNRouter | Out-Null
}

if ($serviceStopped) { exit 10 }
exit 0
