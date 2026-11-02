#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDir,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$WorkDir
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-Winbrat {
    if ([Environment]::MachineName -ine 'WINBRAT') { throw 'Fixed verification identity mismatch.' }
}
Assert-Winbrat
$pilotWork = [IO.Path]::GetFullPath($WorkDir).TrimEnd('\')
$pilotSource = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
$pilotInstall = Join-Path $pilotWork 'install'
$pilotData = Join-Path $env:ProgramData 'VPNRouterBusiness'
$pilotApp = Join-Path $pilotInstall 'app\VPNRouter.App.exe'
$pilotCore = Join-Path $pilotData 'bin\sing-box.exe'
$pilotTask = 'VPNRouterBusinessPilot_' + [guid]::NewGuid().ToString('N')
$pilotStages = [ordered]@{ Identity='UNVERIFIED'; Install='UNVERIFIED'; FirstLaunch='UNVERIFIED'; SplitCycles='UNVERIFIED'; FullCycle='UNVERIFIED'; RepresentativeEgress='UNVERIFIED'; MemorySampling='UNVERIFIED'; MemoryLeak='UNVERIFIED'; UIS='UNVERIFIED'; Cleanup='UNVERIFIED' }
$pilotSamples = New-Object System.Collections.ArrayList
$pilotEgress = New-Object System.Collections.ArrayList
$pilotStage = 'Identity'
$pilotFailure = $null
$pilotOwnData = $false
$pilotOwnInstall = $false
$pilotSession = $null
$pilotTaskCreated = $false
$pilotOwnVerifier = $false
$pilotOwnProbe = $false
$pilotVerifierDir = Join-Path $pilotWork 'verifier-source'
$pilotVerifier = Join-Path $pilotVerifierDir 'tools\brat-verify.ps1'
$pilotReport = Join-Path $pilotWork 'report.json'
$pilotBaseline = $null

function Get-PilotProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        ([string]$_.ExecutablePath -ieq $pilotApp) -or ([string]$_.ExecutablePath -ieq $pilotCore)
    })
}
function Get-PilotState {
    $owned = @(Get-PilotProcesses)
    $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
    [pscustomobject]@{
        AppCount = @($owned | Where-Object { [string]$_.ExecutablePath -ieq $pilotApp }).Count
        CoreCount = @($owned | Where-Object { [string]$_.ExecutablePath -ieq $pilotCore }).Count
        TunState = $(if ($tun) { [string]$tun.Status } else { 'Absent' })
    }
}
function Wait-PilotState([bool]$Connected, [int]$Seconds = 65) {
    $until = (Get-Date).AddSeconds($Seconds)
    do {
        $state = Get-PilotState
        if ($Connected -and $state.AppCount -eq 1 -and $state.CoreCount -eq 1 -and $state.TunState -eq 'Up') { return }
        if (!$Connected -and $state.CoreCount -eq 0 -and $state.TunState -eq 'Absent') { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $until)
    throw 'Owned process/TUN lifecycle expectation failed.'
}
function Invoke-PilotUi([string]$Label, [string]$Kind='Button', [string]$Operation='Invoke') {
    Assert-Winbrat
    $checkSession = New-VerifiedBratSession
    try {
        $response = Invoke-BratInteractive -Session $checkSession -Mode uia -Name $Label -ControlType $Kind -UiaOperation $Operation -TimeoutSeconds 20
        if (!$response.Success) { throw 'UI operation failed.' }
    } finally { Remove-PSSession $checkSession }
}
function Add-PilotSample([string]$Mode, [int]$Cycle, [string]$Phase) {
    foreach ($owned in @(Get-PilotProcesses)) {
        $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$owned.ProcessId)
            $process.Refresh()
            [void]$pilotSamples.Add([pscustomobject]@{
                AtUtc=[DateTimeOffset]::UtcNow.ToString('o'); Mode=$Mode; Cycle=$Cycle; Phase=$Phase
                Kind=$(if ([string]$owned.ExecutablePath -ieq $pilotApp) { 'App' } else { 'Core' })
                PrivateBytes=[long]$process.PrivateMemorySize64; WorkingSet=[long]$process.WorkingSet64
                Handles=[int]$process.HandleCount; Threads=[int]$process.Threads.Count
            })
        } finally { if ($process) { $process.Dispose() } }
    }
}
function Get-PilotPublicIp([string]$Executable) {
    Assert-Winbrat
    $p = New-Object System.Diagnostics.Process
    try {
        $p.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $p.StartInfo.FileName = $Executable
        $p.StartInfo.Arguments = '-4 --silent --fail --max-time 15 --noproxy "*" https://api.ipify.org'
        $p.StartInfo.UseShellExecute = $false
        $p.StartInfo.CreateNoWindow = $true
        $p.StartInfo.RedirectStandardOutput = $true
        $p.StartInfo.RedirectStandardError = $true
        if (!$p.Start()) { return $null }
        if (!$p.WaitForExit(18000)) { $p.Kill(); return $null }
        $raw = $p.StandardOutput.ReadToEnd().Trim()
        if ($p.ExitCode -ne 0) { return $null }
        $address = $null
        if ([Net.IPAddress]::TryParse($raw, [ref]$address)) { return $address.ToString() }
        return $null
    } finally { $p.Dispose() }
}
$pilotStartLabel = ([char]0x25b6).ToString() + '  Start VPN||' + ([char]0x25b6).ToString() + '  Запустить VPN'
$pilotStopLabel = ([char]0x2b1b).ToString() + '  Stop VPN||' + ([char]0x2b1b).ToString() + '  Остановить VPN'
$pilotFullLabel = 'Everything except Russian resources and UIS||Всё, кроме российских ресурсов и UIS'
$pilotSplitLabel = 'Selected apps and sites||Выбранные приложения и сайты'

