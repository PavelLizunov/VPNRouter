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
$ninaBin = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NINA\engine'
$services = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Services')
$known = @('zapret', 'WinDivert', 'WinDivert14', 'WinDivert15')
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
$options = [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames

function Get-DriverImage([string]$image) {
    if (!$image -or $image -match '[\x00\r\n]') { return }
    $driver = $image.Trim()
    if ($driver.Length -ge 2 -and $driver[0] -eq '"' -and $driver[-1] -eq '"') {
        $driver = $driver.Substring(1, $driver.Length - 2)
    }
    if ($driver.StartsWith('\??\') -or $driver.StartsWith('\\?\')) { $driver = $driver.Substring(4) }
    return $driver
}

function Test-LegacyImage($name, $image, $type, $bin = $legacyBin) {
    if (!$image -or $image -match '[\x00\r\n]') { return $false }
    if ($name -eq 'zapret') {
        if ($type -ne 16) { return $false }
        $exe = Join-Path $bin 'winws.exe'
        $quoted = '^"' + [regex]::Escape($exe) + '"(?:[ \t]|$)'
        return ($image -match $quoted -or ($exe -notmatch '\s' -and $image -match ('^' + [regex]::Escape($exe) + '(?:[ \t]|$)')))
    }
    if ($type -ne 1 -or $name -notin $known) { return $false }
    return ((Get-DriverImage $image) -in @((Join-Path $bin 'WinDivert64.sys'), (Join-Path $bin 'WinDivert32.sys')))
}

function Test-NinaDriver($reg, $protectedBin) {
    if ($Edition -ne 'VPNRouter' -or $reg.Name -ne 'WinDivert' -or $reg.Type -ne 1) { return $false }
    $nina = Join-Path $ninaBin 'WinDivert64.sys'
    if ((Get-DriverImage $reg.Image) -ne $nina) { return $false }
    $installed = Join-Path $protectedBin 'WinDivert64.sys'
    foreach ($path in @($nina, $installed)) {
        if (![IO.File]::Exists($path) -or ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)) { return $false }
    }
    $source = $target = $null
    try {
        $source = [IO.File]::Open($nina, 'Open', 'Read', 'Read')
        $target = [IO.File]::Open($installed, 'Open', 'Read', 'Read')
        return ($source.Length -gt 0 -and $source.Length -le 1048576 -and $source.Length -eq $target.Length -and
            (Get-FileHash -InputStream $source).Hash -eq (Get-FileHash -InputStream $target).Hash)
    } finally {
        if ($target) { $target.Dispose() }
        if ($source) { $source.Dispose() }
    }
}

function Assert-PrepareOwnership($registrations, $processes) {
    $protectedBin = Join-Path $runtime 'zapret\bin'
    foreach ($reg in $registrations) {
        if (!(Test-LegacyImage $reg.Name $reg.Image $reg.Type) -and
            !(Test-LegacyImage $reg.Name $reg.Image $reg.Type $protectedBin) -and
            !(Test-NinaDriver $reg $protectedBin)) {
            throw "Zapret migration blocked by service $($reg.Name). Resolve its ownership before upgrading."
        }
    }
    $images = @((Join-Path $legacyBin 'winws.exe'), (Join-Path $protectedBin 'winws.exe'))
    foreach ($process in $processes) {
        if (!$process.ExecutablePath -or $process.ExecutablePath -notin $images) {
            throw 'Zapret migration blocked by an unrelated or unverifiable winws process.'
        }
    }
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

function Invoke-Prepare {
    $registrations = @(foreach ($name in $known) {
        $key = $services.OpenSubKey($name)
        if ($key) {
            try {
                [pscustomobject]@{ Name = $name; Type = $key.GetValue('Type'); Image = $key.GetValue('ImagePath', $null, $options) }
            } finally { $key.Dispose() }
        }
    })
    if (!($registrations | Where-Object { Test-LegacyImage $_.Name $_.Image $_.Type })) { return }
    if ($Edition -eq 'VPNRouter' -and (Get-CimInstance Win32_Process -Filter "Name='winws2.exe'" -ErrorAction Stop)) {
        throw 'Zapret migration blocked by a running winws2 process.'
    }
    Assert-PrepareOwnership $registrations @(Get-CimInstance Win32_Process -Filter "Name='winws.exe'" -ErrorAction Stop)
    Add-Type -AssemblyName System.ServiceProcess
    foreach ($candidate in $registrations) {
        if (!(Test-LegacyImage $candidate.Name $candidate.Image $candidate.Type)) { continue }
        $name = $candidate.Name
        $image = $candidate.Image
        $key = $services.OpenSubKey($name)
        if (!$key) { throw 'Legacy service registration changed.' }
        $controller = $null
        try {
            if ($key.GetValue('Type') -ne $candidate.Type -or $key.GetValue('ImagePath', $null, $options) -ne $image) {
                throw 'Legacy service registration changed.'
            }
            $controller = New-Object System.ServiceProcess.ServiceController($name)
            $null = $controller.ServiceHandle
            if ($key.GetValue('Type') -ne $candidate.Type -or $key.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service registration changed.' }
            $start = $key.GetValue('Start')
            if ($start -notin @(0,1,2,3,4)) { throw 'Unreadable legacy service start mode.' }
            if ($name -ne 'zapret' -and $start -ge 3) { continue }
            if ($null -eq $key.GetValue('VPNRouterPreUpgradeState')) {
                $active = $controller.Status -ne 'Stopped'
                $saved = [int]$start
                if ($active) { $saved += 16 }
                $writeKey = $services.OpenSubKey($name, $true)
                try {
                    if ($writeKey.GetValue('Type') -ne $candidate.Type -or $writeKey.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service owner changed.' }
                    $writeKey.SetValue('VPNRouterPreUpgradeState', $saved, [Microsoft.Win32.RegistryValueKind]::DWord)
                    $writeKey.Flush()
                } finally { $writeKey.Dispose() }
            }
            if ($start -ne 4) {
                if ($key.GetValue('Type') -ne $candidate.Type -or $key.GetValue('ImagePath', $null, $options) -ne $image) { throw 'Legacy service owner changed.' }
                & $sc config $name 'start=' disabled | Out-Null
                if ($LASTEXITCODE -ne 0 -or $key.GetValue('Start') -ne 4) { throw 'Could not disable verified legacy service.' }
            }
        } finally {
            if ($controller) { $controller.Dispose() }
            $key.Dispose()
        }
    }
}

try {
    if (!$services) { throw 'Cannot inspect Windows service registrations.' }
    if ($Operation -eq 'Prepare') {
        Invoke-Prepare
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
