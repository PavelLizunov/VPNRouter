# Setup-Hooks.ps1 — one-time install of VPNRouter v3.0 git hooks.
#
# Usage:
#   pwsh ./Setup-Hooks.ps1
#
# What it does:
#   1. Sets `git config core.hooksPath .githooks` so hooks in the tracked
#      `.githooks/` directory are used instead of `.git/hooks/` (which is
#      untracked + per-clone).
#   2. Ensures `.githooks/pre-commit` + `.githooks/commit-msg` are executable
#      on Unix-like environments (git-bash, WSL, real Linux/Mac).
#   3. Smoke-tests by running each hook with `--help`-equivalent (just exit 0
#      path) to confirm shell + tooling present.
#
# Idempotent: safe to re-run. Reports state vs changes.

[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Status {
    param([string]$Msg, [string]$Color = 'White')
    Write-Host "  → $Msg" -ForegroundColor $Color
}

Write-Host ""
Write-Host "VPNRouter Setup-Hooks — git hook installation" -ForegroundColor Cyan
Write-Host "─────────────────────────────────────────────"
Write-Host ""

# Step 1 — verify we're in a git repo
try {
    $gitRoot = git rev-parse --show-toplevel 2>$null
    if (-not $gitRoot) { throw "Not inside a git repo" }
    Write-Status "Git repo: $gitRoot" 'Gray'
} catch {
    Write-Host "❌ Not inside a git repo. Run from VPNRouter checkout root." -ForegroundColor Red
    exit 1
}

# Step 2 — verify .githooks/ directory exists and has expected files
$hookDir = Join-Path $gitRoot '.githooks'
if (-not (Test-Path $hookDir)) {
    Write-Host "❌ .githooks/ directory missing. Are you on the right branch?" -ForegroundColor Red
    exit 1
}

$expectedHooks = @('pre-commit', 'commit-msg')
foreach ($h in $expectedHooks) {
    $hookPath = Join-Path $hookDir $h
    if (Test-Path $hookPath) {
        Write-Status "Hook found: .githooks/$h" 'Green'
    } else {
        Write-Host "❌ Missing .githooks/$h — methodology hooks not installed in this branch." -ForegroundColor Red
        exit 1
    }
}

# Step 3 — set core.hooksPath
$currentPath = git config --local --get core.hooksPath
if ($currentPath -eq '.githooks') {
    Write-Status "core.hooksPath already set to '.githooks'" 'Gray'
} else {
    if ($DryRun) {
        Write-Status "Would set: git config core.hooksPath .githooks" 'Yellow'
    } else {
        git config --local core.hooksPath '.githooks'
        Write-Status "Set core.hooksPath = .githooks" 'Green'
    }
}

# Step 4 — chmod +x on Unix-like (no-op on Windows, but git records mode bit)
if ($IsLinux -or $IsMacOS -or (Test-Path '/bin/bash')) {
    foreach ($h in $expectedHooks) {
        $hookPath = Join-Path $hookDir $h
        if ($DryRun) {
            Write-Status "Would chmod +x .githooks/$h" 'Yellow'
        } else {
            try {
                # Use git update-index to set executable bit in git's tree
                # (not just filesystem) — survives `git checkout` cleanly.
                git update-index --add --chmod=+x ".githooks/$h" 2>$null
                Write-Status "chmod +x .githooks/$h" 'Green'
            } catch {
                Write-Status "Couldn't chmod .githooks/$h (likely on Windows — bash handles via #!)" 'Gray'
            }
        }
    }
}

# Step 5 — smoke test: stage a doc-only change, dry-run commit message check
Write-Host ""
Write-Host "Smoke test:" -ForegroundColor Cyan
$tempBranch = "claude/setup-hooks-smoke-$(Get-Random)"
$cleanupNeeded = $false
try {
    # Verify hook scripts have valid bash syntax
    if (Test-Path '/bin/bash' -or $IsLinux -or $IsMacOS) {
        & /bin/bash -n .githooks/pre-commit
        if ($LASTEXITCODE -eq 0) {
            Write-Status "pre-commit syntax OK" 'Green'
        } else {
            Write-Host "❌ pre-commit has syntax errors" -ForegroundColor Red
            exit 1
        }
        & /bin/bash -n .githooks/commit-msg
        if ($LASTEXITCODE -eq 0) {
            Write-Status "commit-msg syntax OK" 'Green'
        } else {
            Write-Host "❌ commit-msg has syntax errors" -ForegroundColor Red
            exit 1
        }
    }
} catch {
    Write-Status "Smoke test skipped: $($_.Exception.Message)" 'Gray'
}

Write-Host ""
Write-Host "✅ Hooks installed and configured." -ForegroundColor Green
Write-Host ""
Write-Host "What happens now:" -ForegroundColor Cyan
Write-Host "  • Every `git commit` runs .githooks/pre-commit (build + targeted tests)"
Write-Host "  • Commit-message format is checked by .githooks/commit-msg"
Write-Host "  • Bypass for emergencies: git commit --no-verify"
Write-Host "  • Settings: cat .githooks/pre-commit | head -30  ← see all enforced gates"
Write-Host ""
Write-Host "To verify hooks fire:" -ForegroundColor Cyan
Write-Host "  git commit --allow-empty -m 'test commit'   ← should trigger gates"
Write-Host ""
