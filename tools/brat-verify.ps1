# Safety: fixed target only. Never accepts a caller-supplied host/IP/hostname
# and never falls back to the local machine; every action first verifies over
# WinRM that 100.115.182.0 really is WINBRAT, and fails closed on any mismatch.
#
# Interactive UI work (uia) never touches local process/input/screen
# APIs from this dev box: a helper script is shipped to the verified brat box
# and run there in the interactive console session via a unique transient
# scheduled task. Moving the logged-on session onto the physical console
# (tscon) also runs through a unique transient SYSTEM scheduled task, so no
# password is ever needed. All remote transient files live under
# C:\r4review\verify and are removed again in a finally block.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('identity', 'deploy', 'uia', 'state', 'probe', 'lifecycle', 'logs', 'emergencycleanup', 'tuninventory', 'updateprobe', 'liveupdate')]
    [string]$Action,

    [string]$Version,

    # UIA element selectors (uia): AutomationId and/or Name required.
    [string]$AutomationId,
    [string]$Name,
    [string]$ControlType,

    [ValidateSet('Inspect', 'Invoke', 'InvokeThen', 'CheckUpdate', 'Toggle', 'Expand', 'Select', 'SetValue')]
    [string]$UiaOperation = 'Inspect',
    [string]$Value,

    # logs: only inspect entries written during the recent verification window.
    [ValidateRange(1, 1440)]
    [int]$LogWindowMinutes = 120,
    [string]$LogPattern,

    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30,

    [ValidateSet('Control', 'Boundary')]
    [string]$ProbeProfile = 'Control',

    # lifecycle: caller-provided timestamp only; raw lines never leave WINBRAT.
    [string]$SinceUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve a credential file: prefer the current checkout root (local-first),
# then fall back to the primary worktree root via Git's common directory.
# Never copies credentials into task worktrees; fails closed if neither exists.
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
    # Neither location has the file. Return the local path so the caller's
    # own Test-Path guard (New-VerifiedBratSession) produces the actionable
    # missing-file error; this also keeps store/bootstrap paths reachable.
    return $local
}

$Root            = Split-Path $PSScriptRoot -Parent
# Fixed transport endpoint: the windows-brat VM's Tailscale CGNAT address.
# Caller-nonconfigurable on purpose (see header); the LAN 192.168.0.106 is retired.
$BratIp          = '100.115.182.0'
$BratMachineName = 'WINBRAT'
# Credential file keeps its legacy LAN-era name on purpose: the DPAPI-encrypted
# secret already exists under this filename, so we resolve it as-is and avoid any
# secret copy/migration. Only the transport IP above changed, not the credential.
$CredFile        = Resolve-CredentialFile -FileName '.testpc-cred-192.168.0.106.xml' -LocalRoot $Root
$RemoteVerifyRoot = 'C:\r4review\verify'

function New-VerifiedBratSession {
    if (-not (Test-Path $CredFile)) {
        throw "Credential file missing: $CredFile. Create it once (interactive): Get-Credential -Message 'Local admin on $BratIp ($BratMachineName)' | Export-Clixml '$CredFile'"
    }
    $cred = Import-Clixml $CredFile
    $s = $null
    try {
        $s = New-PSSession -ComputerName $BratIp -Credential $cred
        $machine = Invoke-Command -Session $s { [Environment]::MachineName }
        if ($machine -ine $BratMachineName) {
            throw "Identity check failed: $BratIp reported MachineName '$machine', expected '$BratMachineName'. Refusing to proceed."
        }
        return $s
    }
    catch {
        if ($s) { Remove-PSSession $s -ErrorAction SilentlyContinue }
        throw
    }
}

