$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
. (Join-Path $repo 'packaging/windows/installer/repair-environment.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
$system = [IO.Path]::Combine($fixture, 'system')
$envBefore = [Environment]::GetEnvironmentVariables()
try {
    $null = [IO.Directory]::CreateDirectory($fixture)
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        # An ordinary pre-existing directory is never adopted as privileged scratch.
        $ordinary = [IO.Directory]::CreateDirectory((Join-Path $fixture '.repair-temp'))
        $blocked = $false
        try { Get-RepairScratch $fixture | Out-Null } catch { $blocked = $true }
        if (!$blocked) { throw 'The inherited test ACL was accepted.' }
        $ordinary.Delete()
        if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
            $first = Get-RepairScratch $fixture
            if ((Get-RepairScratch $fixture) -ne $first) { throw 'Could not reuse protected scratch.' }
        }
    }
    function Get-RepairScratch([string]$applicationDir) { return [IO.Path]::Combine($applicationDir, '.repair-temp') }
    $env:VPNROUTER_TEST_INHERITED = 'fixture'
    $env:PSModulePath = $env:PATH = 'untrusted-fixture'
    Set-RepairEnvironment $fixture $system
    if ($env:VPNROUTER_TEST_INHERITED -or $env:PATH.Contains('untrusted-fixture') -or
        !$env:PATH.StartsWith($system) -or !$env:PSModulePath.StartsWith($system) -or
        $env:ComSpec -ne [IO.Path]::Combine($system, 'cmd.exe') -or
        $env:TEMP -ne (Join-Path $fixture '.repair-temp') -or $env:TEMP -ne $env:TMP) {
        throw 'Repair child did not receive protected paths and a clean environment.'
    }
} finally {
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) { [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process') }
    foreach ($key in $envBefore.Keys) { [Environment]::SetEnvironmentVariable($key, $envBefore[$key], 'Process') }
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture, $true) }
}
'Repair child environment passed.'
