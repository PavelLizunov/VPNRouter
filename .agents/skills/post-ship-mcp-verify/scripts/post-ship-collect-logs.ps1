# post-ship-collect-logs.ps1 - Phase 5 of post-ship-mcp-verify skill.
# Tails the most-recent vpnrouter*.log + scans for known-bad patterns,
# returns a structured report to stdout.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .agents/skills/post-ship-mcp-verify/scripts/post-ship-collect-logs.ps1
#
# Output: prints any matching lines with [ERR]/Exception/FATAL/crashed,
# then a single-line summary. Empty body = no issues.

param(
    [string]$LogDir = "C:\ProgramData\VPNRouter\logs",
    [int]$TailLines = 200,
    [string]$BadPatternRegex = "\[ERR\]|\bException\b|FATAL|crashed|Bug-r9-G|Cannot create|Access is denied"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $LogDir)) {
    Write-Host "ERROR: log dir $LogDir not found." -ForegroundColor Red
    exit 1
}

# Find the most-recent vpnrouter*.log file.
$latest = Get-ChildItem -Path $LogDir -Filter "vpnrouter*.log" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latest) {
    Write-Host "WARN: no vpnrouter*.log in $LogDir" -ForegroundColor Yellow
    exit 0
}

Write-Host "Scanning $($latest.FullName) (last $TailLines lines)..." -ForegroundColor Cyan
Write-Host ""

# Logs are UTF-16 LE. Use -Encoding Unicode.
$content = Get-Content $latest.FullName -Encoding Unicode -Tail $TailLines

# Surface matches with 1 line of context above + below.
$matches = @()
for ($i = 0; $i -lt $content.Count; $i++) {
    if ($content[$i] -match $BadPatternRegex) {
        $matches += [PSCustomObject]@{
            Line = $i
            Content = $content[$i]
            Before = if ($i -gt 0) { $content[$i-1] } else { "" }
            After = if ($i -lt $content.Count - 1) { $content[$i+1] } else { "" }
        }
    }
}

if ($matches.Count -eq 0) {
    Write-Host "CLEAN: no matches for known-bad patterns in last $TailLines lines." -ForegroundColor Green
    exit 0
}

Write-Host "FOUND $($matches.Count) suspicious line(s):" -ForegroundColor Yellow
Write-Host ""
foreach ($m in $matches) {
    if ($m.Before) { Write-Host "  ... $($m.Before)" -ForegroundColor DarkGray }
    Write-Host "  >>> $($m.Content)" -ForegroundColor Yellow
    if ($m.After) { Write-Host "  ... $($m.After)" -ForegroundColor DarkGray }
    Write-Host ""
}

Write-Host "SUMMARY: $($matches.Count) suspicious matches in last $TailLines lines of $($latest.Name)" -ForegroundColor Yellow
Write-Host "Some are expected (Bug-r9-G AV-toast suppression noise); inspect each in context." -ForegroundColor DarkGray
exit 0
