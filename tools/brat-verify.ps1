# Safety: fixed target only. Never accepts a caller-supplied host/IP/hostname
# and never falls back to the local machine; every action first verifies over
# WinRM that 100.115.182.0 really is WINBRAT, and fails closed on any mismatch.
#
# Interactive UI work (uia/screenshot) never touches local process/input/screen
# APIs from this dev box: a helper script is shipped to the verified brat box
# and run there in the interactive console session via a unique transient
# scheduled task. Moving the logged-on session onto the physical console
# (tscon) also runs through a unique transient SYSTEM scheduled task, so no
# password is ever needed. All remote transient files live under
# C:\r4review\verify and are removed again in a finally block.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('identity', 'deploy', 'uia', 'screenshot', 'logs')]
    [string]$Action,

    [string]$Version,

    # UIA element selectors (uia): AutomationId and/or Name required.
    [string]$AutomationId,
    [string]$Name,
    [string]$ControlType,

    [ValidateSet('Inspect', 'Invoke', 'Toggle', 'Expand', 'SetValue')]
    [string]$UiaOperation = 'Inspect',
    [string]$Value,

    # screenshot local destination (must stay inside the checkout root).
    [string]$LocalOutput,

    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
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
        [Parameter(Mandatory = $true)] [ValidateSet('uia', 'screenshot')] [string]$Mode,
        [string]$AutomationId,
        [string]$Name,
        [string]$ControlType,
        [ValidateSet('Inspect', 'Invoke', 'Toggle', 'Expand', 'SetValue')]
        [string]$UiaOperation = 'Inspect',
        [string]$Value,
        [string]$LocalOutput,
        [int]$TimeoutSeconds = 30
    )

    # The only place UIA / screen-capture code exists. Shipped to brat and run
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
    $req = Get-Content -Path $RequestPath -Raw | ConvertFrom-Json
    $deadline = (Get-Date).AddSeconds([int]$req.TimeoutSeconds)

    if ($req.Mode -eq 'uia') {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $ae = [System.Windows.Automation.AutomationElement]

        # Prefer the GUI host; only fall back to VPNRouter.App when no GUI
        # process exists. Never pick an arbitrary first result across both names.
        $proc = Get-Process -Name VPNRouter.GUI -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { $proc = Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1 }
        if (-not $proc) { throw "VPNRouter.GUI / VPNRouter.App process not found." }

        $root = $ae::RootElement
        $window = $null
        while (-not $window -and (Get-Date) -lt $deadline) {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $proc.Id)
            $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
            if (-not $window) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $window) { throw "No top-level window for PID $($proc.Id)." }

        $conds = @()
        if ($req.AutomationId) { $conds += New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, [string]$req.AutomationId) }
        if ($req.Name)         { $conds += New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, [string]$req.Name) }
        if ($req.ControlType) {
            $ctProp = [System.Windows.Automation.ControlType].GetField([string]$req.ControlType, [System.Reflection.BindingFlags]'Public,Static')
            if (-not $ctProp) { throw "Unknown ControlType '$($req.ControlType)'." }
            $conds += New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ctProp.GetValue($null))
        }
        $findCond = $conds[0]
        for ($i = 1; $i -lt $conds.Count; $i++) {
            $findCond = New-Object System.Windows.Automation.AndCondition $findCond, $conds[$i]
        }

        $target = $null
        while (-not $target -and (Get-Date) -lt $deadline) {
            $target = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $findCond)
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
            }
            'Invoke' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { throw "InvokePattern unsupported by matched element." }
                $pat.Invoke()
            }
            'Toggle' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { throw "TogglePattern unsupported by matched element." }
                $pat.Toggle()
            }
            'Expand' {
                $pat = $null
                if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pat)) { throw "ExpandCollapsePattern unsupported by matched element." }
                $pat.Expand()
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
    elseif ($req.Mode -eq 'screenshot') {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try { $g.CopyFromScreen($vs.X, $vs.Y, 0, 0, $bmp.Size) } finally { $g.Dispose() }
            $bmp.Save([string]$req.ScreenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bmp.Dispose() }
        $result.ScreenshotPath = [string]$req.ScreenshotPath
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
    $remotePng    = "$remoteDir\screenshot.png"
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
        ScreenshotPath = $remotePng
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

        if ($Mode -eq 'screenshot') {
            $localDir = Split-Path $LocalOutput -Parent
            if ($localDir -and -not (Test-Path $localDir)) { New-Item -ItemType Directory -Path $localDir -Force | Out-Null }
            Copy-Item -Path $remotePng -Destination $LocalOutput -FromSession $Session -Force
        }
        return $res
    }
    finally {
        try {
            Invoke-Command -Session $Session -ArgumentList $taskName, $remoteDir -ScriptBlock {
                param($tn, $dir)
                # Stop the transient task first; only unregister it and delete
                # its run directory once it is confirmed not Running. If it is
                # still Running after Stop-ScheduledTask plus 10 s of polling,
                # warn and leave both in place for manual cleanup.
                $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                if ($t -and $t.State -eq 'Running') {
                    Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                    $stopDeadline = (Get-Date).AddSeconds(10)
                    while ((Get-Date) -lt $stopDeadline) {
                        $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
                        if (-not $t -or $t.State -ne 'Running') { break }
                        Start-Sleep -Milliseconds 200
                    }
                    if ($t -and $t.State -eq 'Running') {
                        Write-Warning "Transient task '$tn' on $env:COMPUTERNAME is still Running after Stop-ScheduledTask + 10 s; leaving the task and '$dir' in place for manual cleanup."
                        return
                    }
                }
                Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction SilentlyContinue
                Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
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

        $s = New-VerifiedBratSession
        Remove-PSSession $s
        # Pass the already-resolved credential explicitly so the generic deploy
        # script never prompts or writes a credential cache named for the new IP.
        & (Join-Path $Root 'deploy-to-testpc.ps1') -TestHost $BratIp -Version $Version -Credential (Import-Clixml $CredFile)
        if ($LASTEXITCODE) { throw "deploy-to-testpc.ps1 failed (exit $LASTEXITCODE)." }
    }

    'uia' {
        if (-not ($AutomationId -or $Name)) { throw "uia requires -AutomationId and/or -Name." }
        if ($UiaOperation -eq 'SetValue' -and -not $PSBoundParameters.ContainsKey('Value')) { throw "-UiaOperation SetValue requires -Value." }
        $s = New-VerifiedBratSession
        try {
            $res = Invoke-BratInteractive -Session $s -Mode 'uia' -AutomationId $AutomationId -Name $Name -ControlType $ControlType -UiaOperation $UiaOperation -Value $Value -TimeoutSeconds $TimeoutSeconds
            if ($UiaOperation -eq 'Inspect') {
                Write-Host "Inspect result on $BratMachineName`:" -ForegroundColor Green
                Write-Host ($res.Element | ConvertTo-Json -Compress)
            } else {
                Write-Host "OK: $UiaOperation completed on $BratMachineName." -ForegroundColor Green
            }
        }
        finally { Remove-PSSession $s }
    }

    'screenshot' {
        if (-not $LocalOutput) { $LocalOutput = Join-Path $Root 'artifacts\brat-verify\screenshot.png' }
        $fullOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LocalOutput)
        $rootPrefix = (Resolve-Path $Root).Path.TrimEnd('\') + '\'
        if (-not $fullOutput.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "LocalOutput must be strictly inside the checkout root '$($rootPrefix.TrimEnd('\'))'; got '$fullOutput'."
        }
        $s = New-VerifiedBratSession
        try {
            Invoke-BratInteractive -Session $s -Mode 'screenshot' -LocalOutput $fullOutput -TimeoutSeconds $TimeoutSeconds | Out-Null
            Write-Host "Screenshot saved to $fullOutput" -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }

    'logs' {
        $s = New-VerifiedBratSession
        try {
            $scan = Invoke-Command -Session $s -ScriptBlock {
                $dir = 'C:\ProgramData\VPNRouter\logs'
                if (-not (Test-Path $dir)) { return @{ Found = $false; File = $null; Hits = @(); Note = "no log dir at $dir" } }
                $f = Get-ChildItem $dir -Filter 'vpnrouter*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if (-not $f) { return @{ Found = $false; File = $null; Hits = @(); Note = "no vpnrouter*.log in $dir" } }
                $hits = @(Get-Content $f.FullName -Tail 500 | Where-Object { $_ -match '\[ERR\]|Exception|FATAL' })
                @{ Found = ($hits.Count -gt 0); File = $f.Name; Hits = $hits; Note = $null }
            }
            if (-not $scan.File) {
                throw "Cannot verify remote logs on $BratMachineName`: $($scan.Note). Failing closed."
            }
            if ($scan.Found) {
                Write-Host "[!] $($scan.Hits.Count) error pattern(s) in remote $($scan.File):" -ForegroundColor Red
                $scan.Hits | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
                exit 1
            }
            Write-Host "CLEAN: no [ERR]/Exception/FATAL in last 500 lines of remote $($scan.File)." -ForegroundColor Green
        }
        finally { Remove-PSSession $s }
    }
}