function Invoke-BratInteractive {
    param(
        [Parameter(Mandatory = $true)] $Session,
        [Parameter(Mandatory = $true)] [ValidateSet('uia')] [string]$Mode,
        [string]$AutomationId,
        [string]$Name,
        [string]$ControlType,
        [ValidateSet('Inspect', 'Invoke', 'InvokeThen', 'CheckUpdate', 'Toggle', 'Expand', 'Select', 'SetValue')]
        [string]$UiaOperation = 'Inspect',
        [string]$Value,
        [int]$TimeoutSeconds = 30
    )

    # The only place UIA code exists. Shipped to brat and run
    # there; never dot-sourced or invoked on this dev box.
    $helper = @'
# BEGIN REMOTE IN-SESSION HELPER
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$RequestPath,
    [Parameter(Mandatory = $true, Position = 1)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ Success = $false; Error = $null }
try {
    $reqText = Get-Content -Path $RequestPath -Raw
    Remove-Item -LiteralPath $RequestPath -Force -ErrorAction SilentlyContinue
    $req = $reqText | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds([int]$req.TimeoutSeconds)

    if ($req.Mode -eq 'uia') {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $ae = [System.Windows.Automation.AutomationElement]

        # VPNRouter.GUI is the bootstrap/update host and can remain alive without
        # owning Avalonia controls. Target the real app first; retain the host as
        # a legacy fallback for older package layouts.
        $proc = Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { $proc = Get-Process -Name VPNRouter.GUI -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $proc) { throw "VPNRouter.GUI / VPNRouter.App process not found." }

        $root = $ae::RootElement
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $proc.Id)
        $window = $null
        while (-not $window -and (Get-Date) -lt $deadline) {
            $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
            if (-not $window) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $window) { throw "No top-level window for PID $($proc.Id)." }

        $conds = @()
        if ($req.AutomationId) { $conds += New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, [string]$req.AutomationId) }
        if ($req.Name) {
            $requestedNames = @(([string]$req.Name).Split(
                [string[]]@('||'),
                [System.StringSplitOptions]::RemoveEmptyEntries))
            if ($requestedNames.Count -eq 1) {
                $conds += New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $requestedNames[0])
            }
            else {
                $nameConditions = @($requestedNames | ForEach-Object {
                    New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $_)
                })
                $conds += [System.Windows.Automation.OrCondition]::new(
                    [System.Windows.Automation.Condition[]]$nameConditions)
            }
        }
        if ($req.ControlType) {
            $ctProp = [System.Windows.Automation.ControlType].GetField([string]$req.ControlType, [System.Reflection.BindingFlags]'Public,Static')
            if (-not $ctProp) { throw "Unknown ControlType '$($req.ControlType)'." }
            $conds += New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ctProp.GetValue($null))
        }
        $findCond = $conds[0]
        for ($i = 1; $i -lt $conds.Count; $i++) {
            $findCond = New-Object System.Windows.Automation.AndCondition $findCond, $conds[$i]
        }
        $processFindCond = New-Object System.Windows.Automation.AndCondition $pidCond, $findCond

        $target = $null
        while (-not $target -and (Get-Date) -lt $deadline) {
            # Search every top-level window owned by the app process. Avalonia
            # flyouts and modal dialogs are siblings of the main window in UIA.
            $matches = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $processFindCond)
            for ($i = 0; $i -lt $matches.Count; $i++) {
                $candidate = $matches.Item($i)
                if ($candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen) {
                    $target = $candidate
                    break
                }
            }
            if (-not $target) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $target) { throw "No descendant matched AutomationId='$($req.AutomationId)' Name='$($req.Name)' ControlType='$($req.ControlType)' before timeout." }

        switch ([string]$req.Operation) {
            'Inspect' {
                $result.Element = [ordered]@{
                    Name         = $target.Current.Name
                    AutomationId = $target.Current.AutomationId
                    ControlType  = $target.Current.ControlType.ProgrammaticName
                    IsEnabled    = $target.Current.IsEnabled
                }
                $toggle = $null
                if ($target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
                    $result.Element.ToggleState = $toggle.Current.ToggleState.ToString()
                }
            }
            'Invoke' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by matched element." }
                $pat.Invoke()
            }
            'InvokeThen' {
                if (-not $req.Value) { throw "InvokeThen requires Value with the follow-up element Name." }
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by matched element." }
                $pat.Invoke()

                $nextName = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, [string]$req.Value)
                $nextCond = New-Object System.Windows.Automation.AndCondition $pidCond, $nextName
                $next = $null
                while (-not $next -and (Get-Date) -lt $deadline) {
                    $next = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nextCond)
                    if (-not $next) { Start-Sleep -Milliseconds 200 }
                }
                if (-not $next) { throw "No process element named '$($req.Value)' appeared after invoking '$($req.Name)'." }
                $nextPat = $null
                if (-not $next.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$nextPat)) { throw "Follow-up element '$($req.Value)' does not support InvokePattern." }
                $nextPat.Invoke()
            }
            'CheckUpdate' {
                if ($target.Current.Name -notin @('Check for updates', 'Проверить обновления')) { throw "CheckUpdate is restricted to the update button." }
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by update button." }
                $pat.Invoke()
                $state = 'Unchanged'
                for ($i = 0; $i -lt 100 -and $state -eq 'Unchanged'; $i++) {
                    Start-Sleep -Milliseconds 100
                    $state = switch ($target.Current.Name) {
                        { $_ -in @('Update available', 'Обновление доступно') } { 'Found'; break }
                        { $_ -in @('Up to date', 'Актуальная версия') } { 'UpToDate'; break }
                        { $_ -in @('Check failed', 'Ошибка проверки') } { 'Failed'; break }
                        default { 'Unchanged' }
                    }
                }
                $result.Element = [ordered]@{ UpdateCheckState = $state }
                if ($state -eq 'Unchanged') { throw "Update check state did not change." }
            }
            'Toggle' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { throw "TogglePattern unsupported by matched element." }
                $before = $pat.Current.ToggleState
                $pat.Toggle()
                $after = $before
                for ($i = 0; $i -lt 20 -and $after -eq $before; $i++) {
                    Start-Sleep -Milliseconds 100
                    $after = $pat.Current.ToggleState
                }
                Start-Sleep -Milliseconds 750
                $after = $pat.Current.ToggleState
                if ($after -eq $before) { throw "TogglePattern returned without changing the matched element state." }
            }
            'Expand' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pat)) { throw "ExpandCollapsePattern unsupported by matched element." }
                $pat.Expand()
            }
            'Select' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { throw "SelectionItemPattern unsupported by matched element." }
                $pat.Select()
                for ($i = 0; $i -lt 20 -and -not $pat.Current.IsSelected; $i++) { Start-Sleep -Milliseconds 100 }
                if (-not $pat.Current.IsSelected) { throw "SelectionItemPattern returned without selecting the matched element." }
            }
            'SetValue' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) { throw "ValuePattern unsupported by matched element." }
                if ($pat.Current.IsReadOnly) { throw "Matched element value is read-only." }
                $pat.SetValue([string]$req.Value)
            }
            default { throw "Unknown UIA operation '$($req.Operation)'." }
        }
    }
    else { throw "Unknown mode '$($req.Mode)'." }

    $result.Success = $true
}
catch {
    $result.Error = $_.Exception.Message
}
# Publish the result atomically: write a .tmp sibling first, then move it
# over ResultPath, so the controller can never observe a partially written file.
$tmpResultPath = "$ResultPath.tmp"
Set-Content -Path $tmpResultPath -Value ($result | ConvertTo-Json -Depth 5) -Encoding UTF8
Move-Item -Path $tmpResultPath -Destination $ResultPath -Force
if (-not $result.Success) { exit 1 }
exit 0
# END REMOTE IN-SESSION HELPER
'@

    $runId        = [guid]::NewGuid().ToString('N')
    $remoteDir    = "$RemoteVerifyRoot\$runId"
    $remoteHelper = "$remoteDir\helper.ps1"
    $remoteReq    = "$remoteDir\request.json"
    $remoteRes    = "$remoteDir\result.json"
    $taskName     = "BratVerify_$runId"
    $credUser     = (Import-Clixml $CredFile).UserName

    $requestJson = ([ordered]@{
        Mode           = $Mode
        AutomationId   = $AutomationId
        Name           = $Name
        ControlType    = $ControlType
        Operation      = $UiaOperation
        Value          = $Value
        TimeoutSeconds = $TimeoutSeconds
    }) | ConvertTo-Json -Depth 5

    # Put the target's logged-on session on the physical console so the helper
    # has a real interactive desktop. Fail closed if nobody is logged on. If
    # the explorer session already is the active console session, skip tscon.
    # Otherwise run tscon through a unique transient SYSTEM scheduled task:
    # tscon needs SeTcbPrivilege and a SYSTEM principal gets it without a
    # password. Everything runs on the already identity-verified PSSession.
    $sessionProbe = Invoke-Command -Session $Session -ScriptBlock {
        $e = Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -gt 0 } | Select-Object -First 1
        if (-not $e) {
            return [pscustomobject]@{ Found = $false; SessionId = $null; NeedsTscon = $false }
        }

        if (-not ('BratVerify.Native' -as [type])) {
            Add-Type -Namespace BratVerify -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint WTSGetActiveConsoleSessionId();
'@
        }
        $active = [int][BratVerify.Native]::WTSGetActiveConsoleSessionId()
        [pscustomobject]@{ Found = $true; SessionId = [int]$e.SessionId; NeedsTscon = ($e.SessionId -ne $active) }
    }
    if (-not $sessionProbe.Found) {
        throw "No logged-on interactive session on $BratMachineName (no explorer.exe with SessionId > 0). Failing closed."
    }
    if ($sessionProbe.NeedsTscon) {
        $sessionId = $sessionProbe.SessionId
        $consoleTaskName = "BratVerifyConsole_$runId"
        try {
            Invoke-Command -Session $Session -ArgumentList $consoleTaskName, $sessionId -ScriptBlock {
                param($tn, $sid)
                $action    = New-ScheduledTaskAction -Execute 'tscon.exe' -Argument "$sid /dest:console"
                $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Force | Out-Null
                $tsconStart = Get-Date
                Start-ScheduledTask -TaskName $tn
                # Poll until the task has actually run (LastRunTime past start)
                # and finished; a first Ready state before the run starts is
                # not completion.
                $deadline = (Get-Date).AddSeconds(30)
                $finished = $false
                $t = $null
                while ((Get-Date) -lt $deadline) {
                    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                    $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                    if ($t -and $t.State -ne 'Running' -and $info -and $info.LastRunTime -and $info.LastRunTime -gt $tsconStart) {
                        $finished = $true
                        break
                    }
                    Start-Sleep -Milliseconds 200
                }
                $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                $code = if ($info) { $info.LastTaskResult } else { $null }
                if (-not $finished) {
                    throw "tscon task '$tn' did not complete within 30 s on $env:COMPUTERNAME (state $(if ($t) { [string]$t.State } else { 'Missing' })). Failing closed."
                }
                if ($null -eq $code -or $code -ne 0) {
                    throw "tscon.exe failed to move session $sid to the console (LastTaskResult=$code). Failing closed."
                }
            }
        }
        finally {
            Invoke-Command -Session $Session -ArgumentList $consoleTaskName -ScriptBlock {
                param($tn)
                Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
            }
        }
    }

    $operationError = $null
    $operationResult = $null
    try {
        Invoke-Command -Session $Session -ArgumentList $remoteDir, $remoteHelper, $remoteReq, $helper, $requestJson -ScriptBlock {
            param($dir, $h, $rq, $helperText, $reqText)
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -Path $h  -Value $helperText -Encoding UTF8
            Set-Content -Path $rq -Value $reqText    -Encoding UTF8
        }

        $taskStart = Invoke-Command -Session $Session -ArgumentList $taskName, $remoteHelper, $remoteReq, $remoteRes, $credUser -ScriptBlock {
            param($tn, $h, $rq, $rs, $user)
            $arg = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$h`" `"$rq`" `"$rs`""
            $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName $tn -Action $action -Principal $principal -Force | Out-Null
            # Capture the start timestamp on the remote clock immediately
            # before Start-ScheduledTask and return it: RanSinceStart then
            # compares remote LastRunTime against a remote-clock value, which
            # removes both the fast-exit race (local Get-Date taken after
            # Invoke-Command returns can postdate a quick run) and any
            # host/VM clock skew.
            $started = Get-Date
            Start-ScheduledTask -TaskName $tn
            $started
        }

        # TimeoutSeconds bounds the helper's own UI matching; the controller
        # adds 30 s of slack for WinRM transport and task startup on top.
        # The slack deadline polls on the local clock; the remote status
        # probes compare LastRunTime against $taskStart from the remote clock.
        $deadline   = (Get-Date).AddSeconds($TimeoutSeconds + 30)
        $resultRaw  = $null
        while ((Get-Date) -lt $deadline) {
            $status = Invoke-Command -Session $Session -ArgumentList $taskName, $remoteRes, $taskStart -ScriptBlock {
                param($tn, $rs, $started)
                $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
                $ran = $false
                if ($info -and $info.LastRunTime -and $info.LastRunTime -gt $started) { $ran = $true }
                [pscustomobject]@{
                    State          = $(if ($t) { [string]$t.State } else { 'Missing' })
                    HasResult      = (Test-Path $rs)
                    RanSinceStart  = $ran
                    LastTaskResult = $(if ($info) { $info.LastTaskResult } else { $null })
                }
            }
            if ($status.HasResult) {
                $resultRaw = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) Get-Content $rs -Raw }
                break
            }
            if ($status.State -eq 'Missing') { throw "Transient helper task '$taskName' disappeared on $BratMachineName." }
            # Fast exit: the task ran and is already finished, but no result
            # file exists. An initial Ready state before LastRunTime advances
            # is NOT a fast exit and keeps polling.
            if ($status.State -eq 'Ready' -and $status.RanSinceStart) {
                Start-Sleep -Milliseconds 300
                $late = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) if (Test-Path $rs) { Get-Content $rs -Raw } else { $null } }
                if ($late) { $resultRaw = $late; break }
                throw "Interactive helper exited without writing a result JSON on $BratMachineName (LastTaskResult=$($status.LastTaskResult))."
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $resultRaw) {
            # One final atomic read: the helper may have published its result
            # while the last poll round-trip was in flight.
            $final = Invoke-Command -Session $Session -ArgumentList $remoteRes -ScriptBlock { param($rs) if (Test-Path $rs) { Get-Content $rs -Raw } else { $null } }
            if ($final) {
                $resultRaw = $final
            }
            else {
                throw "Timed out after $($TimeoutSeconds + 30) s waiting for the interactive helper on $BratMachineName (UI match budget $TimeoutSeconds s + 30 s transport/startup slack)."
            }
        }

        $res = $resultRaw | ConvertFrom-Json
        if (-not $res.Success) { throw "Remote interactive helper failed: $($res.Error)" }

        $operationResult = $res
    }
    catch { $operationError = $_ }

    $cleanupError = $null
    try {
        Invoke-Command -Session $Session -ArgumentList $taskName, $remoteDir -ScriptBlock {
            param($tn, $dir)
            $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
            if ($t -and $t.State -eq 'Running') {
                Stop-ScheduledTask -TaskName $tn -ErrorAction Stop
                $stopDeadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $stopDeadline) {
                    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                    if (-not $t -or $t.State -ne 'Running') { break }
                    Start-Sleep -Milliseconds 200
                }
                if ($t -and $t.State -eq 'Running') {
                    throw "Transient task '$tn' is still running after cleanup timeout."
                }
            }
            if (Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction Stop
            }
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction Stop }
            if ((Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue) -or (Test-Path $dir)) {
                throw "Transient task or helper directory remained after cleanup."
            }
        }
    }
    catch { $cleanupError = $_ }

    if ($cleanupError) {
        $primary = if ($operationError) { " Primary failure: $($operationError.Exception.Message)" } else { '' }
        throw "Remote helper cleanup failed on $BratMachineName`: $($cleanupError.Exception.Message).$primary"
    }
    if ($operationError) { throw $operationError }
    return $operationResult
}

