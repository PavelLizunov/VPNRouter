function Assert-RepairObject($entry, [bool]$ancestor) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Repair target is redirected.' }
    $acl = if ($PSVersionTable.PSEdition -eq 'Core') {
        [IO.FileSystemAclExtensions]::GetAccessControl($entry)
    } else { $entry.GetAccessControl() }
    $trusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $denied = [Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    if (!$ancestor) { $denied = $denied -bor [Security.AccessControl.FileSystemRights]::Write }
    $ownerLimited = $false
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        $sid = $rule.IdentityReference.Value
        $inheritOnly = $rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly
        if (!$inheritOnly -and $sid -eq 'S-1-3-4') { $ownerLimited = $true }
        if ($inheritOnly -and ($ancestor -or ($entry -is [IO.DirectoryInfo] -and $sid -eq 'S-1-3-0'))) { continue }
        if ($rule.AccessControlType -eq 'Allow' -and $sid -notin $trusted -and ($rule.FileSystemRights -band $denied)) {
            throw 'Repair requires an administrator-protected installation.'
        }
    }
    if (!$ownerLimited -and $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) {
        throw 'Repair target owner cannot be verified.'
    }
}

function Assert-RepairRoot([IO.DirectoryInfo]$dir) {
    $root = [IO.Path]::GetPathRoot($dir.FullName)
    if ($root -notmatch '^[A-Za-z]:\\$' -or [IO.DriveInfo]::new($root).DriveType -notin @('Fixed','Removable','Ram')) {
        throw 'Expected a protected local installation directory.'
    }
    for ($parent = $dir.Parent; $parent; $parent = $parent.Parent) { Assert-RepairObject $parent $true }
    Assert-RepairObject $dir $false
}

function Assert-RepairTree([IO.DirectoryInfo]$dir, [IO.FileStream]$heldLock) {
    $pending = [Collections.Generic.Stack[IO.FileSystemInfo]]::new()
    $pending.Push($dir)
    while ($pending.Count) {
        $item = $pending.Pop()
        # Open-RepairInstallLock already verified this exact file before opening it exclusively.
        if ($heldLock -and $item.FullName -eq $heldLock.Name) { continue }
        Assert-RepairObject $item $false
        if ($item -is [IO.DirectoryInfo]) {
            foreach ($child in $item.EnumerateFileSystemInfos()) { $pending.Push($child) }
        }
    }
}

function Open-RepairInstallLock([IO.DirectoryInfo]$application) {
    $root = if ($application.Name -eq 'app') { $application.Parent } else { $application }
    Assert-RepairRoot $root
    $path = [IO.FileInfo]::new([IO.Path]::Combine($root.FullName, '.update-backup.lock'))
    if ([int]$path.Attributes -ne -1) { Assert-RepairObject $path $false }
    return [IO.File]::Open($path.FullName, 'OpenOrCreate', 'ReadWrite', 'None')
}
