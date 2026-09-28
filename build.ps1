param(
    [string]$Version = "1.0",
    [string]$SingBoxVersion = "1.14.0-vpnctl.5",
    [string]$SingBoxSha256 = "3823e4baed13fec43b84acefa480ff9cf9b2c222ea9dd9ceb9987aefd623aeb4",
    [string]$SingBoxPath = "",
    [string]$SlipstreamPath = "",
    [switch]$Upload,
    [string]$GitHubRepo = "PavelLizunov/VPNRouter",
    [switch]$AndroidAlso,
    [switch]$BundleSplitDriver
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$bundleSplitDriver = $BundleSplitDriver -or $Upload

if ($Upload) {
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$') {
        throw 'Release version must be X.Y.Z or X.Y.Z-rN.'
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh CLI not found." }
    $releaseCommit = (& git -C $Root rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $releaseCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve release HEAD.' }
    $sourceChanges = @(& git -C $Root status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count) { throw 'Release source checkout must be clean.' }
    $acceptedCommit = (& gh api "repos/$GitHubRepo/commits/main" --jq '.sha' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $acceptedCommit -ne $releaseCommit) { throw 'Release HEAD must equal accepted main.' }
    $tagCommit = (& gh api "repos/$GitHubRepo/commits/v$Version" --jq '.sha' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $releaseCommit) { throw 'Existing remote release tag must match HEAD.' }
    $releaseJson = (& gh release view "v$Version" --repo $GitHubRepo --json isDraft,isPrerelease | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Create the authorized draft release before building.' }
    $releaseState = $releaseJson | ConvertFrom-Json
    if (-not $releaseState.isDraft -or [bool]$releaseState.isPrerelease -ne ($Version -match '-r[1-9][0-9]*$')) {
        throw 'Release must be a draft with the correct candidate/stable channel.'
    }
    $secretJson = (& gh api --paginate "repos/$GitHubRepo/actions/secrets?per_page=100" --jq '.secrets[].name' | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect signing enrollment; unsigned staging is refused.' }
    $variableNames = (& gh api --paginate "repos/$GitHubRepo/actions/variables?per_page=100" --jq '.variables[].name' | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect signing configuration; unsigned staging is refused.' }
    if ($secretJson -match '(?m)^SIGNPATH_' -or $variableNames -match '(?m)^SIGNPATH_EXPECTED_SUBJECT\s*$') {
        throw 'SignPath configuration is present (possibly incomplete); use Sign Windows, never unsigned fallback.'
    }
}

$appVersionFile = Join-Path $Root "VPNRouter.Core\AppVersion.cs"
if (-not (Test-Path $appVersionFile)) {
    throw "ABORT: AppVersion.cs not found at $appVersionFile. Are you running build.ps1 from the wrong directory?"
}
$appVersionLine = (Get-Content $appVersionFile |
    Select-String 'string Version =' | Select-Object -First 1).Line
if (-not $appVersionLine) {
    throw "ABORT: could not parse 'string Version =' from $appVersionFile."
}
if ($appVersionLine -match '"([^"]+)"') {
    $srcVersion = $Matches[1]
} else {
    throw "ABORT: AppVersion.cs Version line has no quoted value: $appVersionLine"
}
if ($srcVersion -ne $Version) {
    throw @"
ABORT: -Version '$Version' does not match AppVersion.cs '$srcVersion'.

This is the v2.29.0-r1..r5 fake-tag bug. Either:
  (a) Bump $appVersionFile Version constant to '$Version' and commit, OR
  (b) Run build.ps1 with -Version '$srcVersion' to match the source on disk, OR
  (c) If you're working in a worktree, make sure you've pulled main repo:
        cd '$Root' ; git pull --ff-only origin main
      then re-run.

Refusing to ship a binary whose AppVersion does not match the release tag.
"@
}
Write-Host "[0/9] AppVersion match: $srcVersion = -Version $Version OK" -ForegroundColor Green

if ($Upload -and $SingBoxPath) {
    throw "SingBoxPath override is for local builds only and cannot be used with -Upload."
}
if ($SingBoxPath) {
    if (-not (Test-Path $SingBoxPath)) {
        throw "SingBoxPath not found: $SingBoxPath"
    }
} else {
    if ($SingBoxVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+-vpnctl\.[0-9]+$') {
        throw "SingBoxVersion must match pattern '^[0-9]+\.[0-9]+\.[0-9]+-vpnctl\.[0-9]+$': $SingBoxVersion"
    }
    if (-not $SingBoxSha256 -or $SingBoxSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "SingBoxSha256 must be a non-blank 64-character hex string: $SingBoxSha256"
    }
}

$DistDir = Join-Path $Root "publish\dist"
$FdDir = Join-Path $Root "publish\fd"
$UpdateDir = Join-Path $Root "publish\update"
$PackageDir = Join-Path $Root "publish\package"
$InstallZipName = "VPNRouter-v$Version-win.zip"
$UpdateZipName = "VPNRouter-update-v$Version-win.zip"
$InstallZipPath = Join-Path $Root $InstallZipName
$UpdateZipPath = Join-Path $Root $UpdateZipName

Write-Host "=== VPNRouter Build Script ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Install: $InstallZipPath"
Write-Host "Update:  $UpdateZipPath"
Write-Host ""

Write-Host "[1/9] Cleaning previous build..." -ForegroundColor Yellow
foreach ($dir in @($DistDir, $FdDir, $UpdateDir, $PackageDir)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}


Write-Host "[2/9] Publishing VPNRouter.App (Avalonia, self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

Write-Host "[3/9] Publishing VPNRouter.CLI (self-contained, shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Write-Host "[4/9] Publishing VPNRouter.Service (self-contained, shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

Write-Host "[4b/9] Building VPNRouter.GUI launcher stub (Go native)..." -ForegroundColor Yellow
$stubExe = Join-Path $DistDir "VPNRouter.GUI.exe"
$env:GOOS = "windows"
$env:GOARCH = "amd64"
if ($Version -match '-r\d+$') { $stubChannel = "prerelease" } else { $stubChannel = "stable" }
$stubLdflags = "-s -w -H windowsgui -X main.ChannelHint=$stubChannel"
Push-Location "$Root\VPNRouter.GUI"
go build -ldflags="$stubLdflags" -o $stubExe . 2>&1 | Out-Null
$stubExitCode = $LASTEXITCODE
Pop-Location
if ($stubExitCode -ne 0) { throw "GUI stub build failed (is Go installed?)" }
Write-Host "       Stub channel: $stubChannel" -ForegroundColor Gray

Write-Host "[5/9] Building app file list (framework-dependent)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
Copy-Item $stubExe $FdDir -Force
Write-Host "       App files identified: $((Get-ChildItem $FdDir -File).Count) files" -ForegroundColor Gray

Get-ChildItem $DistDir -Recurse -Include "*.pdb", "appsettings.*.json" | Remove-Item -Force

$localeDirs = @("cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "sv", "tr", "zh-Hans", "zh-Hant")
foreach ($locale in $localeDirs) {
    $localeDir = Join-Path $DistDir $locale
    if (Test-Path $localeDir) { Remove-Item -Recurse -Force $localeDir }
}

$unusedFiles = @(
    "createdump.exe",
    "mscordaccore.dll", "mscordaccore_amd64_amd64_*.dll", "mscordbi.dll"
)
foreach ($pattern in $unusedFiles) {
    Get-ChildItem $DistDir -Filter $pattern | Remove-Item -Force -ErrorAction SilentlyContinue
}

$wpfPatterns = @(
    "PresentationFramework*.dll", "PresentationCore.dll", "PresentationUI.dll",
    "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll",
    "System.Xaml.dll", "System.Windows.Controls.Ribbon.dll",
    "ReachFramework.dll", "System.Printing.dll",
    "System.Windows.Input.Manipulations.dll", "System.Windows.Presentation.dll",
    "System.IO.Packaging.dll", "DirectWriteForwarder.dll",
    "PenImc_cor3.dll", "vcruntime140_cor3.dll",
    "WindowsBase.dll", "WindowsFormsIntegration.dll"
)
$wpfRemoved = 0
foreach ($pattern in $wpfPatterns) {
    Get-ChildItem $DistDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        $wpfRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}

$nativeRemoved = 0
foreach ($dir in @("arm64", "x86")) {
    $dirPath = Join-Path $DistDir $dir
    if (Test-Path $dirPath) {
        $nativeRemoved += (Get-ChildItem $dirPath -File -Recurse | Measure-Object Length -Sum).Sum
        Remove-Item $dirPath -Recurse -Force
    }
}
$msdia = Join-Path $DistDir "amd64\msdia140.dll"
if (Test-Path $msdia) {
    $nativeRemoved += (Get-Item $msdia).Length
    Remove-Item $msdia -Force
}
$diasym = Join-Path $DistDir "Microsoft.DiaSymReader.Native.amd64.dll"
if (Test-Path $diasym) {
    $nativeRemoved += (Get-Item $diasym).Length
    Remove-Item $diasym -Force
}

$unusedAssemblies = @(
    "System.Windows.Forms.Design.dll", "System.Windows.Forms.Design.Editors.dll",
    "Microsoft.VisualBasic.Core.dll", "System.CodeDom.dll",
    "System.DirectoryServices.dll"
)
$designRemoved = 0
foreach ($pattern in $unusedAssemblies) {
    Get-ChildItem $DistDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        $designRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}

$totalSaved = ($wpfRemoved + $nativeRemoved + $designRemoved) / 1MB
Write-Host "       Cleaned PDB, locale, debug, WPF, and unused files" -ForegroundColor Gray
Write-Host "       Removed: WPF $([math]::Round($wpfRemoved/1MB,1)) MB + natives $([math]::Round($nativeRemoved/1MB,1)) MB + design $([math]::Round($designRemoved/1MB,1)) MB = $([math]::Round($totalSaved,1)) MB saved" -ForegroundColor Gray

Write-Host "[6/9] Bundling sing-box.exe..." -ForegroundColor Yellow
if ($SingBoxPath) {
    if ($Upload) {
        throw "SingBoxPath override is for local builds only and cannot be used with -Upload."
    }
    if (-not (Test-Path $SingBoxPath)) {
        throw "SingBoxPath not found: $SingBoxPath"
    }
    Copy-Item $SingBoxPath (Join-Path $DistDir "sing-box.exe") -Force
    $ovCronet = Join-Path (Split-Path $SingBoxPath -Parent) "libcronet.dll"
    if (Test-Path $ovCronet) { Copy-Item $ovCronet (Join-Path $DistDir "libcronet.dll") -Force }
    Write-Host "       Copied from: $SingBoxPath" -ForegroundColor Gray
} else {
    $singBoxCache = Join-Path $Root "tools\singbox-cache"
    New-Item -ItemType Directory -Force -Path $singBoxCache | Out-Null
    $zipName = "sing-box-$SingBoxVersion-windows-amd64.zip"
    $zipPath = Join-Path $singBoxCache $zipName
    $extractDir = Join-Path $singBoxCache "sing-box-$SingBoxVersion-windows-amd64"
    $cachedExe = Join-Path $extractDir "sing-box.exe"

    if (-not (Test-Path $zipPath)) {
        $dlUrl = "https://github.com/PavelLizunov/sing-box-vpnctl/releases/download/v$SingBoxVersion/$zipName"
        Write-Host "       Downloading sing-box-vpnctl v$SingBoxVersion from $dlUrl..." -ForegroundColor Gray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $dlUrl -OutFile $zipPath -UseBasicParsing
        } catch {
            Write-Host "       ERROR: Download failed: $_" -ForegroundColor Red
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            throw "sing-box-vpnctl download failed. Check https://github.com/PavelLizunov/sing-box-vpnctl/releases/tag/v$SingBoxVersion"
        }
    }

    $actualHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $SingBoxSha256.ToLowerInvariant()) {
        Remove-Item $zipPath -Force
        throw "sing-box-vpnctl SHA256 mismatch: expected $SingBoxSha256 but got $actualHash"
    }

    if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
    Expand-Archive -Path $zipPath -DestinationPath $singBoxCache -Force
    if (-not (Test-Path $cachedExe)) {
        throw "sing-box.exe not found inside $zipName after extraction"
    }

    Get-ChildItem -File $extractDir | ForEach-Object {
        $destName = if ($_.Name -ieq 'LICENSE') { 'LICENSE.sing-box' } else { $_.Name }
        Copy-Item $_.FullName (Join-Path $DistDir $destName) -Force
    }
    $sbSize = [math]::Round((Get-Item $cachedExe).Length / 1MB, 1)
    $cronetNote = if (Test-Path (Join-Path $extractDir 'libcronet.dll')) { ' + libcronet' } else { '' }
    Write-Host "       Bundled sing-box-vpnctl v$SingBoxVersion$cronetNote ($sbSize MB exe)" -ForegroundColor Green
}

if (-not $SingBoxPath) {
    $cronetCache = Join-Path $Root "tools\singbox-cache"
    New-Item -ItemType Directory -Force -Path $cronetCache | Out-Null
    $cronetZipName = "sing-box-1.13.14-windows-amd64.zip"
    $cronetZipPath = Join-Path $cronetCache $cronetZipName
    $cronetArchiveSha256 = "f580782c6dd10f7691c66cea1d7c421813c5fbf7e305d1ee7ce0c3a40d196341"
    $cronetDllSha256 = "c7434cfa93c3041321dd19111c4de6c52b8a9531a65661ba45425d3c51ec69e2"
    $cronetExtractDir = Join-Path $cronetCache "sing-box-1.13.14-windows-amd64"
    $cronetDll = Join-Path $cronetExtractDir "libcronet.dll"

    if (-not (Test-Path $cronetZipPath)) {
        $cronetUrl = "https://github.com/SagerNet/sing-box/releases/download/v1.13.14/$cronetZipName"
        Write-Host "       Downloading SagerNet sing-box v1.13.14 for libcronet.dll from $cronetUrl..." -ForegroundColor Gray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $cronetUrl -OutFile $cronetZipPath -UseBasicParsing
        } catch {
            Write-Host "       ERROR: Download failed: $_" -ForegroundColor Red
            if (Test-Path $cronetZipPath) { Remove-Item $cronetZipPath -Force }
            throw "SagerNet sing-box download failed. Check https://github.com/SagerNet/sing-box/releases/tag/v1.13.14"
        }
    }

    $actualArchiveHash = (Get-FileHash $cronetZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $cronetArchiveSha256.ToLowerInvariant()) {
        Remove-Item $cronetZipPath -Force
        throw "SagerNet sing-box SHA256 mismatch: expected $cronetArchiveSha256 but got $actualArchiveHash"
    }

    if (Test-Path $cronetExtractDir) { Remove-Item -Recurse -Force $cronetExtractDir }
    Expand-Archive -Path $cronetZipPath -DestinationPath $cronetCache -Force
    if (-not (Test-Path $cronetDll)) {
        throw "libcronet.dll not found inside $cronetZipName after extraction"
    }

    $actualDllHash = (Get-FileHash $cronetDll -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualDllHash -ne $cronetDllSha256.ToLowerInvariant()) {
        throw "libcronet.dll SHA256 mismatch: expected $cronetDllSha256 but got $actualDllHash"
    }

    Copy-Item $cronetDll (Join-Path $DistDir "libcronet.dll") -Force
    $cronetLicense = Join-Path $cronetExtractDir "LICENSE"
    if (Test-Path $cronetLicense) {
        Copy-Item $cronetLicense (Join-Path $DistDir "LICENSE.libcronet") -Force
    }
    Write-Host "       Bundled libcronet.dll from SagerNet v1.13.14 (verified SHA256)" -ForegroundColor Green
}

Write-Host "[6b/9] Bundling slipstream-client.exe (DNS-tunnel)..." -ForegroundColor Yellow
$slipStreamSrc = ""
if ($SlipstreamPath -and (Test-Path $SlipstreamPath)) {
    $slipStreamSrc = $SlipstreamPath
} else {
    $slipCache = Join-Path $Root "tools\slipstream-cache\slipstream-client.exe"
    if (Test-Path $slipCache) { $slipStreamSrc = $slipCache }
}
if ($slipStreamSrc) {
    Copy-Item $slipStreamSrc (Join-Path $DistDir "slipstream-client.exe") -Force
    $slipVcr = Join-Path (Split-Path $slipStreamSrc -Parent) "VCRUNTIME140.dll"
    if (Test-Path $slipVcr) { Copy-Item $slipVcr (Join-Path $DistDir "VCRUNTIME140.dll") -Force }
    $slipSize = [math]::Round((Get-Item $slipStreamSrc).Length / 1MB, 1)
    Write-Host "       Bundled slipstream-client ($slipSize MB) from $slipStreamSrc" -ForegroundColor Green
} else {
    Write-Host "       slipstream-client: NOT bundled (no -SlipstreamPath, no tools\slipstream-cache) - dns-tunnel unavailable until built+placed" -ForegroundColor Yellow
}

Write-Host "[6c/9] Bundling split-tunnel driver..." -ForegroundColor Yellow
if ($bundleSplitDriver) {
    $stCommit = "cc0affb2f06e870fb594e2dd6d61049611991586"
    $stCache  = Join-Path $Root "tools\driver-cache\$stCommit"
    New-Item -ItemType Directory -Force -Path $stCache | Out-Null
    $stPins = [ordered]@{
        "mullvad-split-tunnel.sys" = "10cf25bbcfe51fd663a1fec88a98e9b858f3a579589bb2ec496b66e4fdd1b201"
        "mullvad-split-tunnel.cat" = "c599926a0327d7ae06b534f4cd039db30392e1897bb9d03e4fec3631744a4e6d"
        "mullvad-split-tunnel.inf" = "3dd5905e5fb98d61a942a33e8c9a5ba07c3a2de1e4f319e1fec3e54df6591608"
    }
    $stDst = Join-Path $DistDir "driver"
    New-Item -ItemType Directory -Force -Path $stDst | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $stChecksums = @()
    foreach ($f in $stPins.Keys) {
        $cached = Join-Path $stCache $f
        if (-not (Test-Path $cached)) {
            $url = "https://github.com/mullvad/mullvadvpn-app-binaries/raw/$stCommit/x86_64-pc-windows-msvc/split-tunnel/$f"
            Write-Host "       Downloading $f ..." -ForegroundColor Gray
            try { Invoke-WebRequest -Uri $url -OutFile $cached -UseBasicParsing }
            catch { if (Test-Path $cached) { Remove-Item $cached -Force }; throw "split-tunnel driver download failed for ${f}: $_" }
        }
        $actual = (Get-FileHash -Algorithm SHA256 $cached).Hash.ToLower()
        if ($actual -ne $stPins[$f]) {
            throw "SUPPLY-CHAIN GATE FAIL: $f sha256 mismatch. pinned=$($stPins[$f]) actual=$actual. Re-verify the ABI ref + driver-cache before shipping."
        }
        Copy-Item $cached (Join-Path $stDst $f) -Force
        $stChecksums += "$actual  $f"
    }
    $stChecksums | Set-Content -Encoding ascii (Join-Path $stDst "checksums.sha256")
    $stLicense = Join-Path $Root "LICENSE.split-tunnel"
    if (Test-Path $stLicense) { Copy-Item $stLicense (Join-Path $DistDir "LICENSE.split-tunnel") -Force }
    Write-Host "       Bundled split-tunnel driver (3 files, sha256 gate OK) -> dist\driver\" -ForegroundColor Green
} else {
    Write-Host "       split-tunnel driver: NOT bundled (local build without -BundleSplitDriver)" -ForegroundColor Gray
}

Write-Host "       Zapret: downloaded on demand (not bundled)" -ForegroundColor Gray

$ProfilesSrc = Join-Path $Root "profiles"
$ProfilesDst = Join-Path $DistDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $ProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $ProfilesDst -Recurse
    Write-Host "       Profiles copied" -ForegroundColor Gray
}

$ReadmePath = Join-Path $DistDir "README.txt"
@"
VPNRouter v$Version
====================

Quick Start:
1. Double-click "Start VPN.cmd" (or run app\VPNRouter.App.exe directly)
2. Accept the UAC prompt
3. Paste your VLESS URI(s) in the Servers tab
4. Select application groups in the Applications tab
5. Click Start VPN

Folder Structure:
- Start VPN.cmd            Launcher (double-click to start)
- README.txt               This file
- app\                     Application files
  - VPNRouter.App.exe      Main app (Avalonia GUI, tray icon, settings)
  - VPNRouter.CLI.exe      Command-line interface (advanced)
  - VPNRouter.Service.exe  Windows Service (optional, for auto-start)
  - sing-box.exe           VPN engine (auto-copied on first run)
  - profiles\              Application profiles

CLI Usage (run from app\ folder):
  VPNRouter.CLI.exe start --profile Discord_Privacy
  VPNRouter.CLI.exe status
  VPNRouter.CLI.exe stop

Service Installation (run as admin):
  VPNRouter.CLI.exe service install
  VPNRouter.CLI.exe service start
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

Write-Host "[7/9] Creating package layout..." -ForegroundColor Yellow
$AppDir = Join-Path $PackageDir "app"
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

Copy-Item "$DistDir\*" $AppDir -Recurse

'@start "" "%~dp0app\VPNRouter.App.exe"' | Set-Content (Join-Path $PackageDir "Start VPN.cmd") -Encoding ASCII

Move-Item (Join-Path $AppDir "README.txt") (Join-Path $PackageDir "README.txt") -Force

Write-Host "       Package layout: Start VPN.cmd + README.txt + app/" -ForegroundColor Gray

Write-Host "[8/9] Creating install ZIP (app/ layout)..." -ForegroundColor Yellow
if (Test-Path $InstallZipPath) { Remove-Item $InstallZipPath }
Compress-Archive -Path "$PackageDir\*" -DestinationPath $InstallZipPath -CompressionLevel Optimal

Write-Host "[9/9] Creating update ZIP (bootstrap layout)..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $UpdateDir | Out-Null

$updateGuiStub = Join-Path $DistDir "VPNRouter.GUI.exe"
if (-not (Test-Path $updateGuiStub)) {
    throw "Update ZIP build: VPNRouter.GUI.exe missing from $DistDir - Go stub not built?"
}
Copy-Item $updateGuiStub $UpdateDir
Write-Host "       VPNRouter.GUI.exe -> ROOT (bootstrap entry)" -ForegroundColor Gray

$BootstrapDir = Join-Path $UpdateDir "_bootstrap"
New-Item -ItemType Directory -Force -Path $BootstrapDir | Out-Null

$fdFileNames = (Get-ChildItem $FdDir -File).Name | Sort-Object -Unique
$updateFileCount = 0
foreach ($name in $fdFileNames) {
    if ($name -eq "VPNRouter.GUI.exe") { continue }  # already at root
    $src = Join-Path $DistDir $name
    if (Test-Path $src) {
        Copy-Item $src $BootstrapDir
        $updateFileCount++
    }
}
$singBoxInDist = Join-Path $DistDir "sing-box.exe"
if (Test-Path $singBoxInDist) {
    Copy-Item $singBoxInDist $BootstrapDir
    $updateFileCount++
    Write-Host "       sing-box.exe included in update (under _bootstrap/)" -ForegroundColor Gray
}
$cronetInDist = Join-Path $DistDir "libcronet.dll"
if (Test-Path $cronetInDist) {
    Copy-Item $cronetInDist $BootstrapDir
    $updateFileCount++
    Write-Host "       libcronet.dll included in update (under _bootstrap/)" -ForegroundColor Gray
}
$slipInDist = Join-Path $DistDir "slipstream-client.exe"
if (Test-Path $slipInDist) {
    Copy-Item $slipInDist $BootstrapDir
    $updateFileCount++
    Write-Host "       slipstream-client.exe included in update (under _bootstrap/)" -ForegroundColor Gray
    $slipVcrInDist = Join-Path $DistDir "VCRUNTIME140.dll"
    if (Test-Path $slipVcrInDist) { Copy-Item $slipVcrInDist $BootstrapDir; $updateFileCount++ }
}
$driverInDist = Join-Path $DistDir "driver"
if (Test-Path $driverInDist) {
    $UpdateDriverDst = Join-Path $BootstrapDir "driver"
    New-Item -ItemType Directory -Force -Path $UpdateDriverDst | Out-Null
    Copy-Item "$driverInDist\*" $UpdateDriverDst -Recurse -Force
    $stLicInDist = Join-Path $DistDir "LICENSE.split-tunnel"
    if (Test-Path $stLicInDist) { Copy-Item $stLicInDist $BootstrapDir -Force }
    Write-Host "       split-tunnel driver/ included in update (under _bootstrap/)" -ForegroundColor Gray
}
$UpdateProfilesDst = Join-Path $BootstrapDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $UpdateProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $UpdateProfilesDst -Recurse
}
$bootstrapNames = (Get-ChildItem $BootstrapDir -File).Name
foreach ($f in Get-ChildItem $DistDir -File) {
    if ($f.Name -eq "VPNRouter.GUI.exe") { continue }   # trampoline lives at root
    if ($bootstrapNames -contains $f.Name) { continue }  # app DLL / native already copied
    Copy-Item $f.FullName $BootstrapDir -Force
    $updateFileCount++
}
Write-Host "       .NET runtime DLLs included in update (cross-major-safe)" -ForegroundColor Gray
Copy-Item $ReadmePath $BootstrapDir

Write-Host "       Update package: 1 stub at root + $updateFileCount files in _bootstrap/" -ForegroundColor Gray

if (Test-Path $UpdateZipPath) { Remove-Item $UpdateZipPath }
Compress-Archive -Path "$UpdateDir\*" -DestinationPath $UpdateZipPath -CompressionLevel Optimal

Remove-Item -Recurse -Force $FdDir
Remove-Item -Recurse -Force $UpdateDir
Remove-Item -Recurse -Force $PackageDir

$installSize = (Get-Item $InstallZipPath).Length / 1MB
$updateSize = (Get-Item $UpdateZipPath).Length / 1MB

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Install ZIP: $InstallZipPath ($([math]::Round($installSize, 1)) MB)" -ForegroundColor White
Write-Host "Update ZIP:  $UpdateZipPath ($([math]::Round($updateSize, 1)) MB)" -ForegroundColor White

$InstallShaPath = "$InstallZipPath.sha256"
$UpdateShaPath = "$UpdateZipPath.sha256"
(Get-FileHash -Algorithm SHA256 $InstallZipPath).Hash.ToLower() | Set-Content $InstallShaPath -NoNewline
(Get-FileHash -Algorithm SHA256 $UpdateZipPath).Hash.ToLower() | Set-Content $UpdateShaPath -NoNewline
Write-Host "SHA256 (install): $(Get-Content $InstallShaPath)" -ForegroundColor Gray
Write-Host "SHA256 (update):  $(Get-Content $UpdateShaPath)" -ForegroundColor Gray
Write-Host ""

Write-Host "Package contents:" -ForegroundColor Gray
Get-ChildItem $DistDir -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace($DistDir, "").TrimStart("\")
    if ($_.PSIsContainer) { "  $rel\" } else { "  $rel  ($([math]::Round($_.Length/1KB)) KB)" }
}

$AndroidBuilt = $false
$ApkPath = $null
$ApkShaPath = $null

if ($AndroidAlso) {
    Write-Host ""
    Write-Host "=== Android APK build (-AndroidAlso) ===" -ForegroundColor Cyan

    $envLocal = Join-Path $Root ".env.local"
    if (Test-Path $envLocal) {
        Get-Content $envLocal | ForEach-Object {
            if ($_ -match '^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*?)\s*$') {
                $k = $Matches[1]
                $v = $Matches[2].Trim('"').Trim("'")
                if (-not (Get-Item "Env:$k" -ErrorAction SilentlyContinue)) {
                    Set-Item "Env:$k" -Value $v
                }
            }
        }
        Write-Host "       Loaded $envLocal (existing env vars take precedence)" -ForegroundColor Gray
    }

    $issues = @()
    if (-not $env:JAVA_HOME) {
        $issues += "JAVA_HOME not set (point at JDK 17 - e.g. Temurin)"
    } elseif (-not (Test-Path $env:JAVA_HOME)) {
        $issues += "JAVA_HOME points at non-existent path: $env:JAVA_HOME"
    }
    if (-not $env:ANDROID_HOME) {
        $issues += "ANDROID_HOME not set (point at Android SDK root)"
    } elseif (-not (Test-Path $env:ANDROID_HOME)) {
        $issues += "ANDROID_HOME points at non-existent path: $env:ANDROID_HOME"
    }
    try {
        $workloadOutput = & dotnet workload list 2>&1 | Out-String
        if ($workloadOutput -notmatch '(?im)^\s*android\b') {
            $issues += "dotnet workload 'android' not installed (run: dotnet workload install android)"
        }
    } catch {
        $issues += "could not run 'dotnet workload list' to verify Android workload: $_"
    }

    if ($issues.Count -gt 0) {
        Write-Host "Android build SKIPPED - prerequisites missing:" -ForegroundColor Yellow
        foreach ($i in $issues) { Write-Host "  - $i" -ForegroundColor Yellow }
        Write-Host "Windows artifacts above are still valid; only the APK was skipped." -ForegroundColor Yellow
    } else {
        $signingArgs = @()
        $hasKeystore = $false
        $keystoreSource = ""

        if ($env:ANDROID_KEYSTORE_PATH -and $env:ANDROID_KEYSTORE_PASSWORD -and (Test-Path $env:ANDROID_KEYSTORE_PATH)) {
            $keyAlias = if ($env:ANDROID_KEYSTORE_KEY_ALIAS) { $env:ANDROID_KEYSTORE_KEY_ALIAS } else { "vpnrouter" }
            $keyPass  = if ($env:ANDROID_KEYSTORE_KEY_PASSWORD) { $env:ANDROID_KEYSTORE_KEY_PASSWORD } else { $env:ANDROID_KEYSTORE_PASSWORD }
            $signingArgs = @(
                "-p:AndroidSigningKeyStore=$($env:ANDROID_KEYSTORE_PATH)",
                "-p:AndroidSigningStorePass=$($env:ANDROID_KEYSTORE_PASSWORD)",
                "-p:AndroidSigningKeyAlias=$keyAlias",
                "-p:AndroidSigningKeyPass=$keyPass"
            )
            $hasKeystore = $true
            $keystoreSource = "ANDROID_KEYSTORE_PATH"
        } else {
            $csprojKeystore = Join-Path $Root "VPNRouter.Android\vpnrouter.keystore"
            if (Test-Path $csprojKeystore) {
                if ($env:ANDROID_KEYSTORE_PASSWORD) {
                    $keyAlias = if ($env:ANDROID_KEYSTORE_KEY_ALIAS) { $env:ANDROID_KEYSTORE_KEY_ALIAS } else { "vpnrouter" }
                    $keyPass  = if ($env:ANDROID_KEYSTORE_KEY_PASSWORD) { $env:ANDROID_KEYSTORE_KEY_PASSWORD } else { $env:ANDROID_KEYSTORE_PASSWORD }
                    $signingArgs = @(
                        "-p:AndroidSigningStorePass=$($env:ANDROID_KEYSTORE_PASSWORD)",
                        "-p:AndroidSigningKeyAlias=$keyAlias",
                        "-p:AndroidSigningKeyPass=$keyPass"
                    )
                    $hasKeystore = $true
                    $keystoreSource = "VPNRouter.Android\vpnrouter.keystore"
                }
            }
        }

        if (-not $hasKeystore) {
            Write-Host "Android build SKIPPED - no signing keystore available." -ForegroundColor Yellow
            Write-Host "  Provide via env vars:" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_PATH         = path to .keystore / .jks" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_PASSWORD     = store password" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_KEY_ALIAS    = alias (default: vpnrouter)" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_KEY_PASSWORD = key password (default: store password)" -ForegroundColor Yellow
            Write-Host "  Or place the same keys in .env.local at the repo root." -ForegroundColor Yellow
            Write-Host "  Production keystore must match the one used by CI for auto-update to work." -ForegroundColor Yellow
        } else {
            Write-Host "Signing source: $keystoreSource" -ForegroundColor Gray
            Write-Host "Building APK (Release, android-arm64)..." -ForegroundColor Yellow

            $publishArgs = @(
                "publish", "$Root\VPNRouter.Android\VPNRouter.Android.csproj",
                "-c", "Release",
                "-p:RuntimeIdentifiers=android-arm64",
                "-p:AndroidEnableProfiledAot=false"
            ) + $signingArgs

            & dotnet @publishArgs
            $apkExit = $LASTEXITCODE

            if ($apkExit -ne 0) {
                Write-Host "Android build FAILED (dotnet publish exit $apkExit). Windows artifacts still valid." -ForegroundColor Red
            } else {
                $signedApk = Get-ChildItem -Path "$Root\VPNRouter.Android\bin\Release" -Recurse -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
                             Sort-Object LastWriteTime -Descending |
                             Select-Object -First 1
                if (-not $signedApk) {
                    Write-Host "Android build succeeded but no *-Signed.apk found under VPNRouter.Android\bin\Release\." -ForegroundColor Red
                } else {
                    $ApkName = "VPNRouter-v$Version-android-arm64.apk"
                    $ApkPath = Join-Path $Root $ApkName
                    Copy-Item $signedApk.FullName $ApkPath -Force

                    $ApkShaPath = "$ApkPath.sha256"
                    (Get-FileHash -Algorithm SHA256 $ApkPath).Hash.ToLower() | Set-Content $ApkShaPath -NoNewline

                    $apkSize = [math]::Round((Get-Item $ApkPath).Length / 1MB, 1)
                    Write-Host "APK: $ApkPath ($apkSize MB)" -ForegroundColor Green
                    Write-Host "SHA256 (apk): $(Get-Content $ApkShaPath)" -ForegroundColor Gray
                    $AndroidBuilt = $true
                }
            }
        }
    }
}

Write-Host ""
Write-Host "=== Artifacts ===" -ForegroundColor Cyan
Write-Host "  Windows install ZIP : $InstallZipName" -ForegroundColor White
Write-Host "  Windows update ZIP  : $UpdateZipName" -ForegroundColor White
if ($AndroidAlso) {
    if ($AndroidBuilt) {
        Write-Host "  Android APK         : $(Split-Path $ApkPath -Leaf)" -ForegroundColor White
    } else {
        Write-Host "  Android APK         : SKIPPED (see warnings above)" -ForegroundColor Yellow
    }
}

if ($Upload) {
    Write-Host ""
    Write-Host "Uploading to GitHub Releases..." -ForegroundColor Yellow

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install: winget install GitHub.cli"
    } else {
        $tag = "v$Version"

        $releaseAssets = @($InstallZipPath, $UpdateZipPath, $InstallShaPath, $UpdateShaPath)
        if ($AndroidBuilt) {
            $releaseAssets += @($ApkPath, $ApkShaPath)
            Write-Host "       Including local Android APK in release assets" -ForegroundColor Gray
        }

        $currentHead = (& git -C $Root rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $currentHead -ne $releaseCommit) { throw 'Release HEAD changed during build.' }
        $changes = @(& git -C $Root status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Release source changed during build.' }
        $tagCommit = (& gh api "repos/$GitHubRepo/commits/$tag" --jq '.sha' | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $releaseCommit) { throw 'Remote release tag changed during build.' }
        $releaseJson = (& gh release view $tag --repo $GitHubRepo --json isDraft,isPrerelease | Out-String)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot recheck draft release.' }
        $releaseState = $releaseJson | ConvertFrom-Json
        if (-not $releaseState.isDraft -or [bool]$releaseState.isPrerelease -ne ($Version -match '-r[1-9][0-9]*$')) {
            throw 'Release is no longer the expected draft; refusing staging.'
        }
        & gh release upload $tag @releaseAssets --repo $GitHubRepo
        $releaseUploadExitCode = $LASTEXITCODE
        if ($releaseUploadExitCode -ne 0) {
            throw "GitHub draft upload failed (exit $releaseUploadExitCode); inspect partial assets before retrying."
        }
        Write-Host "       Staged on draft $tag; publication requires the complete pre-publish gate." -ForegroundColor Green
    }
}
