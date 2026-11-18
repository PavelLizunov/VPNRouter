# Used by the elevated installer before files are replaced and before explicit data deletion.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('VPNRouter','VPNRouterBusiness')][string]$Edition,
    [Parameter(Mandatory)][ValidateSet('Prepare','PurgeRuntime','PurgeData')][string]$Operation
)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $Edition
$runtime = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) "$Edition Zapret Runtime"
$legacyBin = Join-Path $data 'zapret\bin'
$services = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Services')
$known = @('zapret', 'WinDivert', 'WinDivert14', 'WinDivert15')
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
$options = [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames

function Test-LegacyImage($name, $image, $type) {
    if (!$image -or $image -match '[\r\n]') { return $false }
    if ($name -eq 'zapret') {
        if ($type -ne 16) { return $false }
        $exe = Join-Path $legacyBin 'winws.exe'
        $quoted = '^"' + [regex]::Escape($exe) + '"(?:[ \t]|$)'
        return ($image -match $quoted -or ($exe -notmatch '\s' -and $image -match ('^' + [regex]::Escape($exe) + '(?:[ \t]|$)')))
    }
    if ($type -ne 1 -or $name -notin $known) { return $false }
    $driver = $image.Trim()
    if ($driver.Length -ge 2 -and $driver[0] -eq '"' -and $driver[-1] -eq '"') {
        $driver = $driver.Substring(1, $driver.Length - 2)
    }
    if ($driver.StartsWith('\??\') -or $driver.StartsWith('\\?\')) { $driver = $driver.Substring(4) }
    return ($driver -in @((Join-Path $legacyBin 'WinDivert64.sys'), (Join-Path $legacyBin 'WinDivert32.sys')))
}

function Test-ReferencesRoot($name, $image, $root) {
    if (!$image) { return $name -in $known }
    $value = [Environment]::ExpandEnvironmentVariables([string]$image).Replace('/', '\')
    if ($value.IndexOf(($root + '\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    if ($value -match '[~%]|(?:^|\\)\.\.(?:\\|$)') { return $true }
    if ($name -notin $known) { return $false }
    # Ambiguous short, relative, device and redirected paths cannot establish an independent owner.
    $value = $value -replace '^\\\?\?\\',''
    if ($value -match '^(?:"([A-Za-z]:\\[^"]+)"(?:\s|$)|([A-Za-z]:\\[^\s"]+)(?:\s|$))') {
        $exe = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        if ($name -ne 'zapret' -and $value -notmatch '"') { $exe = $value }
        if ($exe -match '[~%]|\.\.\\' -or ![IO.File]::Exists($exe)) { return $true }
        for ($entry = Get-Item -LiteralPath $exe -Force; $entry; $entry = Get-Item -LiteralPath $parent -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $true }
            $parent = [IO.Path]::GetDirectoryName($entry.FullName)
            if (!$parent) { break }
        }
        return $false
    }
    return $true
}

try {
    if (!$services) { throw 'Cannot inspect Windows service registrations.' }
    if ($Operation -eq 'Prepare') {
        Add-Type -AssemblyName System.ServiceProcess
        foreach ($name in $known) {
            $key = $services.OpenSubKey($name)
            if (!$key) { continue }
            $controller = $null
            try {
                $image = $key.GetValue('ImagePath', $null, $options)
                if (!(Test-LegacyImage $name $image $key.GetValue('Type'))) { continue }
                $controller = New-Object System.ServiceProcess.ServiceController($name)
                $null = $controller.ServiceHandle
                if ($key.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service registration changed.' }
                $start = $key.GetValue('Start')
                if ($start -notin @(0,1,2,3,4)) { throw 'Unreadable legacy service start mode.' }
                if ($name -ne 'zapret' -and $start -ge 3) { continue }
                if ($null -eq $key.GetValue('VPNRouterPreUpgradeState')) {
                    $active = $controller.Status -ne 'Stopped'
                    $saved = [int]$start
                    if ($active) { $saved += 16 }
                    $writeKey = $services.OpenSubKey($name, $true)
                    try {
                        if ($writeKey.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service owner changed.' }
                        $writeKey.SetValue('VPNRouterPreUpgradeState', $saved, [Microsoft.Win32.RegistryValueKind]::DWord)
                        $writeKey.Flush()
                    } finally { $writeKey.Dispose() }
                }
                if ($start -ne 4) {
                    if ($key.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service owner changed.' }
                    & $sc config $name 'start=' disabled | Out-Null
                    if ($LASTEXITCODE -ne 0 -or $key.GetValue('Start') -ne 4) { throw 'Could not disable verified legacy service.' }
                }
            } finally {
                if ($controller) { $controller.Dispose() }
                $key.Dispose()
            }
        }
        exit 0
    }
    $target = if ($Operation -eq 'PurgeRuntime') { $runtime } else { $data }
    if (![IO.Directory]::Exists($target)) { exit 0 }
    if ($Operation -eq 'PurgeRuntime') {
        $lockPath = $runtime + '.install.lock'
        if ([IO.File]::Exists($lockPath) -and ([IO.File]::GetAttributes($lockPath) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected package lock.' }
        $purgeLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    }
    foreach ($proc in Get-CimInstance Win32_Process -Filter "Name='winws.exe'") {
        if (!$proc.ExecutablePath -or $proc.ExecutablePath.StartsWith(($target + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Keeping $target while winws is running from an owned or unverifiable location."
        }
    }
    foreach ($name in $services.GetSubKeyNames()) {
        $key = $services.OpenSubKey($name)
        if (!$key) { throw 'A service registration could not be verified.' }
        try {
            if (Test-ReferencesRoot $name ($key.GetValue('ImagePath', $null, $options)) $target) {
                throw "Keeping $target while service $name may reference it."
            }
        } finally { $key.Dispose() }
    }
    $dirs = New-Object 'System.Collections.Generic.Stack[string]'
    $dirs.Push($target)
    while ($dirs.Count) {
        $dir = $dirs.Pop()
        if ([IO.File]::GetAttributes($dir) -band [IO.FileAttributes]::ReparsePoint) { throw 'Keeping redirected directory.' }
        foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($dir)) {
            $attr = [IO.File]::GetAttributes($child)
            if ($attr -band [IO.FileAttributes]::ReparsePoint) { throw 'Keeping directory containing a redirect.' }
            if ($attr -band [IO.FileAttributes]::Directory) { $dirs.Push($child) }
        }
    }
    [IO.Directory]::Delete($target, $true)
    exit 0
} finally {
    if ($purgeLock) { $purgeLock.Dispose() }
    if ($services) { $services.Dispose() }
}
