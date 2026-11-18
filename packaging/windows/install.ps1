
[CmdletBinding()]
param(
    [ValidatePattern('^(?:|[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?)$')]
    [string]$Version = "",

    [switch]$Prerelease,

    [switch]$Service,

    [switch]$NoLaunch,

    [switch]$Repair,

    [string]$RepairApplicationDir,

    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ProgressPreference = 'SilentlyContinue'

$GitHubRepo      = "PavelLizunov/VPNRouter"
$GitHubApi       = "https://api.github.com/repos/$GitHubRepo"
$ProgramFilesRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$ProgramDataRoot  = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
$SystemDirectory  = [Environment]::SystemDirectory
$WindowsPowerShell = Join-Path $SystemDirectory "WindowsPowerShell\v1.0\powershell.exe"
$InstallRoot     = Join-Path $ProgramFilesRoot "VPNRouter"
$AppDir          = Join-Path $InstallRoot "app"
$DataRoot        = Join-Path $ProgramDataRoot "VPNRouter"
$StartMenuDir    = Join-Path $ProgramDataRoot "Microsoft\Windows\Start Menu\Programs"
$UninstallKey    = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VPNRouter"
$RemoteUninstall = "https://vpn.ninitux.com/uninstall.ps1"

function Say ($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok  ($msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Warn ($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Err  ($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Elevated) {
        Err "Admin rights required. Installation aborted."
        exit 1
    }

    Say "Installation requires admin rights - triggering UAC prompt..."

    $encodedRepairDir = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$RepairApplicationDir))
    $encodedVersion = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Version))
    $forwardFlags = 0
    if ($Prerelease) { $forwardFlags = $forwardFlags -bor 1 }
    if ($Service)    { $forwardFlags = $forwardFlags -bor 2 }
    if ($NoLaunch)   { $forwardFlags = $forwardFlags -bor 4 }
    if ($Repair)     { $forwardFlags = $forwardFlags -bor 8 }
    $bootstrapTemplate = @'
$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$version = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__FORWARDED_VERSION__'))
$flags = __FORWARDED_FLAGS__
$installParams = @{ Elevated = $true }
$repairDir = '__REPAIR_DIRECTORY__'
if ($repairDir) {
    $installParams.RepairApplicationDir = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($repairDir))
}
if ($version) { $installParams.Version = $version }
if ($flags -band 1) { $installParams.Prerelease = $true }
if ($flags -band 2) { $installParams.Service = $true }
if ($flags -band 4) { $installParams.NoLaunch = $true }
if ($flags -band 8) { $installParams.Repair = $true }
$response = Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -UseBasicParsing -ErrorAction Stop
$content = $response.Content
if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
& ([ScriptBlock]::Create([string]$content)) @installParams
pause
'@
    $bootstrap = $bootstrapTemplate.Replace('__REPAIR_DIRECTORY__', $encodedRepairDir).Replace('__FORWARDED_VERSION__', $encodedVersion).Replace('__FORWARDED_FLAGS__', [string]$forwardFlags)
    $encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    if (-not [IO.File]::Exists($WindowsPowerShell)) {
        Err "Trusted Windows PowerShell executable not found: $WindowsPowerShell"
        exit 1
    }
    Start-Process -FilePath $WindowsPowerShell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap"
    exit 0
}


function Assert-RepairObject($entry, [bool]$ancestor) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Repair target is redirected.' }
    $acl = Get-Acl -LiteralPath $entry.FullName -ErrorAction Stop
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

