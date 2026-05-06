# Task A v2 — coexistence test helpers
# Source via: . .\helpers.ps1

$global:EvidenceDir = "C:\Project\VPNRouter\.claude\worktrees\funny-liskov-3f39cb\plans\coexistence-evidence"

function Test-Internet {
    param([int]$TimeoutSec = 5)
    try {
        $ip = (Invoke-RestMethod 'https://api.ipify.org?format=json' -TimeoutSec $TimeoutSec).ip
        if ($ip) { return @{ ok = $true; ip = $ip } }
    } catch {
        return @{ ok = $false; error = $_.Exception.Message }
    }
    return @{ ok = $false; error = "no ip returned" }
}

function Invoke-Rescue {
    param([string]$Tag = "rescue")
    Write-Output "[$Tag] running rescue — stopping any WG/AWG kill-switch services"

    $stopped = @()
    Get-Service | Where-Object { $_.Name -match '^(WireGuardTunnel|AmneziaWGTunnel)\$' } | ForEach-Object {
        try {
            if ($_.Status -eq 'Running' -or $_.Status -eq 'StartPending') {
                Stop-Service $_.Name -Force -ErrorAction Stop
                $stopped += $_.Name
            }
            Set-Service $_.Name -StartupType Manual -ErrorAction SilentlyContinue
        } catch {
            Write-Output "[$Tag] failed to stop $($_.Name): $($_.Exception.Message)"
        }
    }
    Write-Output "[$Tag] stopped: $($stopped -join ', ')"

    Start-Sleep -Seconds 2
    $ic = Test-Internet -TimeoutSec 8
    if ($ic.ok) {
        Write-Output "[$Tag] post-rescue ipify: $($ic.ip)  OK"
    } else {
        Write-Output "[$Tag] post-rescue: FAILED — $($ic.error)"
    }
    return $ic
}

function Stop-VPNRouter {
    param([string]$Tag = "stop-vpn")
    # Kill any VPNRouter.CLI launched processes + sing-box children
    Get-Process | Where-Object { $_.Name -in 'sing-box','VPNRouter.CLI','VPNRouter.App','VPNRouter.GUI','VPNRouter.Service' } | ForEach-Object {
        try {
            Write-Output "[$Tag] killing $($_.Name) pid=$($_.Id)"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        } catch {}
    }
    # Cleanup VPNRouter-TUN adapter if leftover
    Start-Sleep -Seconds 1
    $tun = Get-NetAdapter | Where-Object { $_.Name -eq 'VPNRouter-TUN' -or $_.InterfaceDescription -match 'sing-box-tun' }
    foreach ($a in $tun) {
        try {
            Write-Output "[$Tag] disabling leftover TUN adapter: $($a.Name)"
            Disable-NetAdapter $a.Name -Confirm:$false -ErrorAction SilentlyContinue
        } catch {}
    }
}

function Save-Snapshot {
    param([string]$Label)
    $f = Join-Path $global:EvidenceDir "snap-$Label.json"
    $ic = Test-Internet -TimeoutSec 5
    $obj = @{
        label = $Label
        timestamp = (Get-Date).ToString("o")
        ipify = $ic
        adapters = (Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -or $_.InterfaceDescription -match 'WireGuard|Amnezia|wintun|sing-box' } | Select Name,Status,InterfaceDescription,ifIndex,MacAddress)
        wg_services = (Get-Service | Where-Object { $_.Name -match '^(WireGuardTunnel|AmneziaWGTunnel)\$' } | Select Name,Status,StartType)
        vpnrouter_procs = (Get-Process | Where-Object { $_.Name -in 'sing-box','VPNRouter.App','VPNRouter.CLI','VPNRouter.Service' } | Select Name,Id,StartTime)
        default_routes = (Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Select InterfaceAlias,NextHop,RouteMetric,ifMetric)
        wg_adapter_subnets = ((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { (Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue).InterfaceDescription -match 'WireGuard|Amnezia|wintun' }) | Select InterfaceAlias,IPAddress,PrefixLength)
    }
    $obj | ConvertTo-Json -Depth 6 | Out-File $f -Encoding utf8
    Write-Output "[SNAP] $Label -> $f"
    Write-Output "[SNAP] ipify=$(if ($ic.ok) {$ic.ip} else {"FAIL:$($ic.error)"})"
    return $obj
}

function Wait-PortBound {
    param([int]$Port, [int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $bound = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($bound) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Output "[helpers.ps1] loaded — Test-Internet, Invoke-Rescue, Stop-VPNRouter, Save-Snapshot, Wait-PortBound"
