#!/usr/bin/env bash
# check-methodology.sh — runs all meta-tests from
# plans/android-development-methodology.md. Exits non-zero if any
# methodology rule is violated. Hooked into pre-push (manually for now).
#
# Usage:
#   bash tools/check-methodology.sh            # run all
#   bash tools/check-methodology.sh --quick    # skip slow checks (#5, #8)
#
# Each check echoes [PASS] / [WARN] / [FAIL] + short reason.
# WARNs do not fail the script; FAILs do.

set -u
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"

FAILS=0
WARNS=0
QUICK=0
[ "${1:-}" = "--quick" ] && QUICK=1

note() { echo "[$1] $2"; }
pass() { note "PASS" "$1"; }
warn() { note "WARN" "$1"; WARNS=$((WARNS+1)); }
fail() { note "FAIL" "$1"; FAILS=$((FAILS+1)); }

# ─── Meta-test 0: project state references current ────────────────────
echo "── #0: project state freshness ──"
if [ -f VPNRouter.Core/AppVersion.cs ]; then
  pass "AppVersion.cs exists"
else
  fail "AppVersion.cs missing — methodology references stale state"
fi
# Phase-0 build flag check — flag can appear as 'EnableAndroidTarget'
# property OR as a Condition gate in TargetFrameworks. Either is OK.
if grep -qE "EnableAndroidTarget|net8\.0-android" VPNRouter.Android/VPNRouter.Android.csproj 2>/dev/null; then
  pass "phase-0 Android build target still wired in csproj"
else
  warn "phase-0 Android target missing — methodology may be stale"
fi

# ─── Meta-test 1: process compliance ──────────────────────────────────
echo "── #1: process compliance (test markers) ──"
android_tests=$(find VPNRouter.Tests -name "Android*Tests.cs" 2>/dev/null)
if [ -z "$android_tests" ]; then
  warn "no Android*Tests.cs files yet — Phase 0 expected"
else
  miss=0
  for f in $android_tests; do
    # Test files should have either [Category("...")] or [Trait("Phase...", "...")]
    if ! head -50 "$f" | grep -qE "(Category|Trait)\s*\("; then
      warn "$f lacks [Category(...)] or [Trait(...)] attribute"
      miss=$((miss+1))
    fi
  done
  [ "$miss" = "0" ] && pass "all Android test files have category markers"
fi

# ─── Meta-test 2: architectural drift ─────────────────────────────────
echo "── #2: architectural drift ──"
last_arch_review=$(git log --since="3 months ago" --format=%H -- \
  plans/android-development-methodology.md 2>/dev/null | head -1)
if [ -z "$last_arch_review" ]; then
  warn "methodology not touched in 90 days — architecture may be drifting"
else
  pass "methodology touched within last 90 days"
fi

# ─── Meta-test 3: test categorization completeness ────────────────────
echo "── #3: test categorization ──"
# Skip if no Android tests yet (Phase 0 state)
if [ -n "$android_tests" ]; then
  for f in $android_tests; do
    cat=$(grep -oE "\[(Category|Trait)\(\"([^\"]+)\"" "$f" | head -1)
    if [ -z "$cat" ]; then
      fail "$f missing [Category] / [Trait]"
    fi
  done
  pass "all Android test files have valid categories (or warns above)"
fi

# ─── Meta-test 4: MCP usage trace (informational) ─────────────────────
echo "── #4: MCP usage trace ──"
android_commits=$(git log --since="1 week ago" --oneline -- VPNRouter.Android/ 2>/dev/null | wc -l)
if [ "$android_commits" -gt 0 ]; then
  # Lenient: just count, don't fail
  pass "$android_commits Android commits in past week — manual review of MCP attribution"
else
  pass "no Android commits in past week — no MCP trace audit needed"
fi

# ─── Meta-test 5: performance baseline freshness ──────────────────────
echo "── #5: performance baseline freshness ──"
if [ "$QUICK" = "1" ]; then
  warn "skipped (--quick mode)"
else
  baselines_dir=VPNRouter.Tests/perf-baselines
  if [ ! -d "$baselines_dir" ]; then
    warn "perf-baselines directory missing — Phase 1+ should add"
  else
    stale=$(find "$baselines_dir" -name "*.json" -mtime +90 2>/dev/null | wc -l)
    if [ "$stale" -gt 0 ]; then
      warn "$stale baseline(s) >90d old — verify still representative"
    else
      pass "all baselines fresh (<90d)"
    fi
  fi
fi

# ─── Meta-test 6: anti-fitted-to-fit (manual checklist) ───────────────
echo "── #6: anti-fitted-to-fit (PR template check) ──"
if grep -q "Anti-fitted-to-fit" .github/pull_request_template.md 2>/dev/null; then
  pass "PR template references anti-fit checklist"
else
  warn "PR template does not embed anti-fit checklist (Phase 1 TODO)"
fi

# ─── Meta-test 7: phase progress documented ───────────────────────────
echo "── #7: phase progress ──"
# Doc should have one '✓' or 'done' per completed phase. If new phase
# work is committed but doc says 'next', that's drift.
if grep -qE "Phase 1.*next" plans/android-development-methodology.md 2>/dev/null; then
  # Check if there are any Phase 1 commits in last 30 days
  phase1_commits=$(git log --since="30 days ago" --grep="Phase 1" --oneline 2>/dev/null | wc -l)
  if [ "$phase1_commits" -gt 0 ]; then
    warn "doc says Phase 1 next but $phase1_commits Phase 1 commits exist — update phase status"
  else
    pass "no Phase 1 work yet — doc accurate"
  fi
else
  pass "phase status section adapts as work progresses"
fi

# ─── Meta-test 8: toolchain reproducibility ────────────────────────────
echo "── #8: toolchain bootstrap ──"
if [ "$QUICK" = "1" ]; then
  warn "skipped (--quick mode)"
else
  if [ -f tools/android-bootstrap.ps1 ]; then
    pass "android-bootstrap.ps1 exists"
  else
    warn "android-bootstrap.ps1 missing — Phase 1 TODO (toolchain documented in handbook)"
  fi
fi

# ─── Meta-test 9: doc freshness vs Android churn ──────────────────────
echo "── #9: doc freshness vs Android churn ──"
android_commits_30=$(git log --since="30 days ago" --oneline -- VPNRouter.Android/ 2>/dev/null | wc -l)
doc_age=$(git log -1 --format=%cr plans/android-development-methodology.md 2>/dev/null || echo "unknown")
if [ "$android_commits_30" -gt 5 ]; then
  doc_recent=$(git log --since="30 days ago" --format=%H -- plans/android-development-methodology.md 2>/dev/null | head -1)
  if [ -z "$doc_recent" ]; then
    warn "$android_commits_30 Android commits in 30d but methodology not updated (last: $doc_age)"
  else
    pass "doc updated within 30d (latest: $doc_age)"
  fi
else
  pass "Android churn low ($android_commits_30 commits in 30d) — doc freshness not critical"
fi

# ─── Summary ─────────────────────────────────────────────────────────
echo
echo "═══════════════════════════════════════"
echo "Methodology check summary"
echo "  Fails: $FAILS"
echo "  Warns: $WARNS"
echo "═══════════════════════════════════════"
if [ "$FAILS" -gt 0 ]; then
  echo "❌ Methodology compliance failed — fix process before merging."
  exit 1
fi
echo "✓ Methodology compliance OK (with $WARNS warning(s))"
exit 0