function Resolve-RepairApplication([string]$path) {
    if ($path -notmatch '^[a-zA-Z]:\\' -or $path.Substring(2).Contains(':')) { throw 'Expected a local installation directory.' }
    $dir = Get-Item -LiteralPath ([IO.Path]::GetFullPath($path)) -Force -ErrorAction Stop
    if ($dir -isnot [IO.DirectoryInfo] -or !$dir.Parent) { throw 'Expected an installed VPNRouter directory.' }
    for ($parent = $dir.Parent; $parent; $parent = $parent.Parent) { Assert-RepairObject $parent $true }
    $pending = New-Object 'Collections.Generic.Stack[IO.FileSystemInfo]'
    $pending.Push($dir)
    while ($pending.Count) {
        $item = $pending.Pop()
        Assert-RepairObject $item $false
        if ($item -is [IO.DirectoryInfo]) {
            foreach ($child in $item.EnumerateFileSystemInfos()) { $pending.Push($child) }
        }
    }
    if ([IO.File]::Exists((Join-Path $dir.FullName 'vpnrouter-business-update.json')) -or
        [IO.File]::Exists((Join-Path $dir.FullName 'business-provisioning.json'))) {
        throw 'Use the Business updater to repair a Business installation.'
    }
    if (!([IO.File]::Exists((Join-Path $dir.FullName 'VPNRouter.GUI.exe')) -or
          [IO.File]::Exists((Join-Path $dir.FullName 'VPNRouter.App.exe')))) { throw 'The repair target is not a VPNRouter installation.' }
    return $dir.FullName
}

if ($RepairApplicationDir) {
    if (!$Repair) { throw 'RepairApplicationDir requires repair mode.' }
    $AppDir = Resolve-RepairApplication $RepairApplicationDir
}

Say "VPNRouter installer running as Administrator"

# The Pages version embeds these helpers. A raw checkout can also supply the exact current source bytes.
function Get-InstallerHelper([string]$embedded, [string]$name, [string]$digest) {
    if ($embedded -and !$embedded.StartsWith('__')) { return ,([Convert]::FromBase64String($embedded)) }
    if (!$installerHelpers) { throw 'Use the published self-contained installer at https://vpn.ninitux.com/install.ps1.' }
    $path = [IO.Path]::Combine($installerHelpers, $name)
    $source = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true)).Replace("`r`n", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($source)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '') }
    finally { $hasher.Dispose() }
    if ($actual -ne $digest) { throw 'Installer helper checksum mismatch. Use https://vpn.ninitux.com/install.ps1.' }
    return ,$bytes
}
$installerHelpers = if ($PSScriptRoot) { [IO.Path]::Combine($PSScriptRoot, 'installer') }
$environmentBytes = Get-InstallerHelper '__VPNROUTER_REPAIR_ENVIRONMENT__' 'repair-environment.ps1' '4a3f68e87e16be9e4837ce12cf64825f1fa2c01948ff5a8dea054019eb8eaf53'
. ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($environmentBytes)))

Say "Querying GitHub for target release..."

if ($Version) {
    $tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
    try {
        $release = Invoke-RestMethod -Uri "$GitHubApi/releases/tags/$tag" -UseBasicParsing
    } catch {
        Err "Release $tag not found on GitHub: $_"
        exit 1
    }
} else {
    $releases = Invoke-RestMethod -Uri "$GitHubApi/releases?per_page=30" -UseBasicParsing
    $filtered = $releases | Where-Object {
        (-not $_.draft) -and
        ($Prerelease -or (-not $_.prerelease))
    }
    $release = $filtered | Select-Object -First 1
    if (-not $release) { Err "No matching release found"; exit 1 }
}

$resolvedVersion = $release.tag_name.TrimStart('v')
Say "Target version: $resolvedVersion ($(if ($release.prerelease) { 'prerelease' } else { 'stable' }))"

$currentVersion = (Get-ItemProperty -Path $UninstallKey -Name DisplayVersion -ErrorAction SilentlyContinue).DisplayVersion
if ($currentVersion -eq $resolvedVersion) {
    Say "VPNRouter $resolvedVersion is already installed. Re-installing over existing."
} elseif ($currentVersion) {
    Say "Upgrading VPNRouter from $currentVersion -> $resolvedVersion"
}

$expectedZipName = "VPNRouter-v$resolvedVersion-win.zip"
$zipAssets = @($release.assets | Where-Object { $_.name -eq $expectedZipName })
if ($zipAssets.Count -ne 1) {
    Err "Release must contain exactly one $expectedZipName (found $($zipAssets.Count))"
    exit 1
}
$zipAsset = $zipAssets[0]

