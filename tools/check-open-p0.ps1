# tools/check-open-p0.ps1 — audit item 7 (2026-06-25): the open-defect-ledger
# gate for cut-stable. Scans the "## Open" section of plans/OPEN-DEFECTS.md for
# unresolved "- [ ]" P0/P1 entries and BLOCKS the stable cut (exit 2) unless the
# operator passes -Waive '<reason>' for this specific cut.
#
# Why: a deferred bug-hunt P0 reached v2.44.0/.1 stable and bit a user
# (auto-failover teardown, diag 20260624-235243) because nothing connected the
# defect ledger to the cut gate. The cut-stable skill (pre-flight 6.5) runs this.
#
# Usage:
#   pwsh tools/check-open-p0.ps1                 # exit 0 clean / exit 2 if open
#   pwsh tools/check-open-p0.ps1 -Waive 'reason' # exit 0, records the waiver line
[CmdletBinding()]
param([string]$Waive = '')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ledger = Join-Path $repoRoot 'plans/OPEN-DEFECTS.md'

if (-not (Test-Path -LiteralPath $ledger)) {
    Write-Host "[check-open-p0] OK: no ledger at $ledger - nothing to gate."
    exit 0
}

$lines = Get-Content -LiteralPath $ledger -Encoding UTF8
$inOpen = $false
$open = @()
foreach ($line in $lines) {
    if ($line -match '^##\s+') {
        # Section boundary: only the "## Open" section is gated.
        $inOpen = ($line -match '(?i)^##\s+Open\b')
        continue
    }
    if ($inOpen -and ($line -match '^\s*-\s*\[\s\]\s') -and ($line -match '\*\*P[01]\*\*')) {
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
