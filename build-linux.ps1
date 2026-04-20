<#
.SYNOPSIS
    VPNRouter Linux build script (cross-compile from Windows).

.DESCRIPTION
    Produces VPNRouter-v<version>-linux.tar.gz — a self-contained Linux x64
    build of the Avalonia app. Layout follows the same idea as the macOS
    build: everything under a top-level VPNRouter/ directory, launcher
    script at VPNRouter/VPNRouter.sh, icon + .desktop file for menu
    integration.

    Runs on Windows 10+ via dotnet publish -r linux-x64 (cross-compile is
    first-class for .NET 8, no Linux needed to BUILD). tar.exe on modern
    Windows handles the tar.gz packaging.

    NOTE: AppImage packaging is NOT done here — appimagetool is Linux-only.
    For v2.21.0 BETA we ship tar.gz only. AppImage in v2.21.1+ via a
    GitHub Actions Linux runner.

.PARAMETER Version
    Version string for the tarball filename.

.PARAMETER Upload
    Upload the tarball to GitHub Releases using gh CLI.

.PARAMETER GitHubRepo
    GitHub repo in "owner/repo" format (default: PavelLizunov/VPNRouter).

.EXAMPLE
    .\build-linux.ps1 -Version "2.21.0"
    .\build-linux.ps1 -Version "2.21.0" -Upload
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$Version,
    [switch]$Upload,
    [string]$GitHubRepo = "PavelLizunov/VPNRouter"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$PublishDir = Join-Path $Root "publish\linux-x64"
$StageDir = Join-Path $Root "publish\linux-stage"
$TarName = "VPNRouter-v$Version-linux.tar.gz"
$TarPath = Join-Path $Root $TarName

Write-Host "=== VPNRouter Linux Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Output:  $TarPath"
Write-Host ""