$expectedShaName = "$expectedZipName.sha256"
$shaAssets = @($release.assets | Where-Object { $_.name -eq $expectedShaName })
if ($shaAssets.Count -ne 1) {
    Err "Release must contain exactly one $expectedShaName (found $($shaAssets.Count))"
    exit 1
}
$shaAsset = $shaAssets[0]

$keepStaging = $false
$stagingDir = Join-Path $ProgramFilesRoot ("VPNRouter-Installer-" + [Guid]::NewGuid().ToString("N"))
# Keep the creating user's filtered token from changing the ACL of downloaded files.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;RC;;;OW)')
$stage = New-Object IO.DirectoryInfo($stagingDir)
if ($stage.Exists -or [IO.File]::Exists($stagingDir)) { throw 'Installer staging already exists.' }
if ($PSVersionTable.PSEdition -eq 'Core') {
    [IO.FileSystemAclExtensions]::Create($stage, $acl)
} else {
    $stage.Create($acl)
}
try {
$zipPath = Join-Path $stagingDir $expectedZipName
$shaTmp = Join-Path $stagingDir $expectedShaName

Say "Downloading $($zipAsset.name) ($([math]::Round($zipAsset.size / 1MB, 1)) MB)..."
Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -UseBasicParsing

Say "Verifying SHA256..."
Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaTmp -UseBasicParsing
$expectedSha = ((Get-Content -Raw $shaTmp).Trim() -split '\s+')[0].ToLowerInvariant()
Remove-Item $shaTmp -Force -ErrorAction SilentlyContinue
if ($expectedSha -notmatch '^[0-9a-f]{64}$') {
    Err "Malformed SHA256 sidecar - refusing an unverified install"
    exit 1
}
$actualSha = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
if ($actualSha -ne $expectedSha) {
    Err "SHA256 mismatch! Expected $expectedSha, got $actualSha"
    exit 1
}
Ok "SHA256 verified: $actualSha"

# Unpack and check the new payload BEFORE touching the existing installation, so that a failed extraction cannot leave it gutted.
$payloadDir = Join-Path $stagingDir "payload"
Say "Unpacking $expectedZipName"
try {
    Expand-Archive -Path $zipPath -DestinationPath $payloadDir -Force
} catch {
    Err "Extraction failed (the existing installation was not touched): $_"
    exit 1
}

if (-not (Test-Path (Join-Path $payloadDir "app\VPNRouter.App.exe"))) {
    Err "Expected app\VPNRouter.App.exe not found in the archive. ZIP layout may have changed. The existing installation was not touched."
    exit 1
}

# Capture the old service's start mode and running state before stopping the old app.
$canRestoreZapret = [IO.File]::Exists((Join-Path $payloadDir 'app\zapret-maintenance.ps1'))
# Prepare belongs to the running installer even if the selected release contains an older helper.
$maintenanceBytes = Get-InstallerHelper '__VPNROUTER_MAINTENANCE_BASE64__' 'zapret-maintenance.ps1' '87a7e58202b3a050e634abf8d8bf9cfebc5a03bc893b1972b39feb2f94ff8e6f'
$prepare = Join-Path $stagingDir 'zapret-maintenance.ps1'
[IO.File]::WriteAllBytes($prepare, $maintenanceBytes)
& $WindowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $prepare -Edition VPNRouter -Operation Prepare
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the verified legacy Zapret service. Installation stopped before file replacement.' }

$svc = Get-Service -Name VPNRouter -ErrorAction SilentlyContinue
$expectedService = Join-Path $AppDir 'VPNRouter.Service.exe'
function Test-OwnService {
    $registered = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'" -ErrorAction Stop
    if (!$registered) { return $false }
    $image = ([string]$registered.PathName).Trim()
    $quoted = '"' + $expectedService + '"'
    return ($image -in @($quoted, ($quoted + ' --service')) -or
        ($expectedService -notmatch '\s' -and $image -in @($expectedService, ($expectedService + ' --service'))))
}
function Start-OwnService {
    $controller = Get-Service -Name VPNRouter -ErrorAction Stop
    try {
        $null = $controller.ServiceHandle
        if (!(Test-OwnService)) { throw 'The VPNRouter service owner changed.' }
        Start-Service -InputObject $controller -ErrorAction Stop
    } finally { $controller.Dispose() }
}
$svcWasRunning = ($svc -and $svc.Status -eq 'Running' -and (Test-OwnService))

if ($svcWasRunning) {
    Say "Stopping VPNRouter service (was running) before file replacement..."
    $null = $svc.ServiceHandle
    if (!(Test-OwnService)) { throw 'The VPNRouter service owner changed before upgrade.' }
    Stop-Service -InputObject $svc -ErrorAction Stop
    $tries = 0
    while ($tries -lt 20) {
        $svc.Refresh()
        if ($svc.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
        $tries++
    }
}

$stopped = @()
foreach ($name in @("VPNRouter.App", "VPNRouter.CLI", "VPNRouter.Service", "VPNRouter.GUI", "sing-box")) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        try {
            $null = $p.Handle
            $image = $p.MainModule.FileName
            $expected = Join-Path $AppDir ($name + '.exe')
            $dataEngine = Join-Path $DataRoot 'bin\sing-box.exe'
            if ($image -ne $expected -and ($name -ne 'sing-box' -or $image -ne $dataEngine)) { continue }
            $p.Kill()
            $stopped += "$name (PID $($p.Id))"
        } catch {} finally { $p.Dispose() }
    }
}
if ($stopped.Count -gt 0) { Say "Stopped running: $($stopped -join ', ')" }

