
[CmdletBinding()]
param(
    [ValidatePattern('^(?:|[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?)$')]
    [string]$Version = "",

    [switch]$Prerelease,

    [switch]$Service,

    [switch]$NoLaunch,

    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
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

    $encodedVersion = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Version))
    $forwardFlags = 0
    if ($Prerelease) { $forwardFlags = $forwardFlags -bor 1 }
    if ($Service)    { $forwardFlags = $forwardFlags -bor 2 }
    if ($NoLaunch)   { $forwardFlags = $forwardFlags -bor 4 }
    $bootstrapTemplate = @'
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$version = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__FORWARDED_VERSION__'))
$flags = __FORWARDED_FLAGS__
$installParams = @{ Elevated = $true }
if ($version) { $installParams.Version = $version }
if ($flags -band 1) { $installParams.Prerelease = $true }
if ($flags -band 2) { $installParams.Service = $true }
if ($flags -band 4) { $installParams.NoLaunch = $true }
$response = Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -UseBasicParsing -ErrorAction Stop
$content = $response.Content
if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
& ([ScriptBlock]::Create([string]$content)) @installParams
pause
'@
    $bootstrap = $bootstrapTemplate.Replace('__FORWARDED_VERSION__', $encodedVersion).Replace('__FORWARDED_FLAGS__', [string]$forwardFlags)
    $encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    if (-not [IO.File]::Exists($WindowsPowerShell)) {
        Err "Trusted Windows PowerShell executable not found: $WindowsPowerShell"
        exit 1
    }
    Start-Process -FilePath $WindowsPowerShell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap"
    exit 0
}


Say "VPNRouter installer running as Administrator"

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

$stagingDir = Join-Path $ProgramFilesRoot ("VPNRouter-Installer-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stagingDir -ErrorAction Stop | Out-Null
try {
& (Join-Path $SystemDirectory "icacls.exe") $stagingDir /inheritance:r /grant:r `
    '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) {
    Err "Could not secure installer staging directory"
    exit 1
}
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

$svc = Get-Service -Name VPNRouter -ErrorAction SilentlyContinue
$svcWasRunning = ($svc -and $svc.Status -eq 'Running')

if ($svcWasRunning) {
    Say "Stopping VPNRouter service (was running) before file replacement..."
    Stop-Service -Name VPNRouter -Force -ErrorAction SilentlyContinue
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
            $p | Stop-Process -Force -ErrorAction SilentlyContinue
            $stopped += "$name (PID $($p.Id))"
        } catch {}
    }
}
if ($stopped.Count -gt 0) { Say "Stopped running: $($stopped -join ', ')" }

Start-Sleep -Milliseconds 500

Say "Installing to $InstallRoot"
if (Test-Path $InstallRoot) {
    Get-ChildItem $InstallRoot -Force | ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch {
            Warn "Could not remove $($_.FullName): $_"
        }
    }
} else {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
}

try {
    Expand-Archive -Path $zipPath -DestinationPath $InstallRoot -Force
} catch {
    Err "Extraction failed: $_"
    exit 1
}

if (-not (Test-Path (Join-Path $AppDir "VPNRouter.App.exe"))) {
    Err "Expected VPNRouter.App.exe not found after extraction. ZIP layout may have changed."
    exit 1
}
} finally {
    Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}

Ok "Installed $resolvedVersion to $InstallRoot"

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

$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"iwr -useb $RemoteUninstall | iex`""
Set-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninstallCmd
Set-ItemProperty -Path $UninstallKey -Name QuietUninstallString -Value $uninstallCmd

Ok "Registered in Add/Remove Programs"

if ($Service) {
    Say "Installing Windows Service..."
    $cli = Join-Path $AppDir "VPNRouter.CLI.exe"
    & $cli service install 2>&1 | ForEach-Object { Write-Host "    $_" }
    if ($LASTEXITCODE -eq 0) {
        & $cli service start 2>&1 | ForEach-Object { Write-Host "    $_" }
        Ok "Windows Service installed + started"
    } else {
        Warn "Service install exited with code $LASTEXITCODE"
    }
} elseif ($svcWasRunning) {
    Say "Restarting VPNRouter service (was running pre-upgrade)..."
    Start-Service -Name VPNRouter -ErrorAction SilentlyContinue
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
