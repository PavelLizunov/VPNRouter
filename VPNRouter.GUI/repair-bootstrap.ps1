$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
$system = [Environment]::SystemDirectory
$windows = [IO.Directory]::GetParent($system).FullName
$systemShell = [IO.Path]::Combine($system, 'WindowsPowerShell\v1.0\powershell.exe')
$env:SystemRoot = $env:WINDIR = $windows
$env:SystemDrive = [IO.Path]::GetPathRoot($windows).TrimEnd('\')
$env:PATH = "$system;$windows;$system\Wbem;$PSHOME"
$env:PATHEXT = '.COM;.EXE;.BAT;.CMD'
$env:ComSpec = [IO.Path]::Combine($system, 'cmd.exe')
function Set-RepairKnownFolders {
    $env:USERPROFILE = [Environment]::GetFolderPath('UserProfile')
    $env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
    $env:APPDATA = [Environment]::GetFolderPath('ApplicationData')
    $env:ProgramData = [Environment]::GetFolderPath('CommonApplicationData')
    $env:ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
    ${env:ProgramFiles(x86)} = [Environment]::GetFolderPath('ProgramFilesX86')
}
Set-RepairKnownFolders
Set-Location -LiteralPath $system

function Invoke-AutomaticRepair([string]$applicationDir, [bool]$prerelease, [string]$work, [string]$shell) {
    $env:TEMP = $env:TMP = $work
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $installer = Join-Path $work 'install.ps1'
    Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -MaximumRedirection 0 -UseBasicParsing -OutFile $installer
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Repair', '-RepairApplicationDir', $applicationDir)
    if ($prerelease) { $arguments += '-Prerelease' }
    & $shell @arguments
    if ($LASTEXITCODE -ne 0) { throw "The repair installer exited with code $LASTEXITCODE." }
}

function Invoke-ElevatedRepair([string]$shell, [string]$systemDir, [string[]]$invocation) {
    # The original encoded command is the last argument produced by both launchers.
    if ($invocation.Length -ne 7 -or $invocation[5] -ne '-EncodedCommand' -or $invocation[6] -notmatch '^[A-Za-z0-9+/]+={0,2}$') {
        throw 'Invalid repair invocation.'
    }
    $child = Start-Process -FilePath $shell -Verb RunAs -WorkingDirectory $systemDir -WindowStyle Hidden -PassThru -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $($invocation[6])"
    try {
        # Wait for the installer itself. Its relaunched desktop app may remain open indefinitely.
        $child.WaitForExit()
        return $child.ExitCode
    } finally { $child.Dispose() }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    exit (Invoke-ElevatedRepair $systemShell $system ([Environment]::GetCommandLineArgs()))
}
$appDir = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__APPLICATION_DIR__')).TrimEnd('\')
$prerelease = __PRERELEASE__
$programFiles = [IO.DirectoryInfo]::new([Environment]::GetFolderPath('ProgramFiles'))
if ($programFiles.FullName -notmatch '^[A-Za-z]:\\') { throw 'Cannot resolve protected Program Files.' }
$trusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
for ($dir = $programFiles; $dir; $dir = $dir.Parent) {
    if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Repair staging is redirected.' }
    $acl = $dir.GetAccessControl()
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Repair staging parent has an untrusted owner.' }
    $disallowed = [Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, TakeOwnership, ChangePermissions'
    if ($dir.FullName -eq $programFiles.FullName) { $disallowed = $disallowed -bor [Security.AccessControl.FileSystemRights]::Write }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $disallowed)) {
            throw 'Repair staging parent grants unsafe access.'
        }
    }
}
$work = Join-Path $programFiles.FullName ('VPNRouter-Repair-' + [Guid]::NewGuid().ToString('N'))
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;RC;;;OW)')
$stage = New-Object IO.DirectoryInfo($work)
if ($stage.Exists -or [IO.File]::Exists($work)) { throw 'Repair staging already exists.' }
$stage.Create($acl)
$stage.Refresh()
$actual = $stage.GetAccessControl()
function Get-RepairAclRules($security) {
    $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
        "$($_.IdentityReference.Value)/$($_.AccessControlType)/$([int]$_.FileSystemRights)/$($_.InheritanceFlags)/$($_.PropagationFlags)/$($_.IsInherited)"
    } | Sort-Object
}
if (($stage.Attributes -band [IO.FileAttributes]::ReparsePoint) -or !$actual.AreAccessRulesProtected -or
    $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
    ((Get-RepairAclRules $actual) -join ',') -ne ((Get-RepairAclRules $acl) -join ',')) {
    throw 'Cannot verify protected repair staging.'
}
try {
    Invoke-AutomaticRepair $appDir $prerelease $work $systemShell
} finally {
    [IO.Directory]::Delete($work, $true)
}
