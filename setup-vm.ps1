<#
.SYNOPSIS
    Bootstrap a Windows 11 VirtualBox guest for VPNRouter development.

.DESCRIPTION
    Runs on a fresh Windows VM. Installs .NET 8 SDK, Go, Git, GitHub CLI,
    Claude Code, and 7-Zip via winget; adds Windows Defender exclusions for
    VPNRouter paths; clones the VPNRouter repo; verifies GitHub / Forgejo
    network reachability; runs an initial dotnet build.

    Must be run as Administrator.

.PARAMETER RepoDir
    Target directory for git clone. Default: C:\Project\VPNRouter

.PARAMETER RepoUrl
    HTTPS URL to clone from. Default: public GitHub mirror.

.PARAMETER GitUser
    Optional. Sets git user.name globally.

.PARAMETER GitEmail
    Optional. Sets git user.email globally.

.PARAMETER SkipDefender
    Skip adding Defender exclusions.

.PARAMETER SkipWinget
    Skip winget install step (assume tools already installed).

.PARAMETER SkipBuild
    Skip dotnet restore + build (useful for first run before PATH refresh).

.EXAMPLE
    # Basic run
    .\setup-vm.ps1

.EXAMPLE
    # With git identity, no initial build
    .\setup-vm.ps1 -GitUser "Jane Doe" -GitEmail "jane@example.com" -SkipBuild

.NOTES
    See README-VM.md for the full VM setup walkthrough (VirtualBox settings,
    Guest Additions, SSH keys for Forgejo, etc.).