Start-Sleep -Milliseconds 500

# The split-tunnel kernel driver that VPNRouter loads from <InstallRoot>\app\driver stays loaded after the app is killed and locks
# its .sys file. Without stopping it, the folder wipe below leaves that file behind and the extraction then fails on it, which
# used to leave a half-deleted installation behind (the shortcut said "the item has been moved or deleted"). Stop it only when its
# image lives inside this install folder: a Mullvad client or another copy keeps its own driver.
$driver = Get-CimInstance Win32_SystemDriver -Filter "Name='mullvad-split-tunnel'" -ErrorAction SilentlyContinue
if ($driver -and $driver.State -ne 'Stopped') {
    $driverImage = ([string]$driver.PathName) -replace '^\\\?\?\\', ''
    if ($driverImage.Trim('"') -eq (Join-Path $AppDir 'driver\mullvad-split-tunnel.sys')) {
        Say "Stopping the split-tunnel driver (it locks its file inside $InstallRoot)"
        Stop-Service -Name 'mullvad-split-tunnel' -ErrorAction Stop
        for ($i = 0; $i -lt 20; $i++) {
            if ((Get-CimInstance Win32_SystemDriver -Filter "Name='mullvad-split-tunnel'").State -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }
    }
}

Say "Installing to $InstallRoot"
if (!$RepairApplicationDir -and (Test-Path $InstallRoot) -and !$Repair) {
    Get-ChildItem $InstallRoot -Force | ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch {
            Warn "Could not remove $($_.FullName): $_"
        }
    }
} elseif (!$RepairApplicationDir) {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
}

