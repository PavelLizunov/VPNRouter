#requires -Version 5.1
<#
Task-specific WINBRAT phase 1 ONLY. Invoke locally on WINBRAT via the approved SSH channel.
Copies the installed app to a unique sibling stage, overlays the e6b85a1a App publish
and verified vpnctl.5 core, and backs up config bytes. NEVER swaps, edits config,
stops anything, registers tasks, launches binaries, or changes credentials/TrustedHosts.
Keep the receipt and protected backup for the separately reviewed final phase.
On failure, partial task-owned paths are deliberately retained for inspection; no success receipt.
#>
[CmdletBinding()]
param()
$CoreArchivePath = 'C:\Temp\vpnrouter-pictograms-20260907\sing-box-1.14.0-vpnctl.5-windows-amd64.zip'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = 'C:\Temp\vpnrouter-pictograms-20260907'
$installed = 'C:\Program Files\VPNRouter\app'
$published = "$taskRoot\preview-app-e6b85a1a"
$core = "$taskRoot\core-vpnctl5\sing-box-1.14.0-vpnctl.5-windows-amd64\sing-box.exe"
$config = 'C:\ProgramData\VPNRouter\config.yaml'
$expectedArchiveHash = '3823e4baed13fec43b84acefa480ff9cf9b2c222ea9dd9ceb9987aefd623aeb4'
$runId = [Guid]::NewGuid().ToString('N')
$stage = "C:\Program Files\VPNRouter\.app-stage-pictograms-20260907-$runId"
$backup = "C:\ProgramData\VPNRouter-pictograms-20260907-backup-$runId"

