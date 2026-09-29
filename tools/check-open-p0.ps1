[CmdletBinding()]
param([string]$Waive = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ledger = Join-Path $repoRoot 'plans/OPEN-DEFECTS.md'

if (-not (Test-Path -LiteralPath $ledger)) {
    Write-Host "[check-open-p0] BLOCK: required ledger missing at $ledger."
    exit 3
}

$lines = Get-Content -LiteralPath $ledger -Encoding UTF8
$openHeadings = @($lines | Where-Object { $_ -match '(?i)^##\s+Open\b' })
if ($openHeadings.Count -ne 1) {
    Write-Host "[check-open-p0] BLOCK: ledger must contain exactly one Open section (found $($openHeadings.Count))."
    exit 3
}
$inOpen = $false
$open = @()
foreach ($line in $lines) {
    if ($line -match '^##\s+') {
        $inOpen = ($line -match '(?i)^##\s+Open\b')
        continue
    }
    if ($inOpen -and ($line -match '^\s*-\s*\[\s\]\s') -and ($line -match '\*\*P[01]\b[^*]*\*\*')) {
        $open += $line.Trim()
    }
}

if ($open.Count -eq 0) {
    Write-Host "[check-open-p0] OK: no open P0/P1 in plans/OPEN-DEFECTS.md."
    exit 0
}

Write-Host "[check-open-p0] $($open.Count) OPEN defect(s) in plans/OPEN-DEFECTS.md:"
foreach ($o in $open) { Write-Host "  $o" }

if (-not [string]::IsNullOrWhiteSpace($Waive)) {
    Write-Host ''
    Write-Host "[check-open-p0] WAIVED for this cut: $Waive"
    Write-Host "[check-open-p0] (proceed only if each open item above is genuinely out of THIS cut's scope)"
    exit 0
}

Write-Host ''
Write-Host "[check-open-p0] BLOCK: fix each (set '- [x]' + 'RESOLVED vX.Y.Z' in the ledger)"
Write-Host "[check-open-p0]        or re-run with -Waive '<reason>' to consciously defer them."
exit 2
