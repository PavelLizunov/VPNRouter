# Call only after verifying the installation directory and its contents.
function Get-RepairScratch([string]$applicationDir) {
    $scratch = [IO.DirectoryInfo]::new([IO.Path]::Combine($applicationDir, '.repair-temp'))
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;RC;;;OW)')
    if (!$scratch.Exists) {
        if ($PSVersionTable.PSEdition -eq 'Core') { [IO.FileSystemAclExtensions]::Create($scratch, $acl) }
        else { $scratch.Create($acl) }
    }
    $scratch.Refresh()
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    if (($scratch.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-Acl -LiteralPath $scratch.FullName).GetSecurityDescriptorSddlForm($sections) -cne $acl.GetSecurityDescriptorSddlForm($sections)) {
        throw 'Could not verify the administrator-protected repair directory.'
    }
    return $scratch.FullName
}

function Set-RepairEnvironment([string]$applicationDir, [string]$system = [Environment]::SystemDirectory) {
    $scratch = Get-RepairScratch $applicationDir
    $windows = [IO.Directory]::GetParent($system).FullName
    $shell = [IO.Path]::Combine($system, 'WindowsPowerShell\v1.0')
    $environment = @{
        SystemRoot = $windows; WINDIR = $windows; OS = 'Windows_NT'
        SystemDrive = [IO.Path]::GetPathRoot($system).TrimEnd('\')
        ComSpec = [IO.Path]::Combine($system, 'cmd.exe'); PATHEXT = '.COM;.EXE;.BAT;.CMD'
        PATH = "$system;$windows;$system\Wbem;$shell"; PSModulePath = [IO.Path]::Combine($shell, 'Modules')
        TEMP = $scratch; TMP = $scratch
        USERPROFILE = [Environment]::GetFolderPath('UserProfile')
        APPDATA = [Environment]::GetFolderPath('ApplicationData')
        LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
        ProgramData = [Environment]::GetFolderPath('CommonApplicationData')
        ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
        'ProgramFiles(x86)' = [Environment]::GetFolderPath('ProgramFilesX86')
    }
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) {
        [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process')
    }
    foreach ($key in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
    }
}
