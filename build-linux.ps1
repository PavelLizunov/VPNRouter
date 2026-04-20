<#
.SYNOPSIS
    VPNRouter Linux build (cross-compile from Windows).

.DESCRIPTION
    Produces VPNRouter-v<version>-linux.tar.gz via `dotnet publish -r
    linux-x64 --self-contained`. Static asset files (launcher, .desktop,
    README) live in packaging/linux/ so we don't tangle with PowerShell
    here-string escaping for multi-line bash/shell content.

.PARAMETER Version
.PARAMETER Upload
.PARAMETER GitHubRepo
#>
param(
    [Parameter(Mandatory=$true)] [string]$Version,
    [switch]$Upload,
    [string]$GitHubRepo = "PavelLizunov/VPNRouter"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$PublishDir = Join-Path $Root "publish\linux-x64"
$StageDir = Join-Path $Root "publish\linux-stage"
$TarName = "VPNRouter-v$Version-linux.tar.gz"
$TarPath = Join-Path $Root $TarName
$PkgDir  = Join-Path $Root "packaging\linux"

Write-Host "=== VPNRouter Linux Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Output:  $TarPath"
Write-Host ""

Write-Host "[1/6] Clean..." -ForegroundColor Yellow
foreach ($d in @($PublishDir, $StageDir)) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
}
if (Test-Path $TarPath) { Remove-Item -Force $TarPath }

Write-Host "[2/6] dotnet publish VPNRouter.App (linux-x64)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r linux-x64 --self-contained true -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

Write-Host "[3/6] dotnet publish VPNRouter.CLI (shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r linux-x64 --self-contained true -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Write-Host "[4/6] Staging..." -ForegroundColor Yellow
$AppDir = Join-Path $StageDir "VPNRouter"
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $AppDir -Recurse -Force

# Copy static asset files from packaging/linux/. The README contains a
# {VERSION} placeholder that we substitute here.
foreach ($asset in @("VPNRouter.sh", "vpnrouter.desktop")) {
    $src = Join-Path $PkgDir $asset
    if (Test-Path $src) { Copy-Item -Path $src -Destination (Join-Path $AppDir $asset) -Force }
    else { Write-Host "    WARN: packaging asset missing: $src" -ForegroundColor Red }
}

$readmeSrc = Join-Path $PkgDir "README.txt"
if (Test-Path $readmeSrc) {
    $readmeText = [System.IO.File]::ReadAllText($readmeSrc)
    $readmeText = $readmeText.Replace("{VERSION}", $Version).Replace("{REPO}", $GitHubRepo)
    $readmePath = Join-Path $AppDir "README.txt"
    [System.IO.File]::WriteAllText($readmePath, $readmeText.Replace("`r`n", "`n"),
        [System.Text.UTF8Encoding]::new($false))
}

# Icon from Avalonia Assets (same mascot used by subheader + chrome).
$iconSource = Join-Path $Root "VPNRouter.App\Assets\penguin_mascot.png"
if (Test-Path $iconSource) {
    Copy-Item -Path $iconSource -Destination (Join-Path $AppDir "icon.png") -Force
}

Write-Host "    Layout:"
Get-ChildItem $AppDir | ForEach-Object { Write-Host "      $($_.Name)" }

Write-Host "[5/6] tar.gz..." -ForegroundColor Yellow
Push-Location $StageDir
try {
    # Use Windows built-in tar.exe explicitly. Git Bash / MSYS2 PATHs
    # often put a Cygwin tar first which treats C:\... as a remote host
    # spec ("Cannot connect to C: resolve failed"). System32\tar.exe
    # (bsdtar, shipped since Win10 1803) handles native paths correctly.
    $windowsTar = Join-Path $env:SystemRoot "System32\tar.exe"
    if (-not (Test-Path $windowsTar)) { $windowsTar = "tar" }
    & $windowsTar -czf $TarPath VPNRouter
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
} finally { Pop-Location }

Write-Host "    Size: $([math]::Round((Get-Item $TarPath).Length / 1MB, 1)) MB"
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $TarPath).Hash.ToLower()
$shaPath = "$TarPath.sha256"
"$sha256  $TarName" | Out-File -FilePath $shaPath -Encoding ASCII -NoNewline
Write-Host "    SHA256: $sha256"

if ($Upload) {
    Write-Host "[6/6] gh release upload..." -ForegroundColor Yellow
    gh release view "v$Version" --repo $GitHubRepo --json tagName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    Release v$Version not found — run build.ps1 -Upload first." -ForegroundColor Red
        exit 1
    }
    gh release upload "v$Version" $TarPath $shaPath --repo $GitHubRepo --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }
    Write-Host "    Uploaded." -ForegroundColor Green
} else {
    Write-Host "[6/6] Skipping upload." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done: $TarPath" -ForegroundColor Green