try {
    if ($RepairApplicationDir) {
        Get-ChildItem -LiteralPath (Join-Path $payloadDir 'app') -Force | Copy-Item -Destination $AppDir -Recurse -Force -ErrorAction Stop
    } else {
        Get-ChildItem $payloadDir -Force | Copy-Item -Destination $InstallRoot -Recurse -Force -ErrorAction Stop
    }
} catch {
    Err "Copying the new files failed: $_"
    Err "The unpacked payload is kept in $payloadDir; run the installer again once the file named above is no longer in use."
    $keepStaging = $true
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $AppDir "VPNRouter.App.exe"))) {
    Err "Expected VPNRouter.App.exe not found after extraction. ZIP layout may have changed."
    exit 1
}
} finally {
    if (-not $keepStaging) { Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Ok "Installed $resolvedVersion to $InstallRoot"

# Historical App/GUI versions may run their own automatic repair using inherited TEMP and PATH.
Set-RepairEnvironment (Resolve-RepairApplication $AppDir)

if ($canRestoreZapret) {
    $migration = Start-Process -FilePath (Join-Path $AppDir 'VPNRouter.App.exe') -ArgumentList '--migrate-zapret' -WorkingDirectory $AppDir -Wait -PassThru
    if ($migration.ExitCode -notin @(0, 2)) { throw 'Zapret service migration failed. Rerun VPNRouter.App.exe --migrate-zapret as administrator before restarting Windows.' }
    if ($migration.ExitCode -eq 2) { Warn 'Zapret service choice saved. Run VPNRouter.App.exe --migrate-zapret as administrator to retry the verified download and restore the service.' }
} elseif ($null -ne (Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\zapret' -Name VPNRouterPreUpgradeState -ErrorAction SilentlyContinue)) {
    Warn 'The previous Zapret service choice is saved. Install a migration-capable version and run VPNRouter.App.exe --migrate-zapret as administrator.'
}

if ($Repair) {
    if ($svcWasRunning -and (Test-OwnService)) { Start-OwnService }
    & (Join-Path $AppDir 'VPNRouter.CLI.exe') doctor
    if ($RepairApplicationDir -and !$NoLaunch) {
        Start-Process -FilePath (Join-Path $AppDir 'VPNRouter.App.exe') -WorkingDirectory $AppDir
    }
    return
}

$exclOk = $false
try {
    Add-MpPreference -ExclusionPath $InstallRoot -ErrorAction SilentlyContinue
    Add-MpPreference -ExclusionPath $DataRoot    -ErrorAction SilentlyContinue
    $paths = @()
    try { $paths = (Get-MpPreference -ErrorAction SilentlyContinue).ExclusionPath } catch {}
    $exclOk = ($paths -contains $InstallRoot) -and ($paths -contains $DataRoot)
} catch {}

if ($exclOk) {
    Ok "Defender exclusions verified ($InstallRoot, $DataRoot)"
} else {
    $tp = $null
    try { $tp = (Get-MpComputerStatus -ErrorAction SilentlyContinue).IsTamperProtected } catch {}
    $thirdParty = @()
    try {
        $thirdParty = (Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
            Where-Object { $_.displayName -and $_.displayName -notmatch 'Windows Defender' } | Select-Object -ExpandProperty displayName)
    } catch {}

    Warn "Could NOT confirm a Defender exclusion for VPNRouter."
    if ($tp -eq $true) { Warn "  Reason: Windows Tamper Protection is ON - it blocks scripted exclusions." }
    if ($thirdParty) { Warn "  Detected third-party antivirus: $($thirdParty -join ', ') (Defender exclusions do not apply to it)." }
    Warn "  => VPNRouter is UNSIGNED; an antivirus may delete it on a reboot scan."
    Warn "  => Add these two paths to your antivirus / Windows Security EXCLUSIONS manually:"
    Warn "       $InstallRoot"
    Warn "       $DataRoot"
    Warn "     Windows Security > Virus & threat protection > Manage settings > Exclusions > Add > Folder."
}

Say "Restricting data directory ACL ($DataRoot)..."
try {
    if (-not (Test-Path $DataRoot)) {
        New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    }
    $acl = Get-Acl $DataRoot
    $acl.SetAccessRuleProtection($true, $true)

    $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
    $noProp  = [System.Security.AccessControl.PropagationFlags]::None

    $systemSid = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminsSid = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $usersSid  = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)

    $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $systemSid,"FullControl",$inherit,$noProp,"Allow")))
    $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $adminsSid,"FullControl",$inherit,$noProp,"Allow")))
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $me,"Modify",$inherit,$noProp,"Allow")))
    } catch { }

    $acl.RemoveAccessRuleAll((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $usersSid,"ReadAndExecute",$inherit,$noProp,"Allow"))) | Out-Null

    Set-Acl $DataRoot $acl
    Ok "Data directory ACL restricted (SYSTEM + Administrators FullControl, Users read removed)"
} catch {
    Warn "Could not restrict data directory ACL: $_"
    Warn "  => On a shared box, local users may still read $DataRoot (config / logs)."
    Warn "     Restrict it manually: icacls `"$DataRoot`" /inheritance:r /grant:r SYSTEM:F Administrators:F"
}

Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

Say "Creating Start Menu shortcut..."
$lnkPath = Join-Path $StartMenuDir "VPNRouter.lnk"
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut($lnkPath)
$lnk.TargetPath       = Join-Path $AppDir "VPNRouter.GUI.exe"
$lnk.WorkingDirectory = $AppDir
$lnk.IconLocation     = "$(Join-Path $AppDir 'VPNRouter.App.exe'),0"
$lnk.Description      = "Virtual Penguin Network - split-tunnel VPN router"
$lnk.Save()

Say "Registering Add/Remove Programs entry..."
if (-not (Test-Path $UninstallKey)) {
    New-Item -Path $UninstallKey -Force | Out-Null
}
$sizeKb = [int]((Get-ChildItem $InstallRoot -Recurse -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum / 1KB)

Set-ItemProperty -Path $UninstallKey -Name DisplayName       -Value "VPNRouter"
Set-ItemProperty -Path $UninstallKey -Name DisplayVersion    -Value $resolvedVersion
Set-ItemProperty -Path $UninstallKey -Name Publisher         -Value "NiniTux"
Set-ItemProperty -Path $UninstallKey -Name DisplayIcon       -Value (Join-Path $AppDir "VPNRouter.App.exe")
Set-ItemProperty -Path $UninstallKey -Name InstallLocation   -Value $InstallRoot
Set-ItemProperty -Path $UninstallKey -Name URLInfoAbout      -Value "https://github.com/$GitHubRepo"
Set-ItemProperty -Path $UninstallKey -Name HelpLink          -Value "https://github.com/$GitHubRepo/issues"
Set-ItemProperty -Path $UninstallKey -Name EstimatedSize     -Value $sizeKb -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoModify          -Value 1       -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoRepair          -Value 1       -Type DWord

$uninstallCmd = ('"' + $WindowsPowerShell + '"') + " -NoProfile -ExecutionPolicy Bypass -Command `"iwr -useb $RemoteUninstall | iex`""
Set-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninstallCmd
Set-ItemProperty -Path $UninstallKey -Name QuietUninstallString -Value $uninstallCmd

Ok "Registered in Add/Remove Programs"

if ($Service) {
    $registered = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'" -ErrorAction Stop
    if (!$registered) {
        $cli = Join-Path $AppDir "VPNRouter.CLI.exe"
        & $cli service install
        if ($LASTEXITCODE -ne 0) { Warn "Service install exited with code $LASTEXITCODE" }
    }
    if (Test-OwnService) {
        Start-OwnService
        Ok "Windows Service installed and started"
    } else {
        Warn 'Could not verify a VPNRouter service from this installation. Check its registration before starting it.'
    }
} elseif ($svcWasRunning -and (Test-OwnService)) {
    Start-OwnService
}

if (-not $NoLaunch) {
    Say "Launching VPNRouter..."
    Start-Process (Join-Path $AppDir "VPNRouter.GUI.exe")
}

Write-Host ""
Ok "VPNRouter $resolvedVersion installed successfully"
Write-Host ""
Write-Host "  Install dir:   $InstallRoot"
Write-Host "  Start Menu:    $lnkPath"
Write-Host "  Data dir:      $DataRoot"
Write-Host "  Add/Remove:    Settings -> Apps -> 'VPNRouter'"
Write-Host ""
Write-Host "  Upgrade:       iwr -useb https://vpn.ninitux.com/install.ps1 | iex"
Write-Host "  Uninstall:     iwr -useb https://vpn.ninitux.com/uninstall.ps1 | iex"
Write-Host "                 (or Settings -> Apps -> VPNRouter -> Uninstall)"
Write-Host ""