try {
    if (Test-Path -LiteralPath $pilotData) { throw 'Existing Business data must not be overwritten.' }
    if (Test-Path -LiteralPath $pilotInstall) { throw 'Task install path already exists.' }
    $foreign = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('VPNRouter.App.exe','VPNRouter.GUI.exe','sing-box.exe') })
    if ($foreign.Count -ne 0) { throw 'Another VPNRouter/core process is present.' }
    if (Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue) { throw 'Existing TUN must be resolved before this isolated scenario.' }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash -ine $ExpectedSha256) { throw 'Installer hash mismatch.' }
    $testUser = [string](Get-CimInstance Win32_ComputerSystem).UserName
    if ([string]::IsNullOrWhiteSpace($testUser)) { throw 'No interactive Windows tester session.' }
    Assert-Winbrat
    if (Test-Path -LiteralPath $pilotVerifierDir) { throw 'Verifier scenario path already exists.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $pilotVerifierDir 'tools') | Out-Null
    $pilotOwnVerifier = $true
    foreach ($file in @('brat-local-shim.ps1','brat-verify.ps1')) {
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath (Join-Path $pilotSource ('tools\'+$file)) -Destination (Join-Path $pilotVerifierDir ('tools\'+$file))
    }
    # This dummy credential is used only by the local shim to name the logged-on tester.
    New-Object System.Management.Automation.PSCredential($testUser, (ConvertTo-SecureString 'unused' -AsPlainText -Force)) |
        Export-Clixml (Join-Path $pilotVerifierDir '.testpc-cred-192.168.0.106.xml')
    . (Join-Path $pilotVerifierDir 'tools\brat-local-shim.ps1')
    . $pilotVerifier -Action identity 6>$null | Out-Null
    $RemoteVerifyRoot = Join-Path $pilotWork 'uia'
    $pilotSession = New-VerifiedBratSession
    $pilotStages.Identity = 'PASS'
    $curlCmd = Get-Command curl.exe -ErrorAction SilentlyContinue
    $pilotCurl = $(if ($curlCmd) { [string]$curlCmd.Source } else { $null })
    $pilotTelegram = Join-Path $pilotWork 'Telegram.exe'
    if ($pilotCurl) {
        $pilotBaseline = Get-PilotPublicIp $pilotCurl
        Assert-Winbrat
        if (Test-Path -LiteralPath $pilotTelegram) { throw 'Probe scenario path already exists.' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $pilotCurl -Destination $pilotTelegram
        $pilotOwnProbe = $true
    }
    $pilotStage = 'Install'
    Assert-Winbrat
    $pilotOwnInstall = $true
    $installer = Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="'+$pilotInstall+'"')) -PassThru -Wait
    try { if ($installer.ExitCode -ne 0) { throw 'Installer returned failure.' } } finally { $installer.Dispose() }
    if (!(Test-Path -LiteralPath $pilotApp)) { throw 'Installed Business executable missing.' }
    $pilotStages.Install = 'PASS'
    $pilotStage = 'FirstLaunch'
    Assert-Winbrat
    $pilotOwnData = $true
    $pilotLaunchAction = New-ScheduledTaskAction -Execute $pilotApp -WorkingDirectory (Split-Path $pilotApp -Parent)
    $principal = New-ScheduledTaskPrincipal -UserId $testUser -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName $pilotTask -Action $pilotLaunchAction -Principal $principal | Out-Null
    $pilotTaskCreated = $true
    Assert-Winbrat
    Start-ScheduledTask -TaskName $pilotTask
    Invoke-PilotUi $pilotStartLabel 'Button' 'Inspect'
    if (!(Test-Path -LiteralPath (Join-Path $pilotData 'config.yaml'))) { throw 'Automatic first-run configuration missing.' }
    $pilotStages.FirstLaunch = 'PASS'
    for ($cycle = 1; $cycle -le 3; $cycle++) {
        $mode = $(if ($cycle -eq 2) { 'full' } else { 'split' })
        $pilotStage = $(if ($mode -eq 'full') { 'FullCycle' } else { 'SplitCycles' })
        if ($cycle -gt 1) { Invoke-PilotUi $(if ($mode -eq 'full') { $pilotFullLabel } else { $pilotSplitLabel }) 'RadioButton' 'Select' }
        Invoke-PilotUi $pilotStartLabel
        Wait-PilotState $true
        Invoke-PilotUi $pilotStopLabel 'Button' 'Inspect'
        Add-PilotSample $mode $cycle 'connected'
        Start-Sleep -Seconds 5
        Add-PilotSample $mode $cycle 'connected-settled'
        if ($pilotBaseline -and $pilotCurl -and $cycle -le 2) {
            $unselected = Get-PilotPublicIp $pilotCurl
            $selected = Get-PilotPublicIp $pilotTelegram
            $observed = [bool]($unselected -and $selected)
            $directOk = $observed -and $(if ($mode -eq 'split') { $unselected -eq $pilotBaseline } else { $unselected -ne $pilotBaseline })
            $proxyOk = $observed -and ($selected -ne $pilotBaseline)
            [void]$pilotEgress.Add([pscustomobject]@{ Mode=$mode; Status=$(if (!$observed) {'UNVERIFIED'} elseif ($directOk -and $proxyOk) {'PASS'} else {'FAIL'}); UnselectedExpected=$directOk; SelectedProxyExpected=$proxyOk })
        }
        Invoke-PilotUi $pilotStopLabel
        Wait-PilotState $false 35
        Start-Sleep -Seconds 3
        Add-PilotSample $mode $cycle 'disconnected-settled'
        $pilotStages[$pilotStage] = 'PASS'
    }
    if ($pilotSamples.Count -gt 0) { $pilotStages.MemorySampling = 'PASS' }
    if ($pilotEgress.Count -gt 0) {
        $pilotStages.RepresentativeEgress = $(if (@($pilotEgress | Where-Object Status -eq 'FAIL').Count) {'FAIL'} elseif (@($pilotEgress | Where-Object Status -ne 'PASS').Count) {'UNVERIFIED'} else {'PASS'})
    }
} catch {
    $pilotStages[$pilotStage] = 'FAIL'
    $pilotFailure = [pscustomobject]@{ Stage=$pilotStage; Category=$_.Exception.GetType().Name }
} finally {
    try {
        Assert-Winbrat
        if ($pilotTaskCreated) {
            try { if ((Get-PilotState).CoreCount -gt 0) { Invoke-PilotUi $pilotStopLabel; Wait-PilotState $false 25 } } catch { }
        }
        $cleanupProcesses = @(if ($pilotOwnInstall) { Get-PilotProcesses })
        foreach ($owned in $cleanupProcesses) {
            Assert-Winbrat
            $p = $null
            try {
                $p = [Diagnostics.Process]::GetProcessById([int]$owned.ProcessId)
                if ($p.MainModule.FileName -ieq [string]$owned.ExecutablePath) { $p.Kill(); [void]$p.WaitForExit(5000) }
            } catch { } finally { if ($p) { $p.Dispose() } }
        }
        if ($pilotTaskCreated) {
            Assert-Winbrat
            Unregister-ScheduledTask -TaskName $pilotTask -Confirm:$false
        }
        $uninstaller = Join-Path $pilotInstall 'unins000.exe'
        if ($pilotOwnInstall -and (Test-Path -LiteralPath $uninstaller)) {
            Assert-Winbrat
            $u = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait
            try { if ($u.ExitCode -ne 0) { throw 'Scenario uninstaller failed.' } } finally { $u.Dispose() }
        }
        if ($pilotOwnData -and (Test-Path -LiteralPath $pilotData)) {
            Assert-Winbrat
            Remove-Item -LiteralPath $pilotData -Recurse -Force
        }
        if ($pilotOwnInstall -and (Test-Path -LiteralPath $pilotInstall)) {
            Assert-Winbrat
            Remove-Item -LiteralPath $pilotInstall -Recurse -Force
        }
        if ($pilotOwnVerifier -and (Test-Path -LiteralPath $pilotVerifierDir)) {
            Assert-Winbrat
            Remove-Item -LiteralPath $pilotVerifierDir -Recurse -Force
        }
        if ($pilotOwnProbe -and (Test-Path -LiteralPath (Join-Path $pilotWork 'Telegram.exe'))) {
            Assert-Winbrat
            Remove-Item -LiteralPath (Join-Path $pilotWork 'Telegram.exe') -Force
        }
        if (@(Get-PilotProcesses).Count -ne 0 -or (Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue)) { throw 'Scenario network/process cleanup incomplete.' }
        $pilotStages.Cleanup = 'PASS'
    } catch { $pilotStages.Cleanup = 'FAIL' }
    if ($pilotSession) { Remove-PSSession $pilotSession }
    $summary = [ordered]@{ Stages=$pilotStages; Failure=$pilotFailure; Samples=@($pilotSamples); RepresentativeEgress=@($pilotEgress); Limits=@('Three cycles are not proof of absence of memory leaks.','Representative egress tests do not prove every routing rule or DNS path.','UIS requires real customer calls.'); AtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    if (!(Test-Path -LiteralPath $pilotWork)) { Assert-Winbrat; New-Item -ItemType Directory -Path $pilotWork | Out-Null }
    $json = $summary | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($pilotReport, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output $json
}
if (@($pilotStages.Values | Where-Object { $_ -eq 'FAIL' }).Count -gt 0) { exit 1 }
exit 0
