#requires -Version 5.1
<# FINAL task stage; PARENT REVIEW REQUIRED BEFORE EXECUTION.
Run only on WINBRAT via the explicitly authorized SSH channel / process Bypass.
No release, download, service, GUI trampoline, network start, or automatic recovery
following a launch attempt. Both protected backup and old app sibling are retained.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installed = 'C:\Program Files\VPNRouter\app'
$stage = 'C:\Program Files\VPNRouter\.app-stage-pictograms-20260907-392ffe3d184440f7a4558dfb28cad913'
$old = 'C:\Program Files\VPNRouter\.app-before-pictograms-20260907-392ffe3d184440f7a4558dfb28cad913'
$backup = 'C:\ProgramData\VPNRouter-pictograms-20260907-backup-392ffe3d184440f7a4558dfb28cad913'
$config = 'C:\ProgramData\VPNRouter\config.yaml'
$prepared = "$backup\config.preview.yaml"
$attempt = [Guid]::NewGuid().ToString('N')
$verify = "$backup\config.activation-verify-$attempt.yaml"
$temp = "C:\ProgramData\VPNRouter\.config-pictograms-20260907-$attempt.tmp"
$restore = "C:\ProgramData\VPNRouter\.config-restore-pictograms-20260907-$attempt.tmp"
$replaceBackup = "$backup\config.replaced-original-$attempt.yaml"
$rollbackBackup = "$backup\config.replaced-preview-$attempt.yaml"
$dotnet = 'C:\dotnet\dotnet.exe'
$helperRoot = 'C:\Temp\vpnrouter-pictograms-20260907\plans\preview-settings-helper\bin\Debug\net10.0'
$helper = "$helperRoot\PreviewSettingsHelper.dll"
$revision = 'e6b85a1a2012d3fae285d00e7d489eea1dfd384a'
$app = "$installed\VPNRouter.App.exe"
$taskName = 'VPNRouter-PictogramPreview-' + [Guid]::NewGuid().ToString('N')
$taskRegistered = $false
$oldMoved = $false
$stageMoved = $false
$configChanged = $false
$launchAttempted = $false
$success = $false
$rollback = 'not-needed'
$phase = 'preflight'
$appPid = 0

function Assert-NoReparse([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse path refused.' }
        $parent = [IO.Path]::GetDirectoryName($item.FullName)
        if (-not $parent -or $parent -eq $item.FullName) { break }
        $item = Get-Item -LiteralPath $parent -Force
    }
}
function Get-Manifest([string]$Root) {
    Assert-NoReparse $Root
    $map = @{}
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse tree entry refused.' }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            $map[$item.FullName.Substring($Root.Length + 1)] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    return $map
}
function Assert-Manifest($Actual, $Expected) {
    if ($Actual.Count -ne $Expected.Count) { throw 'File manifest count mismatch.' }
    foreach ($key in $Expected.Keys) {
        if (-not $Actual.ContainsKey($key) -or $Actual[$key] -cne $Expected[$key]) { throw 'File manifest checksum mismatch.' }
    }
}
function Convert-Manifest($Object) {
    $map = @{}
    foreach ($p in $Object.PSObject.Properties) {
        if ($p.Value -notmatch '^[0-9A-F]{64}$' -or $map.ContainsKey($p.Name)) { throw 'Invalid receipt manifest.' }
        $map[$p.Name] = [string]$p.Value
    }
    if (-not $map.Count) { throw 'Empty receipt manifest.' }
    return $map
}
function Assert-Identity {
    if ([Environment]::MachineName -cne 'WINBRAT') { throw 'Fixed WINBRAT identity required.' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevation required.' }
}
function Get-Conflicts {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(?i:VPNRouter.*|sing-box|winws|winws2|zapret.*|tgwsproxy.*|tgproxy.*|ciadpi|goodbyedpi|tpws)\.exe$' -or
        ([string]$_.ExecutablePath).StartsWith('C:\Program Files\VPNRouter\', [StringComparison]::OrdinalIgnoreCase) -or
        ([string]$_.ExecutablePath).StartsWith('C:\ProgramData\VPNRouter\', [StringComparison]::OrdinalIgnoreCase)
    })
}
function Assert-Idle([int]$AllowedPid = 0) {
    Assert-Identity
    if (@(Get-CimInstance Win32_Service | Where-Object { $_.Name -like '*VPNRouter*' -or $_.PathName -match '(?i)VPNRouter' }).Count) {
        throw 'VPNRouter service exists, including stopped service.'
    }
    if (@(Get-Conflicts | Where-Object { $AllowedPid -eq 0 -or $_.ProcessId -ne $AllowedPid }).Count) {
        throw 'Conflicting app/core/tool exists.'
    }
}
function Assert-Console {
    if ([PreviewConsole]::WTSGetActiveConsoleSessionId() -ne 1) { throw 'Console session 1 required.' }
    $buffer = [IntPtr]::Zero
    $bytes = 0
    try {
        if (-not [PreviewConsole]::WTSQuerySessionInformation([IntPtr]::Zero, 1, 8, [ref]$buffer, [ref]$bytes) -or
            $bytes -ne 4 -or [Runtime.InteropServices.Marshal]::ReadInt32($buffer) -ne 0) { throw 'Console is not active.' }
    } finally { if ($buffer -ne [IntPtr]::Zero) { [PreviewConsole]::WTSFreeMemory($buffer) } }
    $explorers = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { $_.SessionId -eq 1 })
    if ($explorers.Count -ne 1 -or $explorers[0].ExecutablePath -ine 'C:\Windows\explorer.exe') { throw 'Unique console explorer required.' }
    $owner = Invoke-CimMethod -InputObject $explorers[0] -MethodName GetOwnerSid
    if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne $testerSid.Value) { throw 'Console tester owner required.' }
}
function Assert-Hash([string]$Path, [string]$Hash) {
    Assert-NoReparse $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Hash) { throw 'Checksum mismatch.' }
}
function Copy-ConfigTemp([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw 'Config temporary path already exists.' }
    Assert-NoReparse ([IO.Path]::GetDirectoryName($Destination))
    # Create with the original descriptor BEFORE any secret-bearing bytes are copied.
    $stream = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew,
        [Security.AccessControl.FileSystemRights]::Write, [IO.FileShare]::None, 4096,
        [IO.FileOptions]::WriteThrough, $configAcl)
    try {
        $input = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $input.CopyTo($stream); $stream.Flush($true) } finally { $input.Dispose() }
    } finally { $stream.Dispose() }
    Set-Acl -LiteralPath $Destination -AclObject $configAcl
}