switch ($Action) {
    'identity' {
        $s = New-VerifiedBratSession
        try {
            $id = Invoke-Command -Session $s {
                [pscustomobject]@{
                    MachineName = [Environment]::MachineName
                    UserName    = [Environment]::UserDomainName + '\' + [Environment]::UserName
                }
            }
            Write-Host "Verified identity: $($id.MachineName) @ $BratIp (connected as $($id.UserName))" -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }

    'deploy' {
        if (-not $Version) { throw "deploy requires -Version (e.g. -Version 2.48.0-r3)." }

        # Fail-closed integrity gate BEFORE any remote contact: the exact
        # repo-root artifact and its .sha256 sidecar must both exist, and the
        # recomputed hash must match the sidecar exactly. No match => no deploy.
        $zipName = "VPNRouter-v$Version-win.zip"
        $zipPath = Join-Path $Root $zipName
        $shaPath = "$zipPath.sha256"
        if (-not (Test-Path $zipPath)) { throw "Artifact not found: $zipPath. Build it first (build.ps1 -Version $Version). Failing closed." }
        if (-not (Test-Path $shaPath)) { throw "SHA256 sidecar not found: $shaPath. Refusing to deploy without it. Failing closed." }

        $expected = (Get-Content $shaPath -Raw).Trim().ToLower()
        if ($expected -notmatch '^[0-9a-f]{64}$') { throw "Sidecar $shaPath must contain exactly one 64-char lowercase hex SHA256 (got '$expected'). Failing closed." }
        $actual = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
        if ($actual -ne $expected) { throw "SHA256 mismatch for $zipName`: sidecar=$expected actual=$actual. Failing closed; not deploying." }
        Write-Host "SHA256 verified for $zipName`: $actual" -ForegroundColor Green

        # The generic deploy script verifies WINBRAT on the same session it uses
        # for process stop/copy/install, avoiding a check-then-reconnect gap.
        & (Join-Path $Root 'deploy-to-testpc.ps1') -TestHost $BratIp -Version $Version -Credential (Import-Clixml $CredFile) -ExpectedMachineName $BratMachineName
        if ($LASTEXITCODE) { throw "deploy-to-testpc.ps1 failed (exit $LASTEXITCODE)." }
    }

    'uia' {
        if (-not ($AutomationId -or $Name)) { throw "uia requires -AutomationId and/or -Name." }
        if ($UiaOperation -eq 'SetValue' -and -not $PSBoundParameters.ContainsKey('Value')) { throw "-UiaOperation SetValue requires -Value." }
        if ($UiaOperation -eq 'InvokeThen' -and -not $PSBoundParameters.ContainsKey('Value')) { throw "-UiaOperation InvokeThen requires -Value with the follow-up element Name." }
        $s = New-VerifiedBratSession
        try {
            $res = Invoke-BratInteractive -Session $s -Mode 'uia' -AutomationId $AutomationId -Name $Name -ControlType $ControlType -UiaOperation $UiaOperation -Value $Value -TimeoutSeconds $TimeoutSeconds
            if ($UiaOperation -in @('Inspect', 'CheckUpdate')) {
                Write-Host "Inspect result on $BratMachineName`:" -ForegroundColor Green
                Write-Host ($res.Element | ConvertTo-Json -Compress)
            } else {
                Write-Host "OK: $UiaOperation completed on $BratMachineName." -ForegroundColor Green
            }
        }
        finally { Remove-PSSession $s }
    }

    'state' {
        $s = New-VerifiedBratSession
        try {
            $state = Invoke-Command -Session $s -ScriptBlock {
                $guiPaths = @('C:\Program Files\VPNRouter\app\VPNRouter.App.exe')
                $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'

                $owned = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                    $path = [string]$_.ExecutablePath
                    $guiPaths -icontains $path -or $path -ieq $corePath
                })
                $gui = @($owned | Where-Object { $guiPaths -icontains ([string]$_.ExecutablePath) })
                $core = @($owned | Where-Object { ([string]$_.ExecutablePath) -ieq $corePath })

                $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
                $tunState = if ($tun) { [string]$tun.Status } else { 'Absent' }

                function Get-FixedProbeRouteScope {
                    $hosts = @('www.gstatic.com', 'stun.l.google.com')
                    $scopes = foreach ($hostName in $hosts) {
                        try {
                            $address = [System.Net.Dns]::GetHostAddresses($hostName) |
                                Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                                Select-Object -First 1
                            if (-not $address) { 'Unknown'; continue }
                            $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop |
                                Select-Object -First 1
                            $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                            if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                        }
                        catch { 'Unknown' }
                    }
                    if (@($scopes | Where-Object { $_ -eq 'Direct' }).Count -gt 0) { return 'Direct' }
                    if (@($scopes | Where-Object { $_ -eq 'Tunnel' }).Count -eq $hosts.Count) { return 'Tunnel' }
                    return 'Unknown'
                }

                $workingSetBytes = ($owned | Measure-Object -Property WorkingSetSize -Sum).Sum
                if ($null -eq $workingSetBytes) { $workingSetBytes = 0 }
                $handleCount = ($owned | Measure-Object -Property HandleCount -Sum).Sum
                if ($null -eq $handleCount) { $handleCount = 0 }
                $threadCount = ($owned | Measure-Object -Property ThreadCount -Sum).Sum
                if ($null -eq $threadCount) { $threadCount = 0 }

                [ordered]@{
                    AtUtc        = [DateTimeOffset]::UtcNow.ToString('o')
                    GuiCount     = $gui.Count
                    CoreCount    = $core.Count
                    TunState     = $tunState
                    RouteScope   = Get-FixedProbeRouteScope
                    WorkingSetMb = [Math]::Round(([double]$workingSetBytes / 1MB), 1)
                    Handles      = [int]$handleCount
                    Threads      = [int]$threadCount
                }
            }
            $cleanState = [ordered]@{
                AtUtc        = [string]$state.AtUtc
                GuiCount     = [int]$state.GuiCount
                CoreCount    = [int]$state.CoreCount
                TunState     = [string]$state.TunState
                RouteScope   = [string]$state.RouteScope
                WorkingSetMb = [double]$state.WorkingSetMb
                Handles      = [int]$state.Handles
                Threads      = [int]$state.Threads
            }
            Write-Output ($cleanState | ConvertTo-Json -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'probe' {
        $s = New-VerifiedBratSession
        try {
            $probe = Invoke-Command -Session $s -ArgumentList $ProbeProfile, $TimeoutSeconds -ScriptBlock {
                param($profile, $timeoutSeconds)

                $httpsHost = 'www.gstatic.com'
                $httpsUrl = 'https://www.gstatic.com/generate_204'
                $stunHost = 'stun.l.google.com'
                $stunPort = 19302

                function Resolve-FixedIpv4([string]$hostName) {
                    [System.Net.Dns]::GetHostAddresses($hostName) |
                        Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                        Select-Object -First 1
                }

                function Get-RouteScope([System.Net.IPAddress[]]$addresses) {
                    $scopes = foreach ($address in $addresses) {
                        if (-not $address) { 'Unknown'; continue }
                        try {
                            $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop |
                                Select-Object -First 1
                            $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
                            if ($adapter.Name -eq 'VPNRouter-TUN' -and $adapter.Status -eq 'Up') { 'Tunnel' } else { 'Direct' }
                        }
                        catch { 'Unknown' }
                    }
                    if (@($scopes | Where-Object { $_ -eq 'Direct' }).Count -gt 0) { return 'Direct' }
                    if (@($scopes | Where-Object { $_ -eq 'Tunnel' }).Count -eq $addresses.Count) { return 'Tunnel' }
                    return 'Unknown'
                }

                function Get-ProbeErrorKind([Exception]$exception) {
                    $current = $exception
                    while ($current.InnerException) { $current = $current.InnerException }
                    if ($current -is [System.TimeoutException] -or
                        $current -is [System.Threading.Tasks.TaskCanceledException]) { return 'Timeout' }
                    if ($current -is [System.Net.Sockets.SocketException]) {
                        if ($current.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) { return 'Timeout' }
                        return 'Socket'
                    }
                    return 'Other'
                }

                function New-StunBindingRequest([int]$packetSize) {
                    if ($packetSize -lt 24 -or ($packetSize % 4) -ne 0) { throw 'Invalid fixed STUN packet size.' }
                    $request = New-Object byte[] $packetSize
                    $messageLength = $packetSize - 20
                    $request[0] = 0x00; $request[1] = 0x01
                    $request[2] = [byte](($messageLength -shr 8) -band 0xff)
                    $request[3] = [byte]($messageLength -band 0xff)
                    $request[4] = 0x21; $request[5] = 0x12; $request[6] = 0xA4; $request[7] = 0x42
                    $transactionId = New-Object byte[] 12
                    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
                    try { $rng.GetBytes($transactionId) } finally { $rng.Dispose() }
                    [Array]::Copy($transactionId, 0, $request, 8, 12)

                    # RFC-compatible unknown optional attribute pads fixed-size
                    # requests without simulating a proprietary application.
                    $attributeLength = $packetSize - 24
                    $request[20] = 0xC0; $request[21] = 0x01
                    $request[22] = [byte](($attributeLength -shr 8) -band 0xff)
                    $request[23] = [byte]($attributeLength -band 0xff)
                    for ($i = 24; $i -lt $packetSize; $i++) { $request[$i] = 0x58 }

                    [pscustomobject]@{ Bytes = $request; TransactionId = $transactionId }
                }

                function Test-UdpProxyChain(
                    [string]$controller,
                    [string]$secret,
                    [int]$sourcePort,
                    [string]$destinationIp,
                    [int]$destinationPort,
                    [string]$expectedTag,
                    [int]$timeoutSeconds) {
                    if (-not $controller -or -not $expectedTag) { return $false }
                    $client = New-Object System.Net.Http.HttpClient
                    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
                    $request = New-Object System.Net.Http.HttpRequestMessage(
                        [System.Net.Http.HttpMethod]::Get,
                        "http://$controller/connections")
                    try {
                        if (-not [string]::IsNullOrEmpty($secret)) {
                            [void]$request.Headers.TryAddWithoutValidation('Authorization', "Bearer $secret")
                        }
                        $response = $client.SendAsync($request).GetAwaiter().GetResult()
                        try {
                            if (-not $response.IsSuccessStatusCode) { return $false }
                            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                            $connection = @($body.connections | Where-Object {
                                [string]$_.metadata.network -ieq 'udp' -and
                                [int]$_.metadata.sourcePort -eq $sourcePort -and
                                [string]$_.metadata.destinationIP -eq $destinationIp -and
                                [int]$_.metadata.destinationPort -eq $destinationPort
                            } | Select-Object -First 1)
                            return $connection.Count -eq 1 -and
                                @($connection[0].chains) -contains $expectedTag
                        }
                        finally { $response.Dispose() }
                    }
                    catch { return $false }
                    finally {
                        $request.Dispose()
                        $client.Dispose()
                    }
                }

                $httpsAddress = Resolve-FixedIpv4 $httpsHost
                $stunAddress = Resolve-FixedIpv4 $stunHost
                $routeScope = Get-RouteScope @($httpsAddress, $stunAddress)
                if ($routeScope -ne 'Tunnel') {
                    throw "Fixed probes are not tunnel-scoped (scope=$routeScope). Dataplane verification is blocked."
                }

                Add-Type -AssemblyName System.Net.Http
                [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

                # The OS route only proves entry into the TUN. The Clash delay
                # endpoint for the canonical `proxy` outbound proves sing-box
                # can actually egress through the selected proxy, even when the
                # caller process itself is not included by split-tunnel rules.
                $proxyResult = [ordered]@{ Success = $false; Status = 0; LatencyMs = 0; Error = 'Other' }
                $proxyWatch = [System.Diagnostics.Stopwatch]::StartNew()
                $proxyHttp = New-Object System.Net.Http.HttpClient
                $proxyHttp.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
                $config = $null
                $controller = $null
                $secret = $null
                $expectedUdpTag = $null
                try {
                    $configPath = 'C:\ProgramData\VPNRouter\config\current.json'
                    $config = Get-Content -LiteralPath $configPath -Raw -ErrorAction Stop | ConvertFrom-Json
                    $controller = [string]$config.experimental.clash_api.external_controller
                    if ($controller -notmatch '^127\.0\.0\.1:(?<port>[1-9][0-9]{0,4})$' -or [int]$Matches.port -gt 65535) {
                        throw 'The Clash controller is not fixed to IPv4 loopback.'
                    }
                    $secret = [string]$config.experimental.clash_api.secret
                    $expectedUdpTag = if (@($config.outbounds | Where-Object { [string]$_.tag -eq 'proxy-udp' }).Count -gt 0) {
                        'proxy-udp'
                    } else {
                        'proxy'
                    }
                    $delayUrl = "http://$controller/proxies/proxy/delay?url=$([Uri]::EscapeDataString($httpsUrl))&timeout=$($timeoutSeconds * 1000)"
                    $proxyRequest = New-Object System.Net.Http.HttpRequestMessage(
                        [System.Net.Http.HttpMethod]::Get,
                        $delayUrl)
                    try {
                        if (-not [string]::IsNullOrEmpty($secret)) {
                            [void]$proxyRequest.Headers.TryAddWithoutValidation('Authorization', "Bearer $secret")
                        }
                        $proxyResponse = $proxyHttp.SendAsync($proxyRequest).GetAwaiter().GetResult()
                        try {
                            $proxyWatch.Stop()
                            $proxyResult.Status = [int]$proxyResponse.StatusCode
                            $proxyResult.LatencyMs = [int]$proxyWatch.ElapsedMilliseconds
                            if ($proxyResponse.IsSuccessStatusCode) {
                                $proxyBody = $proxyResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                                $proxyResult.Success = [int]$proxyBody.delay -gt 0
                                $proxyResult.Error = if ($proxyResult.Success) { 'None' } else { 'Unreachable' }
                            }
                            else { $proxyResult.Error = 'HttpStatus' }
                        }
                        finally { $proxyResponse.Dispose() }
                    }
                    finally { $proxyRequest.Dispose() }
                }
                catch {
                    $proxyWatch.Stop()
                    $proxyResult.LatencyMs = [int]$proxyWatch.ElapsedMilliseconds
                    $proxyResult.Error = Get-ProbeErrorKind $_.Exception
                }
                finally { $proxyHttp.Dispose() }

                # The Clash delay request above performs the fixed HTTPS fetch
                # through the canonical `proxy` outbound. Reuse that result as
                # the HTTPS dataplane assertion; a second request made by this
                # WinRM PowerShell process could legitimately select `direct`
                # in include-split mode and must never satisfy the gate.
                $httpResult = [ordered]@{
                    Success       = [bool]$proxyResult.Success
                    ProxyObserved = [bool]$proxyResult.Success
                    LatencyMs     = [int]$proxyResult.LatencyMs
                    Error         = [string]$proxyResult.Error
                }

                $sizes = if ($profile -eq 'Boundary') { @(64, 512, 1200, 1392) } else { @(64) }
                $udpResults = @()
                foreach ($size in $sizes) {
                    $row = [ordered]@{ Size = $size; Success = $false; ProxyObserved = $false; LatencyMs = 0; Error = 'Other' }
                    $udp = New-Object System.Net.Sockets.UdpClient([System.Net.Sockets.AddressFamily]::InterNetwork)
                    $udp.Client.ReceiveTimeout = $timeoutSeconds * 1000
                    $udpWatch = [System.Diagnostics.Stopwatch]::StartNew()
                    try {
                        $request = New-StunBindingRequest $size
                        $udp.Connect($stunAddress, $stunPort)
                        [void]$udp.Send($request.Bytes, $request.Bytes.Length)
                        $sourcePort = ([System.Net.IPEndPoint]$udp.Client.LocalEndPoint).Port
                        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                        $responseBytes = $udp.Receive([ref]$remote)
                        $udpWatch.Stop()
                        $valid = $responseBytes.Length -ge 20 -and
                            $responseBytes[0] -eq 0x01 -and $responseBytes[1] -eq 0x01
                        if ($valid) {
                            for ($i = 0; $i -lt 12; $i++) {
                                if ($responseBytes[8 + $i] -ne $request.TransactionId[$i]) { $valid = $false; break }
                            }
                        }
                        if ($valid) {
                            $row.ProxyObserved = Test-UdpProxyChain `
                                $controller `
                                $secret `
                                $sourcePort `
                                $stunAddress.IPAddressToString `
                                $stunPort `
                                $expectedUdpTag `
                                $timeoutSeconds
                        }
                        $row.Success = $valid -and $row.ProxyObserved
                        $row.LatencyMs = [int]$udpWatch.ElapsedMilliseconds
                        $row.Error = if (-not $valid) {
                            'InvalidResponse'
                        } elseif (-not $row.ProxyObserved) {
                            'UnverifiedOutbound'
                        } else {
                            'None'
                        }
                    }
                    catch {
                        $udpWatch.Stop()
                        $row.LatencyMs = [int]$udpWatch.ElapsedMilliseconds
                        $row.Error = Get-ProbeErrorKind $_.Exception
                    }
                    finally { $udp.Dispose() }
                    $udpResults += [pscustomobject]$row
                }

                [ordered]@{
                    AtUtc      = [DateTimeOffset]::UtcNow.ToString('o')
                    Profile    = $profile
                    RouteScope = $routeScope
                    Success    = ($proxyResult.Success -and $httpResult.Success -and @($udpResults | Where-Object { -not $_.Success }).Count -eq 0)
                    Proxy      = [pscustomobject]$proxyResult
                    Http       = [pscustomobject]$httpResult
                    Udp        = $udpResults
                }
            }
            $cleanUdp = @($probe.Udp | ForEach-Object {
                [ordered]@{
                    Size      = [int]$_.Size
                    Success   = [bool]$_.Success
                    ProxyObserved = [bool]$_.ProxyObserved
                    LatencyMs = [int]$_.LatencyMs
                    Error     = [string]$_.Error
                }
            })
            $cleanProbe = [ordered]@{
                AtUtc      = [string]$probe.AtUtc
                Profile    = [string]$probe.Profile
                RouteScope = [string]$probe.RouteScope
                Success    = [bool]$probe.Success
                Proxy      = [ordered]@{
                    Success   = [bool]$probe.Proxy.Success
                    Status    = [int]$probe.Proxy.Status
                    LatencyMs = [int]$probe.Proxy.LatencyMs
                    Error     = [string]$probe.Proxy.Error
                }
                Http       = [ordered]@{
                    Success       = [bool]$probe.Http.Success
                    ProxyObserved = [bool]$probe.Http.ProxyObserved
                    LatencyMs     = [int]$probe.Http.LatencyMs
                    Error         = [string]$probe.Http.Error
                }
                Udp        = $cleanUdp
            }
            Write-Output ($cleanProbe | ConvertTo-Json -Depth 6 -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'lifecycle' {
        if (-not $SinceUtc) { throw 'lifecycle requires -SinceUtc in round-trip ISO-8601 format.' }
        $since = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact(
            $SinceUtc,
            'o',
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$since)) {
            throw 'SinceUtc must use round-trip ISO-8601 format.'
        }
        if ($since -gt [DateTimeOffset]::UtcNow.AddMinutes(1) -or
            $since -lt [DateTimeOffset]::UtcNow.AddHours(-24)) {
            throw 'SinceUtc must be within the last 24 hours and not in the future.'
        }

        $s = New-VerifiedBratSession
        try {
            $summary = Invoke-Command -Session $s -ArgumentList $since.ToString('o') -ScriptBlock {
                param($sinceText)
                $since = [DateTimeOffset]::ParseExact(
                    $sinceText,
                    'o',
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind)
                $logDir = 'C:\ProgramData\VPNRouter\logs'
                if (-not (Test-Path $logDir)) { throw 'Lifecycle log source is unavailable.' }

                $events = @()
                $counts = @{}
                $errorCount = 0
                $fatalCount = 0
                $unknownErrorCount = 0
                $recentCount = 0
                $maxLines = 50000
                $files = @(Get-ChildItem $logDir -Filter 'vpnrouter*.log' -File | Sort-Object LastWriteTime)
                if (-not $files) { throw 'Lifecycle log source is unavailable.' }

                foreach ($file in $files) {
                    $lines = @(Get-Content $file.FullName -Tail $maxLines)
                    if ($lines.Count -eq $maxLines) {
                        $oldestTimestamp = $null
                        foreach ($candidateLine in $lines) {
                            if ($candidateLine -notmatch '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') { continue }
                            $candidateTimestamp = [DateTimeOffset]::MinValue
                            if ([DateTimeOffset]::TryParseExact(
                                $Matches.timestamp,
                                'yyyy-MM-dd HH:mm:ss.fff zzz',
                                [System.Globalization.CultureInfo]::InvariantCulture,
                                [System.Globalization.DateTimeStyles]::None,
                                [ref]$candidateTimestamp)) {
                                $oldestTimestamp = $candidateTimestamp
                                break
                            }
                        }
                        if (-not $oldestTimestamp -or $oldestTimestamp -ge $since) {
                            throw 'Lifecycle window exceeds the bounded line cap.'
                        }
                    }
                    $include = $false
                    $at = $null
                    foreach ($line in $lines) {
                        if ($line -match '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') {
                            $parsed = [DateTimeOffset]::MinValue
                            $ok = [DateTimeOffset]::TryParseExact(
                                $Matches.timestamp,
                                'yyyy-MM-dd HH:mm:ss.fff zzz',
                                [System.Globalization.CultureInfo]::InvariantCulture,
                                [System.Globalization.DateTimeStyles]::None,
                                [ref]$parsed)
                            $include = $ok -and $parsed -ge $since
                            $at = if ($include) { $parsed } else { $null }
                            if ($include) {
                                $recentCount++
                                if ($recentCount -gt $maxLines) { throw 'Lifecycle window exceeds the bounded line cap.' }
                            }
                        }
                        if (-not $include -or -not $at) { continue }

                        $isErrorLine = $line -match '\[ERR\]|Exception|FATAL'
                        if ($isErrorLine) {
                            $errorCount++
                            if ($line -match 'FATAL') { $fatalCount++ }
                        }

                        $kind = $null
                        if ($line -match '\[VpnEngine\].*sing-box started|TUN interface ready|TUN ready') {
                            $kind = if ($line -match 'TUN') { 'TunReady' } else { 'CoreStarted' }
                        }
                        elseif ($line -match '\[HealthMonitor\] Started') { $kind = 'MonitorStarted' }
                        elseif ($line -match '\[HealthMonitor\] Stopped') { $kind = 'MonitorStopped' }
                        elseif ($line -match '\[HealthMonitor\] Health check failed') { $kind = 'HealthFailed' }
                        elseif ($line -match '\[HealthMonitor\] sing-box WEDGED') { $kind = 'CoreWedged' }
                        elseif ($line -match '\[HealthMonitor\] Restarting sing-box') { $kind = 'RestartRequested' }
                        elseif ($line -match '\[HealthMonitor\] sing-box restarted successfully') { $kind = 'RestartSucceeded' }
                        elseif ($line -match '\[HealthMonitor\].*requesting failover') { $kind = 'FailoverRequested' }
                        elseif ($line -match '\[AutoFailover\].*switched|AutoFailover.*commit') { $kind = 'FailoverCommitted' }
                        elseif ($line -match '\[HealthMonitor\] VPN is up') { $kind = 'HealthRecovered' }
                        if ($isErrorLine -and -not $kind) { $unknownErrorCount++ }

                        if ($kind) {
                            if (-not $counts.ContainsKey($kind)) { $counts[$kind] = 0 }
                            $counts[$kind]++
                            $events += [pscustomobject]@{ AtUtc = $at.ToUniversalTime().ToString('o'); Kind = $kind }
                        }
                    }
                }
                if ($recentCount -eq 0) { throw 'Lifecycle window contains no timestamped entries.' }

                [ordered]@{
                    SinceUtc          = $since.ToUniversalTime().ToString('o')
                    RecentEntryCount  = $recentCount
                    EventCounts       = @($counts.GetEnumerator() | ForEach-Object {
                        [pscustomobject]@{ Kind = [string]$_.Key; Count = [int]$_.Value }
                    })
                    Events            = $events
                    ErrorCount        = $errorCount
                    FatalCount        = $fatalCount
                    UnknownErrorCount = $unknownErrorCount
                }
            }
            $cleanCounts = [ordered]@{}
            foreach ($pair in @($summary.EventCounts)) { $cleanCounts[[string]$pair.Kind] = [int]$pair.Count }
            $cleanEvents = @($summary.Events | ForEach-Object {
                [ordered]@{ AtUtc = [string]$_.AtUtc; Kind = [string]$_.Kind }
            })
            $cleanSummary = [ordered]@{
                SinceUtc          = [string]$summary.SinceUtc
                RecentEntryCount  = [int]$summary.RecentEntryCount
                EventCounts       = $cleanCounts
                Events            = $cleanEvents
                ErrorCount        = [int]$summary.ErrorCount
                FatalCount        = [int]$summary.FatalCount
                UnknownErrorCount = [int]$summary.UnknownErrorCount
            }
            Write-Output ($cleanSummary | ConvertTo-Json -Depth 6 -Compress)
        }
        finally { Remove-PSSession $s }
    }

    'logs' {
        $s = New-VerifiedBratSession
        try {
            $sinceText = [DateTimeOffset]::Now.AddMinutes(-$LogWindowMinutes).ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
            $scan = Invoke-Command -Session $s -ArgumentList $sinceText, $LogPattern -ScriptBlock {
                param($sinceText, $logPattern)
                $since = [DateTimeOffset]::ParseExact(
                    $sinceText,
                    'o',
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind)
                $dir = 'C:\ProgramData\VPNRouter\logs'
                if (-not (Test-Path $dir)) { return @{ Verified = $false; ErrorCount = 0; ContextCount = 0; RecentEntryCount = 0; Truncated = $false } }
                $allFiles = @(Get-ChildItem $dir -Filter 'vpnrouter*.log' -File | Sort-Object LastWriteTime)
                if (-not $allFiles) { return @{ Verified = $false; ErrorCount = 0; ContextCount = 0; RecentEntryCount = 0; Truncated = $false } }
                $files = @($allFiles | Where-Object { $_.LastWriteTimeUtc -ge $since.UtcDateTime })
                if (-not $files) {
                    return @{ Verified = $false; ErrorCount = 0; ContextCount = 0; RecentEntryCount = 0; Truncated = $false }
                }
                $errorCount = 0
                $contextCount = 0
                $recentEntryCount = 0
                $maxLines = 50000
                foreach ($f in $files) {
                    $lines = @(Get-Content $f.FullName -Tail $maxLines)
                    $include = $false
                    $oldestParsed = $null
                    foreach ($line in $lines) {
                        if ($line -match '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})') {
                            $parsed = [DateTimeOffset]::MinValue
                            $parsedOk = [DateTimeOffset]::TryParseExact(
                                $Matches.timestamp,
                                'yyyy-MM-dd HH:mm:ss.fff zzz',
                                [System.Globalization.CultureInfo]::InvariantCulture,
                                [System.Globalization.DateTimeStyles]::None,
                                [ref]$parsed)
                            if ($parsedOk -and $null -eq $oldestParsed) { $oldestParsed = $parsed }
                            $include = $parsedOk -and $parsed -ge $since
                            if ($include) { $recentEntryCount++ }
                        }
                        if ($include -and $line -match '\[ERR\]|Exception|FATAL') { $errorCount++ }
                        if ($include -and $logPattern -and $line -match $logPattern) { $contextCount++ }
                    }
                    if ($lines.Count -ge $maxLines -and ($null -eq $oldestParsed -or $oldestParsed -ge $since)) {
                        return @{ Verified = $false; ErrorCount = $errorCount; ContextCount = $contextCount; RecentEntryCount = $recentEntryCount; Truncated = $true }
                    }
                }
                if ($recentEntryCount -eq 0) {
                    return @{ Verified = $false; ErrorCount = 0; ContextCount = 0; RecentEntryCount = 0; Truncated = $false }
                }
                @{ Verified = $true; ErrorCount = $errorCount; ContextCount = $contextCount; RecentEntryCount = $recentEntryCount; Truncated = $false }
            }
            if (-not [bool]$scan.Verified) {
                throw "Cannot verify the bounded remote log window on $BratMachineName. Failing closed."
            }
            if ($LogPattern -and [int]$scan.ContextCount -gt 0) {
                Write-Host "Context matches on ${BratMachineName}: $([int]$scan.ContextCount) sanitized count(s)." -ForegroundColor Cyan
            }
            if ([int]$scan.ErrorCount -gt 0) {
                Write-Host "[!] $([int]$scan.ErrorCount) error pattern(s) in the bounded remote log window." -ForegroundColor Red
                exit 1
            }
            Write-Host "CLEAN: no error patterns in $([int]$scan.RecentEntryCount) recent remote log entries." -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }

    'emergencycleanup' {
        $s = New-VerifiedBratSession
        try {
            $cleanup = Invoke-Command -Session $s -ScriptBlock {
                $appDir = 'C:\Program Files\VPNRouter\app'
                $servicePaths = @(
                    (Join-Path $appDir 'VPNRouter.Service.exe'),
                    (Join-Path $appDir 'service\VPNRouter.Service.exe'))
                $ownedPaths = @(
                    (Join-Path $appDir 'VPNRouter.App.exe'),
                    (Join-Path $appDir 'VPNRouter.GUI.exe'),
                    $servicePaths[0],
                    $servicePaths[1],
                    'C:\ProgramData\VPNRouter\bin\sing-box.exe')

                $service = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'" -ErrorAction Stop
                if ($service) {
                    $serviceExe = if ([string]$service.PathName -match '^\s*"(?<exe>[^"]+)"') {
                        $Matches.exe
                    } else {
                        ([string]$service.PathName -split '\s+--service(?:\s|$)', 2)[0].Trim()
                    }
                    if ($servicePaths -notcontains ([IO.Path]::GetFullPath($serviceExe))) {
                        throw 'The VPNRouter service name is owned by a non-canonical executable path.'
                    }
                    if ([string]$service.State -ine 'Stopped') {
                        Stop-Service -Name 'VPNRouter' -Force -ErrorAction Stop
                    }
                }

                $owned = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
                    $path = [string]$_.ExecutablePath
                    $path -and $ownedPaths -icontains ([IO.Path]::GetFullPath($path))
                })
                foreach ($process in $owned) {
                    Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
                }

                $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
                do {
                    $core = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
                        ([string]$_.ExecutablePath) -ieq 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
                    })
                    $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue
                    if ($core.Count -eq 0 -and -not $tun) { break }
                    Start-Sleep -Seconds 1
                } while ([DateTimeOffset]::UtcNow -lt $deadline)

                [ordered]@{
                    StoppedOwnedProcessCount = [int]$owned.Count
                    CoreCount = [int]$core.Count
                    TunAbsent = [bool](-not $tun)
                }
            }
            $safe = [ordered]@{
                StoppedOwnedProcessCount = [int]$cleanup.StoppedOwnedProcessCount
                CoreCount = [int]$cleanup.CoreCount
                TunAbsent = [bool]$cleanup.TunAbsent
            }
            Write-Output ($safe | ConvertTo-Json -Compress)
            if ($safe.CoreCount -ne 0 -or -not $safe.TunAbsent) {
                throw 'Emergency cleanup did not reach core-absent/TUN-absent state.'
            }
        }
        finally { Remove-PSSession $s }
    }

    'tuninventory' {
        $s = New-VerifiedBratSession
        try {
            $inventory = Invoke-Command -Session $s -ScriptBlock {
                $networkClass = '{4D36E972-E325-11CE-BFC1-08002BE10318}'
                $connectionRoot = "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network\$networkClass"
                $allConnections = @(
                    Get-ChildItem $connectionRoot -ErrorAction SilentlyContinue |
                        ForEach-Object {
                            $connectionKey = Join-Path $_.PSPath 'Connection'
                            $values = Get-ItemProperty $connectionKey -ErrorAction SilentlyContinue
                            if ($values -and $values.Name) {
                                [pscustomobject]@{
                                    Name          = [string]$values.Name
                                    PnpInstanceId = [string]$values.PnpInstanceID
                                    ConnectionId  = $_.PSChildName
                                }
                            }
                        })

                $wintunRoot = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\SWD\WINTUN'
                $ownedConnections = @($allConnections | Where-Object {
                    $_.Name -eq 'VPNRouter-TUN' -or
                    $_.Name -eq 'sing-box-tun' -or
                    $_.Name -like 'sing-box-tun-*'
                })
                $numberedCount = @($allConnections | Where-Object {
                    $_.Name -match '^VPNRouter-TUN \d+$'
                }).Count
                $wintunCount = @(Get-ChildItem $wintunRoot -ErrorAction SilentlyContinue).Count

                [pscustomobject]@{
                    OwnedConnections      = $ownedConnections
                    NumberedVpnRouterCount = $numberedCount
                    TotalWintunDeviceCount = $wintunCount
                }
            }
            Write-Host "Read-only TUN inventory on $BratMachineName`:" -ForegroundColor Cyan
            Write-Host ($inventory | ConvertTo-Json -Depth 5)
        }
        finally { Remove-PSSession $s }
    }

    'updateprobe' {
        if (-not $Version) { throw "updateprobe requires -Version." }
        $s = New-VerifiedBratSession
        try {
            $probe = Invoke-Command -Session $s -ArgumentList $Version -ScriptBlock {
                param($version)
                try {
                    $headers = @{ 'User-Agent' = 'VPNRouter-update-probe' }
                    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/PavelLizunov/VPNRouter/releases?per_page=30' -Headers $headers -TimeoutSec 30
                    $tag = "v$version"
                    $release = @($releases | Where-Object { $_.tag_name -eq $tag -and -not $_.draft }) | Select-Object -First 1
                    $asset = @($release.assets | Where-Object { $_.name -eq "VPNRouter-v$version-win.zip" }) | Select-Object -First 1
                    $latestLog = Get-ChildItem 'C:\ProgramData\VPNRouter\logs' -Filter 'vpnrouter*.log' -File -ErrorAction SilentlyContinue |
                        Sort-Object LastWriteTime -Descending | Select-Object -First 1
                    $manualFailed = $false
                    if ($latestLog) {
                        $manualFailed = [bool](Get-Content $latestLog.FullName -Tail 5000 |
                            Select-String -SimpleMatch '[UpdateVm] Manual check failed' -Quiet)
                    }
                    @{ ApiReachable = $true; ReleaseVisible = ($null -ne $release); WindowsAssetVisible = ($null -ne $asset); ManualCheckFailed = $manualFailed }
                }
                catch {
                    @{ ApiReachable = $false; ReleaseVisible = $false; WindowsAssetVisible = $false; ManualCheckFailed = $false }
                }
            }
            $safe = [ordered]@{
                ApiReachable = [bool]$probe.ApiReachable
                ReleaseVisible = [bool]$probe.ReleaseVisible
                WindowsAssetVisible = [bool]$probe.WindowsAssetVisible
                ManualCheckFailed = [bool]$probe.ManualCheckFailed
            }
            Write-Host ($safe | ConvertTo-Json -Compress)
            if (-not ($safe.ApiReachable -and $safe.ReleaseVisible -and $safe.WindowsAssetVisible)) { exit 1 }
        }
        finally { Remove-PSSession $s }
    }

    'liveupdate' {
        if (-not $Version -or $Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$') {
            throw "liveupdate requires a stable or rolling target such as -Version 2.48.0 or 2.48.0-r9."
        }
        $s = New-VerifiedBratSession
        try {
            $interactiveUser = (Import-Clixml $CredFile).UserName
            $result = Invoke-Command -Session $s -ArgumentList $Version, $interactiveUser -ScriptBlock {
                param($version, $interactiveUser)
                $appDir = 'C:\Program Files\VPNRouter\app'
                $cli = Join-Path $appDir 'VPNRouter.CLI.exe'
                $gui = Join-Path $appDir 'VPNRouter.GUI.exe'
                $dataDir = 'C:\ProgramData\VPNRouter'
                $log = Join-Path $dataDir 'logs\update.log'
                $stderr = Join-Path $dataDir 'logs\helper-stderr.log'
                $receipt = Join-Path $dataDir '.update-installed-version'
                if (-not (Test-Path -LiteralPath $cli) -or -not (Test-Path -LiteralPath $gui)) {
                    return @{ Status = 'BLOCKED'; Lifecycle = 'InstalledCliMissing' }
                }

                Get-Process -Name VPNRouter.App, VPNRouter.GUI, VPNRouter.CLI, sing-box -ErrorAction SilentlyContinue |
                    Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
                if (Get-Process -Name VPNRouter.App, VPNRouter.GUI, VPNRouter.CLI -ErrorAction SilentlyContinue) {
                    return @{ Status = 'BLOCKED'; Lifecycle = 'AppDidNotStop' }
                }

                New-Item -ItemType Directory -Path (Split-Path $log -Parent) -Force | Out-Null
                Remove-Item -LiteralPath $log, $stderr, $receipt -Force -ErrorAction SilentlyContinue

                $psi = [System.Diagnostics.ProcessStartInfo]::new()
                $psi.FileName = $cli
                $psi.Arguments = "test-update --target $version"
                $psi.UseShellExecute = $false
                $psi.CreateNoWindow = $true
                $psi.EnvironmentVariables['VPNROUTER_CI'] = '1'
                $cliProcess = [System.Diagnostics.Process]::Start($psi)
                if (-not $cliProcess.WaitForExit(300000)) {
                    try { $cliProcess.Kill() } catch { }
                    return @{ Status = 'FAIL'; Lifecycle = 'CliTimeout' }
                }
                if ($cliProcess.ExitCode -ne 0) {
                    return @{ Status = 'FAIL'; Lifecycle = 'DownloadOrDispatchFailed' }
                }

                $deadline = [DateTime]::UtcNow.AddMinutes(5)
                $helperDone = $false
                $parserFailure = $false
                while ([DateTime]::UtcNow -lt $deadline) {
                    $combined = ''
                    foreach ($path in @($log, $stderr)) {
                        if (Test-Path -LiteralPath $path) {
                            $combined += [Environment]::NewLine + (Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue)
                        }
                    }
                    if ($combined -match 'was unexpected at this time|syntax of the command is incorrect') {
                        $parserFailure = $true
                        break
                    }
                    if ($combined -match 'helper done') {
                        $helperDone = $true
                        break
                    }
                    Start-Sleep -Seconds 2
                }

                $content = if (Test-Path -LiteralPath $log) {
                    Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue
                } else { '' }
                $xcopyExitZero = $content -match 'xcopy exit=0(?:\D|$)'
                $copiedCountSane = $false
                if ($content -match '(\d+) File\(s\) copied') {
                    $copiedCountSane = [int]$Matches[1] -ge 50
                }
                $receiptPresent = Test-Path -LiteralPath $receipt
                $failedMarkerAbsent = -not (Test-Path -LiteralPath (Join-Path $appDir '.update-failed'))

                if ($parserFailure -or -not ($helperDone -and $xcopyExitZero -and $copiedCountSane -and $receiptPresent -and $failedMarkerAbsent)) {
                    return @{
                        Status = 'FAIL'; Lifecycle = if ($parserFailure) { 'HelperParserFailure' } else { 'HelperApplyFailed' }
                        HelperDone = $helperDone; XcopyExitZero = $xcopyExitZero; CopiedCountSane = $copiedCountSane
                        ReceiptPresent = $receiptPresent; FailedMarkerAbsent = $failedMarkerAbsent; AppStarted = $false
                    }
                }

                $taskName = 'VPNRouterLiveUpdateLaunch-' + [Guid]::NewGuid().ToString('N')
                $action = New-ScheduledTaskAction -Execute $gui
                $principal = New-ScheduledTaskPrincipal -UserId $interactiveUser -LogonType Interactive -RunLevel Highest
                Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
                try {
                    Start-ScheduledTask -TaskName $taskName
                    $launchDeadline = [DateTime]::UtcNow.AddSeconds(45)
                    do {
                        Start-Sleep -Seconds 1
                        $appStarted = $null -ne (Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1)
                    } while (-not $appStarted -and [DateTime]::UtcNow -lt $launchDeadline)
                }
                finally {
                    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
                }

                @{
                    Status = if ($appStarted) { 'PASS' } else { 'FAIL' }
                    Lifecycle = if ($appStarted) { 'Completed' } else { 'RelaunchFailed' }
                    HelperDone = $helperDone; XcopyExitZero = $xcopyExitZero; CopiedCountSane = $copiedCountSane
                    ReceiptPresent = $receiptPresent; FailedMarkerAbsent = $failedMarkerAbsent; AppStarted = $appStarted
                }
            }

            $allowedStatus = @('PASS', 'FAIL', 'BLOCKED')
            $allowedLifecycle = @('Completed', 'InstalledCliMissing', 'AppDidNotStop', 'CliTimeout',
                'DownloadOrDispatchFailed', 'HelperParserFailure', 'HelperApplyFailed', 'RelaunchFailed')
            if ($result.Status -notin $allowedStatus -or $result.Lifecycle -notin $allowedLifecycle) {
                throw 'WINBRAT live-update returned an invalid status schema.'
            }
            $safe = [ordered]@{
                Status = [string]$result.Status
                Lifecycle = [string]$result.Lifecycle
                HelperDone = $result.HelperDone -is [bool] -and $result.HelperDone
                XcopyExitZero = $result.XcopyExitZero -is [bool] -and $result.XcopyExitZero
                CopiedCountSane = $result.CopiedCountSane -is [bool] -and $result.CopiedCountSane
                ReceiptPresent = $result.ReceiptPresent -is [bool] -and $result.ReceiptPresent
                FailedMarkerAbsent = $result.FailedMarkerAbsent -is [bool] -and $result.FailedMarkerAbsent
                AppStarted = $result.AppStarted -is [bool] -and $result.AppStarted
            }
            Write-Host ($safe | ConvertTo-Json -Compress)
            if ($safe.Status -ne 'PASS') { exit 1 }
        }
        finally { Remove-PSSession $s }
    }
}