# ── Clean ──
Write-Host "[1/6] Cleaning previous build..." -ForegroundColor Yellow
foreach ($dir in @($PublishDir, $StageDir)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
if (Test-Path $TarPath) { Remove-Item -Force $TarPath }

# ── Publish ──
# --self-contained bundles .NET 8 runtime; user doesn't need dotnet installed.
# PublishSingleFile=false because Avalonia loads native dependencies at
# runtime (libHarfBuzzSharp, libSkiaSharp) that break single-file extraction
# on some minimal distros. Multi-file layout is more forgiving.
Write-Host "[2/6] Publishing VPNRouter.App (linux-x64, self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r linux-x64 --self-contained true `
    -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

# VPNRouter.CLI + Service reuse the same App payload layout — drop them in
# the same dir so shared runtime DLLs aren't duplicated.
Write-Host "[3/6] Publishing VPNRouter.CLI (shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r linux-x64 --self-contained true `
    -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

# ── Stage ──
# Top-level layout:
#   VPNRouter/
#     VPNRouter.App            <- the actual Linux binary
#     VPNRouter.CLI            <- CLI entrypoint
#     *.dll / native .so       <- runtime dependencies
#     VPNRouter.sh             <- launcher script (chmod +x)
#     vpnrouter.desktop        <- menu integration
#     icon.png                 <- launcher icon (from penguin_mascot.png)
Write-Host "[4/6] Staging archive contents..." -ForegroundColor Yellow
$AppDir = Join-Path $StageDir "VPNRouter"
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $AppDir -Recurse -Force

# Launcher script — handles $0 resolution so it works from any cwd
$launcherContent = @'
#!/usr/bin/env bash
# VPNRouter launcher — resolves the script's own directory so the app can be
# unpacked anywhere (~/VPNRouter, /opt/vpnrouter, etc.) and still locate its
# sibling DLLs.
SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
exec "$SCRIPT_DIR/VPNRouter.App" "$@"
'@
$launcherPath = Join-Path $AppDir "VPNRouter.sh"
# Use -NoNewline to avoid BOM/CRLF that breaks bash shebangs
[System.IO.File]::WriteAllText($launcherPath, $launcherContent.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

# .desktop file for menu integration (user copies to ~/.local/share/applications/)
$desktopContent = @"
[Desktop Entry]
Type=Application
Name=VPNRouter
Comment=Virtual Penguin Network — process-based split-tunnel VPN
Exec=VPNRouter.sh %U
Icon=vpnrouter
Terminal=false
Categories=Network;
StartupNotify=true
"@
$desktopPath = Join-Path $AppDir "vpnrouter.desktop"
[System.IO.File]::WriteAllText($desktopPath, $desktopContent.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

# Icon — reuse the 640×640 mascot PNG; let users / distros resize.
$iconSource = Join-Path $Root "VPNRouter.App\Assets\penguin_mascot.png"
if (Test-Path $iconSource) {
    Copy-Item -Path $iconSource -Destination (Join-Path $AppDir "icon.png") -Force
}

# README with installation + elevation notes
$readmeContent = @"
VPNRouter v$Version (Linux BETA)
================================

First-time setup:

1. Extract:
     tar -xzf VPNRouter-v$Version-linux.tar.gz
     cd VPNRouter

2. Make launcher executable (tar should preserve this, but just in case):
     chmod +x VPNRouter.sh VPNRouter.App VPNRouter.CLI

3. Run:
     ./VPNRouter.sh

4. Optional — menu entry:
     cp vpnrouter.desktop ~/.local/share/applications/
     cp icon.png ~/.local/share/icons/hicolor/256x256/apps/vpnrouter.png
     update-desktop-database ~/.local/share/applications/

Elevation
---------
VPNRouter runs the sing-box proxy process as root (needed for TUN mode
routing). It uses pkexec to show a GUI password prompt via your desktop
environment's polkit agent (GNOME / KDE / XFCE / Cinnamon all include
one by default).

If pkexec isn't available on your system, grant sing-box the required
capability once:
     sudo setcap cap_net_admin,cap_net_bind_service=+eip \$(pwd)/bin/sing-box

Then VPNRouter will launch sing-box without a password prompt.

GNOME users — tray icon
------------------------
GNOME doesn't show system-tray icons by default. Install the
"AppIndicator and KStatusNotifierItem Support" extension from
https://extensions.gnome.org/extension/615/appindicator-support/ to see
the VPNRouter tray icon. KDE / XFCE / Cinnamon work out of the box.

sing-box binary
---------------
The first time you Connect, VPNRouter downloads sing-box-linux-amd64
into ~/.config/vpnrouter/bin/. About 25 MB one-time download.

What's not in this BETA
-----------------------
  * Zapret DPI bypass (Windows-only for now — winws.exe via Cygwin)
  * Telegram proxy (Python-embeddable path is Windows-only)
  * systemd service / boot autostart (session autostart via .desktop works)
  * Auto-update (download new tarball manually for now)

Questions / bugs
----------------
https://github.com/$GitHubRepo/issues

"@
$readmePath = Join-Path $AppDir "README.txt"
[System.IO.File]::WriteAllText($readmePath, $readmeContent.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))

Write-Host "    Layout:"
Get-ChildItem $AppDir | ForEach-Object { Write-Host "      $($_.Name)" }

# ── Package ──
# tar.exe on Windows 10+ supports -czf out of the box. --posix keeps the
# archive portable and preserves long paths. Execute bit on .sh is set
# explicitly inside the archive post-hoc is not trivial from PowerShell,
# so the README instructs users to run `chmod +x VPNRouter.sh` if needed.
# (Actually tar.exe should preserve the +x if the source file is marked
# executable on NTFS via cygwin-style attrs — we don't control that from
# PS1. Manual chmod in README is the safe fallback.)
Write-Host "[5/6] Packaging tar.gz..." -ForegroundColor Yellow
Push-Location $StageDir
try {
    # Use -C so the archive has a clean top-level VPNRouter/ folder
    tar -czf $TarPath VPNRouter
    if ($LASTEXITCODE -ne 0) { throw "tar packaging failed" }
}
finally {
    Pop-Location
}

Write-Host "    Size: $([math]::Round((Get-Item $TarPath).Length / 1MB, 1)) MB"

# SHA256 sidecar
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $TarPath).Hash.ToLower()
$shaPath = "$TarPath.sha256"
"$sha256  $TarName" | Out-File -FilePath $shaPath -Encoding ASCII -NoNewline
Write-Host "    SHA256: $sha256"

# ── Upload ──
if ($Upload) {
    Write-Host "[6/6] Uploading to GitHub Releases..." -ForegroundColor Yellow
    # gh release create accepts an existing tag with --notes "" to attach
    # assets; if tag doesn't exist yet it creates one.
    $existing = gh release view "v$Version" --repo $GitHubRepo --json tagName 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        gh release upload "v$Version" $TarPath $shaPath --repo $GitHubRepo --clobber
    }
    else {
        Write-Host "    Release v$Version not found — upload skipped. Create it first via build.ps1 -Upload." -ForegroundColor Red
        exit 1
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }
    Write-Host "    Uploaded." -ForegroundColor Green
}
else {
    Write-Host "[6/6] Skip upload (no -Upload flag)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done: $TarPath" -ForegroundColor Green