#>
param(
    [string]$RepoDir  = "C:\Project\VPNRouter",
    [string]$RepoUrl  = "https://github.com/PavelLizunov/VPNRouter.git",
    [string]$GitUser  = "",
    [string]$GitEmail = "",
    [switch]$SkipDefender,
    [switch]$SkipWinget,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------
function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Section($msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

function Install-WingetPackage($id, $name) {
    Write-Host "  - $name ($id)"
    & winget install --id $id --silent `
        --accept-source-agreements --accept-package-agreements `
        --disable-interactivity
    # winget exit codes we treat as non-fatal:
    #   0                 - installed OK
    #   -1978335189       - APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE (already installed)
    #   -1978335212       - APPINSTALLER_CLI_ERROR_NO_APPLICABLE_UPGRADE
    $nonFatal = @(0, -1978335189, -1978335212)
    if ($nonFatal -notcontains $LASTEXITCODE) {
        Write-Warning "    winget returned exit code $LASTEXITCODE for $id - continuing"
    }
}

function Test-Endpoint($hostname, $port, $label) {
    $ok = $false
    try {
        $ok = Test-NetConnection -ComputerName $hostname -Port $port `
              -InformationLevel Quiet -WarningAction SilentlyContinue
    } catch { }
    if ($ok) {
        Write-Host ("  OK   {0} ({1}:{2})" -f $label, $hostname, $port) `
            -ForegroundColor Green
    } else {
        Write-Host ("  FAIL {0} ({1}:{2}) - not reachable" -f $label, $hostname, $port) `
            -ForegroundColor Yellow
    }
}

# -----------------------------------------------------------------------------
# Preflight
# -----------------------------------------------------------------------------
if (-not (Test-Admin)) {
    Write-Host "[!] This script must run as Administrator." -ForegroundColor Red
    Write-Host "    Open an elevated PowerShell, then:"
    Write-Host "      powershell -ExecutionPolicy Bypass -File .\setup-vm.ps1"
    exit 1
}

Write-Host "VPNRouter VM setup" -ForegroundColor Cyan
Write-Host "  Target repo dir: $RepoDir"
Write-Host "  Clone from:      $RepoUrl"

# -----------------------------------------------------------------------------
# 1. Install dev tools via winget
# -----------------------------------------------------------------------------
if (-not $SkipWinget) {
    Write-Section "Installing dev tools via winget"

    if (-not (Test-Command winget)) {
        Write-Host "[!] winget is not available." -ForegroundColor Red
        Write-Host "    Install 'App Installer' from Microsoft Store and re-run."
        exit 1
    }

    Install-WingetPackage "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
    Install-WingetPackage "GoLang.Go"              "Go"
    Install-WingetPackage "Git.Git"                "Git for Windows"
    Install-WingetPackage "GitHub.cli"             "GitHub CLI"
    Install-WingetPackage "Anthropic.Claude"       "Claude Code"
    Install-WingetPackage "7zip.7zip"              "7-Zip"

    # Refresh PATH in this session so later commands (git, dotnet) are found.
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path","User")
}

# -----------------------------------------------------------------------------
# 2. Windows Defender exclusions
# -----------------------------------------------------------------------------
if (-not $SkipDefender) {
    Write-Section "Adding Windows Defender exclusions"
    $paths = @(
        $RepoDir,
        "C:\ProgramData\VPNRouter"
    )
    foreach ($path in $paths) {
        Write-Host "  - $path"
        try {
            Add-MpPreference -ExclusionPath $path -ErrorAction Stop
        } catch {
            Write-Warning "    Could not add exclusion for $path : $($_.Exception.Message)"
        }
    }
}

# -----------------------------------------------------------------------------
# 3. Clone repo
# -----------------------------------------------------------------------------
Write-Section "Cloning VPNRouter repo"

if (-not (Test-Command git)) {
    Write-Host "[!] git not found on PATH. Close this PowerShell window," `
        -ForegroundColor Red
    Write-Host "    open a new one (to pick up PATH changes), and re-run."
    exit 1
}

$parent = Split-Path -Parent $RepoDir
if (-not (Test-Path $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

if (Test-Path (Join-Path $RepoDir ".git")) {
    Write-Host "  Repo already present at $RepoDir - running git pull"
    git -C $RepoDir pull
} else {
    git clone $RepoUrl $RepoDir
}

# -----------------------------------------------------------------------------
# 4. Git identity (global)
# -----------------------------------------------------------------------------
if ($GitUser -or $GitEmail) {
    Write-Section "Configuring git identity (global)"
    if ($GitUser)  {
        git config --global user.name  $GitUser
        Write-Host "  user.name  = $GitUser"
    }
    if ($GitEmail) {
        git config --global user.email $GitEmail
        Write-Host "  user.email = $GitEmail"
    }
}

# -----------------------------------------------------------------------------
# 5. Network connectivity check
# -----------------------------------------------------------------------------
Write-Section "Verifying network connectivity"
Test-Endpoint "github.com"     443   "GitHub (HTTPS)"
Test-Endpoint "api.github.com" 443   "GitHub API"
Test-Endpoint "10.9.1.1"       18222 "Forgejo SSH (via AmneziaWG)"
Test-Endpoint "10.9.1.1"       18300 "Forgejo Web UI (via AmneziaWG)"

# -----------------------------------------------------------------------------
# 6. First build
# -----------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Section "dotnet restore + build"
    if (-not (Test-Command dotnet)) {
        Write-Host "[!] dotnet not on PATH yet." -ForegroundColor Yellow
        Write-Host "    Close this PowerShell, open a new elevated one, then:"
        Write-Host "      .\setup-vm.ps1 -SkipWinget -SkipDefender"
    } else {
        Push-Location $RepoDir
        try {
            dotnet restore VPNRouter.sln
            dotnet build VPNRouter.sln --configuration Release
        } finally {
            Pop-Location
        }
    }
}

# -----------------------------------------------------------------------------
# Done
# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. gh auth login"
Write-Host "       - Authenticate GitHub CLI for release uploads."
Write-Host "  2. Copy your Forgejo SSH key into ~\.ssh\ (id_ed25519 + .pub)."
Write-Host "       - Required only if you plan to push to Forgejo over VPN."
Write-Host "  3. ssh -T git@10.9.1.1 -p 18222"
Write-Host "       - Verify Forgejo SSH. Requires AmneziaWG connected on the host."
Write-Host "  4. cd $RepoDir; .\build.ps1 -Version test"
Write-Host "       - First full build (shared runtime + 3 zip artifacts)."
Write-Host ""
Write-Host "See README-VM.md for the full walkthrough."