function Assert-Idle {
    if ([Environment]::MachineName -cne 'WINBRAT') { throw 'Fixed WINBRAT identity required.' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Elevated administrator token required.'
    }
    $services = @(Get-CimInstance Win32_Service | Where-Object {
        $_.Name -like '*VPNRouter*' -or $_.PathName -match '(?i)VPNRouter'
    })
    if ($services.Count) { throw 'A VPNRouter service exists; phase 1 refuses even a stopped service.' }
    $conflicts = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(?i:VPNRouter.*|sing-box|winws|winws2|zapret.*|tgwsproxy.*|tgproxy.*|ciadpi|goodbyedpi|tpws)\.exe$' -or
        ([string]$_.ExecutablePath).StartsWith('C:\Program Files\VPNRouter\', [StringComparison]::OrdinalIgnoreCase) -or
        ([string]$_.ExecutablePath).StartsWith('C:\ProgramData\VPNRouter\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($conflicts.Count) { throw 'Conflicting app/core/tool process exists; nothing will be stopped.' }
}
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
    # Walk one directory at a time: never traverse a reparse-point directory.
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse tree entry refused.' }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
            $relative = $item.FullName.Substring($Root.Length + 1)
            $map[$relative] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
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
function Copy-Tree([string]$Source, [string]$Destination) {
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

Assert-Idle
foreach ($path in @($installed, $published, $core, $config, 'C:\ProgramData', 'C:\Program Files\VPNRouter')) {
    Assert-NoReparse $path
}
$CoreArchivePath = [IO.Path]::GetFullPath($CoreArchivePath)
if (-not $CoreArchivePath.StartsWith("$taskRoot\", [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($CoreArchivePath) -ine '.zip') { throw 'Core archive must be a ZIP inside the exact taskroot.' }
Assert-NoReparse $CoreArchivePath
if ((Get-FileHash -LiteralPath $CoreArchivePath -Algorithm SHA256).Hash -ine $expectedArchiveHash) {
    throw 'Core archive checksum mismatch.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($CoreArchivePath)
try {
    $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'sing-box-1.14.0-vpnctl.5-windows-amd64/sing-box.exe' })
    if ($entries.Count -ne 1) { throw 'Expected unique core archive member missing.' }
    $stream = $entries[0].Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $archiveCoreHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
} finally { $zip.Dispose() }
if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -cne $archiveCoreHash) { throw 'Extracted core differs from verified archive member.' }
$original = Get-Manifest $installed
# Match build.ps1's canonical App -> CLI -> Service shared-runtime precedence.
$publishRoots = @($published, "$taskRoot\preview-VPNRouter.CLI-e6b85a1a", "$taskRoot\preview-VPNRouter.Service-e6b85a1a")
$publishMaps = @{}
$overlay = @{}
foreach ($root in $publishRoots) {
    $map = Get-Manifest $root
    $publishMaps[$root] = $map
    foreach ($key in $map.Keys) {
        if ($overlay.ContainsKey($key) -and $overlay[$key] -cne $map[$key]) {
            # App transitively uses Extensions 6; Service ships the newer compatible
            # assemblies, exactly as the repository's shared-output build does.
            $sharedRuntime = @('Microsoft.Extensions.DependencyInjection.Abstractions.dll',
                'Microsoft.Extensions.DependencyInjection.dll', 'Microsoft.Extensions.Logging.Abstractions.dll',
                'Microsoft.Extensions.Logging.dll', 'Microsoft.Extensions.Options.dll', 'Microsoft.Extensions.Primitives.dll')
            if ($key -notin $sharedRuntime) { throw 'Unexpected shared publish mismatch.' }
        }
        $overlay[$key] = $map[$key]
    }
}
foreach ($required in @('VPNRouter.App.exe', 'VPNRouter.App.dll', 'VPNRouter.App.deps.json', 'VPNRouter.App.runtimeconfig.json', 'coreclr.dll')) {
    if (-not $overlay.ContainsKey($required)) { throw 'Self-contained App publish is incomplete.' }
}
foreach ($required in @('VPNRouter.GUI.exe', 'VPNRouter.CLI.exe', 'VPNRouter.Service.exe', 'sing-box.exe')) {
    if (-not $original.ContainsKey($required)) { throw 'Expected existing full install binary missing.' }
}
foreach ($required in @('VPNRouter.Service.exe', 'VPNRouter.Service.dll', 'VPNRouter.CLI.exe', 'VPNRouter.CLI.dll')) {
    if (-not $overlay.ContainsKey($required)) { throw 'Matching Service/CLI publish is incomplete.' }
}
foreach ($key in $overlay.Keys) {
    if ($key -match '^(?i:VPNRouter\.GUI\.exe|sing-box\.exe)$') { throw 'Unexpected native GUI/core in managed publish.' }
}
$needed = (Get-ChildItem -LiteralPath $installed -File -Recurse -Force | Measure-Object Length -Sum).Sum +
    ($publishRoots | ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Recurse -Force } | Measure-Object Length -Sum).Sum + 512MB
if ((Get-PSDrive C).Free -lt $needed) { throw 'Insufficient free space for isolated staging.' }
$testerSid = ([Security.Principal.NTAccount]::new('WINBRAT', 'tester')).Translate([Security.Principal.SecurityIdentifier])
Assert-Idle
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup)) { throw 'Unique task output already exists.' }
# Apply a protected ACL atomically at directory creation, BEFORE secret bytes exist.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($testerSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
[IO.DirectoryInfo]::new($backup).Create($acl)
$actualAcl = Get-Acl -LiteralPath $backup
if (-not $actualAcl.AreAccessRulesProtected -or $actualAcl.Access.Count -ne 3) { throw 'Protected backup ACL verification failed.' }
# FileShare.Read blocks concurrent writers during byte-copy and hash verification.
$input = [IO.File]::Open($config, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $output = [IO.File]::Open("$backup\config.yaml", [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
    $input.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $configHash = [BitConverter]::ToString($sha.ComputeHash($input)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ((Get-FileHash -LiteralPath "$backup\config.yaml" -Algorithm SHA256).Hash -cne $configHash) { throw 'Config backup checksum mismatch.' }
} finally { $input.Dispose() }
# Write-once by script, read-only attribute for accidental edits; admins can still recover it.
[IO.File]::SetAttributes("$backup\config.yaml", [IO.FileAttributes]::ReadOnly)
Assert-Idle
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Tree $installed $stage
Assert-Manifest (Get-Manifest $stage) $original
foreach ($root in $publishRoots) { Copy-Tree $root $stage }
Copy-Item -LiteralPath $core -Destination "$stage\sing-box.exe" -Force
$expected = @{}
foreach ($key in $original.Keys) { $expected[$key] = $original[$key] }
foreach ($key in $overlay.Keys) { $expected[$key] = $overlay[$key] }
$expected['sing-box.exe'] = $archiveCoreHash
Assert-Manifest (Get-Manifest $stage) $expected
Assert-Manifest (Get-Manifest $installed) $original
foreach ($root in $publishRoots) { Assert-Manifest (Get-Manifest $root) $publishMaps[$root] }
if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $configHash) { throw 'Live config changed during staging; refuse handoff.' }
Assert-Idle
# Sensitive backup hash and manifests remain only inside the protected backup root.
@{ Installed = $original; Staged = $expected; ConfigSHA256 = $configHash } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$backup\verification.json" -Encoding UTF8
[pscustomobject]@{
    Phase = 'stage-only'; Machine = 'WINBRAT'; SourceRevision = 'e6b85a1a'
    Stage = $stage; BackupRoot = $backup; InstalledUnchanged = $true; ConfigUnchanged = $true
    StageFileCount = $expected.Count; CoreArchiveVerified = $true; Launched = $false
} | ConvertTo-Json -Compress