try {
    Assert-Idle
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PreviewConsole {
 [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
 [DllImport("wtsapi32.dll", EntryPoint="WTSQuerySessionInformationW", SetLastError=true)]
 public static extern bool WTSQuerySessionInformation(IntPtr server, int session, int info, out IntPtr buffer, out int bytes);
 [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr buffer);
}
'@
    $testerSid = ([Security.Principal.NTAccount]::new('WINBRAT', 'tester')).Translate([Security.Principal.SecurityIdentifier])
    Assert-Console
    foreach ($path in @($installed, $stage, $backup, $config, $prepared, "$backup\config.yaml", "$backup\verification.json", $dotnet, $helper)) { Assert-NoReparse $path }
    foreach ($path in @($old, $verify, $temp, $restore, $replaceBackup, $rollbackBackup)) { if (Test-Path -LiteralPath $path) { throw 'Task output already exists.' } }
    $backupAcl = Get-Acl -LiteralPath $backup
    if (-not $backupAcl.AreAccessRulesProtected) { throw 'Unprotected backup refused.' }
    foreach ($rule in $backupAcl.Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -ne 'Allow' -or $sid -notin @('S-1-5-18', 'S-1-5-32-544', $testerSid.Value)) { throw 'Unexpected backup ACL.' }
        $testerAllowed = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize
        if ($sid -eq $testerSid.Value -and (([int]$rule.FileSystemRights -band (-bnot [int]$testerAllowed)) -ne 0)) { throw 'Excess tester backup rights refused.' }
    }
    $receipt = Get-Content -LiteralPath "$backup\verification.json" -Raw | ConvertFrom-Json
    $original = Convert-Manifest $receipt.Installed
    $expected = Convert-Manifest $receipt.Staged
    $originalHash = [string]$receipt.ConfigSHA256
    if ($originalHash -cnotmatch '^[0-9A-F]{64}$') { throw 'Invalid config receipt.' }
    Assert-Manifest (Get-Manifest $installed) $original
    Assert-Manifest (Get-Manifest $stage) $expected
    Assert-Hash $config $originalHash
    Assert-Hash "$backup\config.yaml" $originalHash
    $versions = @('VPNRouter.App.dll', 'VPNRouter.Core.dll', 'VPNRouter.CLI.dll', 'VPNRouter.Service.dll') | ForEach-Object {
        [Diagnostics.FileVersionInfo]::GetVersionInfo("$stage\$_").ProductVersion
    }
    if (@($versions | Select-Object -Unique).Count -ne 1 -or $versions[0] -notmatch ('\+' + $revision + '$')) { throw 'Managed product revision mismatch.' }
    $configAcl = Get-Acl -LiteralPath $config
    $configSddl = $configAcl.Sddl
    $preparedHash = (Get-FileHash -LiteralPath $prepared -Algorithm SHA256).Hash
    $helperManifest = Get-Manifest $helperRoot
    # Helper's only operation is source-preserving YAML transformation of three autostart flags.
    # Suppress ALL child output; receipt must never expose YAML or raw error messages.
    & $dotnet $helper "$backup\config.yaml" $verify *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Preview helper failed.' }
    Assert-Manifest (Get-Manifest $helperRoot) $helperManifest
    Assert-Hash $verify $preparedHash
    Assert-Hash $prepared $preparedHash
    Assert-Hash "$backup\config.yaml" $originalHash
    Copy-ConfigTemp $prepared $temp
    Assert-Hash $temp $preparedHash
    Assert-Idle
    Assert-Console
    Assert-Manifest (Get-Manifest $installed) $original
    Assert-Manifest (Get-Manifest $stage) $expected
    Assert-Hash $config $originalHash
    if ((Get-Acl -LiteralPath $config).Sddl -cne $configSddl) { throw 'Config ACL changed.' }
    $phase = 'swap'
    [IO.Directory]::Move($installed, $old)
    $oldMoved = $true
    [IO.Directory]::Move($stage, $installed)
    $stageMoved = $true
    # Windows PowerShell 5.1 may bind $null as an empty string: use a unique protected backup.
    [IO.File]::Replace($temp, $config, $replaceBackup)
    $configChanged = $true
    Assert-Hash $config $preparedHash
    if ((Get-Acl -LiteralPath $config).Sddl -cne $configSddl) { throw 'Config ACL not preserved.' }
    Assert-Manifest (Get-Manifest $installed) $expected
    Assert-Idle
    Assert-Console
    $phase = 'launch'
    $action = New-ScheduledTaskAction -Execute $app -WorkingDirectory $installed
    $principal = New-ScheduledTaskPrincipal -UserId $testerSid.Value -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    # No trigger, no arguments, no persisted autostart; registration must not replace another task.
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Principal $principal -Settings $settings | Out-Null
    $taskRegistered = $true
    Assert-Idle
    Assert-Console
    $launchTime = Get-Date
    # Set BEFORE Start: even a failed RPC may already have launched. Never auto-rollback after here.
    $launchAttempted = $true
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $found = @(Get-Conflicts)
        if ($found.Count -gt 1) { throw 'Unexpected postlaunch process.' }
        if ($found.Count -eq 1) {
            $p = $found[0]
            if ($p.Name -cne 'VPNRouter.App.exe' -or $p.ExecutablePath -ine $app -or $p.SessionId -ne 1 -or
                $p.CreationDate -lt $launchTime) { throw 'Unexpected app identity.' }
            $owner = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid
            if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne $testerSid.Value) { throw 'Unexpected app owner.' }
            $window = Get-Process -Id $p.ProcessId
            if ($window.MainWindowHandle -ne 0 -and $window.MainWindowTitle -ceq 'VPNRouter') {
                $appPid = [int]$p.ProcessId
                break
            }
        }
        Start-Sleep -Milliseconds 250
    } while ($deadline.Elapsed.TotalSeconds -lt 20)
    if (-not $appPid) { throw 'Expected generic window not observed within deadline.' }
    Assert-Console
    Assert-Idle $appPid
    $success = $true
    $phase = 'complete'
} catch {
    # Source line only: no exception message, YAML, path or child output.
    $failureLine = $_.InvocationInfo.ScriptLineNumber
    Write-Output ("ActivationFailureLine=" + $failureLine)
    # Deliberately never print $_: it may include config content, paths or child output.
    if (-not $launchAttempted -and ($oldMoved -or $configChanged)) {
        $rollbackFailed = $false
        if ($configChanged) {
            try {
                Assert-Idle
                Assert-Hash "$backup\config.yaml" $originalHash
                Copy-ConfigTemp "$backup\config.yaml" $restore
                Assert-Hash $restore $originalHash
                [IO.File]::Replace($restore, $config, $rollbackBackup)
                Set-Acl -LiteralPath $config -AclObject $configAcl
                if ((Get-Acl -LiteralPath $config).Sddl -cne $configSddl) { throw 'Rollback ACL mismatch.' }
                Assert-Hash $config $originalHash
            } catch { $rollbackFailed = $true }
        }
        # Config recovery failure must not suppress independently safe app recovery.
        try {
            Assert-Idle
            if ($stageMoved) { [IO.Directory]::Move($installed, $stage) }
            if ($oldMoved) { [IO.Directory]::Move($old, $installed) }
            Assert-Manifest (Get-Manifest $installed) $original
        } catch { $rollbackFailed = $true }
        if ($rollbackFailed) { $rollback = 'failed-manual-review-required' }
        else { $rollback = 'complete' }
    }
} finally {
    if ($taskRegistered) {
        try { Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false }
        catch { $success = $false; $phase = 'task-cleanup-failed' }
    }
    # Recheck immediately before return; do not stop an unexpected network process.
    try { Assert-Idle $appPid } catch { $success = $false; $phase = 'return-process-guard-failed' }
}
[pscustomobject]@{
    Success = $success; Phase = $phase; Machine = 'WINBRAT'; SourceRevision = $revision
    LaunchAttempted = $launchAttempted; AppPid = $appPid; GenericWindowVerified = ($appPid -ne 0)
    PrelaunchRollback = $rollback; BackupRetained = $true; TaskName = $taskName
    ManualReviewRequired = (-not $success)
} | ConvertTo-Json -Compress
if (-not $success) { exit 1 }
