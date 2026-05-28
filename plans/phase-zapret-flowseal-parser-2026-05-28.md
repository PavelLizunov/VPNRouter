# Brief — Flowseal probe parser format-drift fix (v2.37.0-r53)

**Type**: user-reported bug hotfix (ship-rolling-candidate path, not v3.0).
**Date**: 2026-05-28
**Risk**: LOW — isolated to the Flowseal stdout parser + a tolerant string
match in the VM; no change to VPN core, config generation, or startup.

## Why

User (problem Windows PC, logs Z:\zapret) ran the Zapret one-tap probe.
Flowseal's DPI checker clearly worked — `general (ALT3)` scored 45/108 and
`general (ALT9)` scored 75/108 (Flowseal's own ANALYTICS + "Best config:"
lines confirm) — yet VPNRouter reported **"стратегия не найдена"**.

## What (root cause)

Flowseal's `test zapret.ps1` (mode 2) changed its stdout format. Per-test
status lines used to be two-bracket:

```
[YT_LIVE@0][HTTP] code=200 … status=OK
```

Current build moved the target id to a separate header line and emits a
bare single-bracket status line:

```
=== [🧠][Self check] US.GH-HPRN ===
[HTTP]   code=405 … status=OK
```

`ZapretAutoStrategy`'s `statusLineRx` required the two-bracket shape
(`^\s*\[[^\]]+\]\[(?:HTTP|TLS1\.[23])\]…`) so it matched ZERO lines →
`currentOkCount`/`currentTotalChecks` stayed 0 → `perStrategyResults` empty →
early-winner never fired → winner fell through to the "no strategy" branch.

## How (fix)

`VPNRouter.Core/Services/ZapretAutoStrategy.cs`:
- Hoisted the 4 stdout regexes to `internal static readonly` (single source
  of truth, shared with the new pure parser).
- `StatusLineRx`: made the leading target-id bracket OPTIONAL
  (`^\s*(?:\[[^\]]+\])?\[(?:HTTP|TLS1\.[23])\]…`) → parses both formats.
- Extracted pure `ParseFlowsealTranscript(string)` (testable) +
  `BestStrategyByScore(dict)`.
- Winner is now empirical best-by-score (most passing labels, tie-break by
  ratio) — also fixes a latent "last `Best config:` line wins" ordering bug in
  per-`[1/1]` runs. Explicit "Best config:" is the degenerate-case fallback.
- Post-exit: when no explicit winner matched, promote the best-scoring
  strategy instead of returning null.

`VPNRouter.App/ViewModels/MainWindowViewModel.cs`:
- Winner→strategy lookup is now tolerant (trim / case / stray `.bat`)
  instead of exact `==`, closing the "Winner X not found in strategy list"
  path.

## Verification gate

- [x] Gate 1 build: `dotnet build VPNRouter.App -c Release` → 0 errors.
- [x] Gate 2 tests: 14 new `ZapretFlowsealParserTests` + 57/57 Zapret + MVM
      characterization green.
- [x] Gate 3 docs: release notes `plans/release-notes-v2.37.0-r53.md`; this brief.
- [x] Gate 4 self-review: parser logic reviewed; static regexes shared to
      avoid divergence; UNSUPPORTED-as-pass / LIKELY_BLOCKED-as-fail preserved.
- [-] Gate 5 MCP verify: live end-to-end probe needs real censored DPI targets
      (not reproducible in the dev VM — all targets would FAIL legitimately).
      **Parser verified via 14 unit tests against the user's EXACT Z:\zapret
      line format.** Live confirmation is on the user's problem machine.
- [-] Gate 6 characterization diff: MVM public-surface hash UNCHANGED (VM edit
      added only a local function — characterization test green).

## Outcome

**Status**: PASS (shipped r53)
**Commit**: `e426b13`
**Files changed**: 4 · +449 / −15
**Pushed**: github + origin `e426b13`
**Rollback**: `git revert e426b13`
