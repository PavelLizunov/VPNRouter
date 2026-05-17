# Phase 2 — 2A: Localization dedup (App/Strings.cs → Core pass-through)

**Owner**: Wave 5 parallel agent
**Roadmap ref**: plans/v3.0-refactor-roadmap.md §"Phase 2 — Mediums" 2A; plans/dead-code-audit-2026-05-17.md §3 "Biggest dedup target"
**Effort**: 1 day
**Risk**: LOW-MEDIUM (wide blast radius — every UI string touched — but Android already proves the pattern works)

## Why
Audit A: `VPNRouter.App/Localization/Strings.cs` is **1631 LOC** and **93% duplicates** of `VPNRouter.Core/Localization/Strings.cs` (547/589 keys identical Ru/En). This is the single largest dedup opportunity in the codebase.

Android already uses the clean pattern (`VPNRouter.Android/Localization.cs`):
```csharp
public static string SmpScanQrButton => global::VPNRouter.Core.Localization.Strings.SmpScanQrButton;
```

Apply same to App. Keep only the ~42 App-specific keys (desktop-only menu items).

**LOC delta**: ~−1,400 (target: 1631 → ~230 LOC).

## What
1. Inventory App/Strings.cs — list every public static getter.
2. For each, find the matching Core/Strings.cs getter. Compare Ru/En literal text.
3. Categorize each App-Strings.cs getter:
   - **DUPLICATE** — identical Ru/En to Core → rewrite as pass-through (`=> global::VPNRouter.Core.Localization.Strings.<Name>`)
   - **APP-SPECIFIC** — no Core match OR Ru/En differs → keep in App/Strings.cs as-is (annotate with `// app-only` comment)
4. After all rewrites, verify file compiles + same XAML binding behavior

## How

**Step 1 — Inventory**:
```
cd C:/Project/VPNRouter
grep -oE 'public static string [A-Za-z0-9_]+' VPNRouter.App/Localization/Strings.cs | sort -u > /tmp/app-strings.txt
grep -oE 'public static string [A-Za-z0-9_]+' VPNRouter.Core/Localization/Strings.cs | sort -u > /tmp/core-strings.txt
```
Expected: ~589 app, ~700+ core.

**Step 2 — Match + diff** (programmatic):
For each app getter name, check if Core has same name AND same Ru/En literal:
```csharp
// In Core:
public static string SmpFoo => Ru ? "значение" : "value";
// In App:
public static string SmpFoo => Ru ? "значение" : "value";
// → DUPLICATE → rewrite to pass-through
```

Write a small helper script (PowerShell or Python) that:
- Parses both files into name → (Ru, En) maps
- For each name in App, compares against Core
- Outputs: app-name | core-has-name | ru-match | en-match | verdict

**Step 3 — Bulk Edit**:
For each DUPLICATE, replace in App/Strings.cs:
```csharp
public static string SmpFoo => Ru ? "значение" : "value";
```
with:
```csharp
public static string SmpFoo => global::VPNRouter.Core.Localization.Strings.SmpFoo;
```

Keep file structure (regions, comments). Sectionwise rewrite.

For APP-SPECIFIC getters: leave alone but add comment `// app-only: not in Core/Strings.cs` for future audit clarity.

**Step 4 — Cross-file refs**: check if anything outside Strings.cs reads `Ru` boolean directly. If so, the pass-through still works because Core's `Ru` is read at runtime.

**Step 5 — Verification**:
- `dotnet build VPNRouter.sln -c Release` → 0 errors
- `dotnet test ... --filter "!~Headless&!~PageScreenshot&!~VisualDiff"` → 839 pass (no regression)
- Run headless suite to catch XAML binding regressions:
  - `dotnet test --filter "FullyQualifiedName~HeadlessGuiTests|FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests"`
  - VisualDiffTests baseline match = no visual regression

## Verification gate
- [ ] Inventory: ~589 App getters classified
- [ ] DUPLICATE count: ~547 (per audit)
- [ ] APP-SPECIFIC count: ~42
- [ ] **Gate 1**: dotnet build VPNRouter.sln -c Release → 0 errors
- [ ] **Gate 2**: full suite 839/842 (3 skipped) — same as baseline
- [ ] **Gate 2b**: headless suite all green (binding regression net)
- [ ] **Gate 6**: VisualDiffTests pixel-tolerance baseline match — no visual drift
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome

**Status**: PASS (gates clean, ready for integrator commit)

**LOC delta**:
- App/Strings.cs: 1631 → 1217 LOC (**−414 LOC, −25 %**)
- Diff stat: `1 file changed, 588 insertions(+), 1002 deletions(-)`

The −414 number is smaller than the brief's optimistic target of `~230 LOC`
(`−1400`) because Step 3 explicitly said "Keep file structure (regions,
comments)". I preserved every comment block above each getter — many of
them are valuable history (`// v2.30.6-r1 (UX-46 fix): ...`) and chasing
the optimistic LOC would have deleted ~1200 lines of comment provenance.
The brief's "+1 / −3 per getter" expected-commit-shape and the "keep
comments" instruction were in tension; I sided with the more
conservative "keep comments" reading. Result is still the single
largest dedup win in the codebase.

**Per-getter shape now**:
- DUPLICATEs become single-line pass-throughs:
  `public static string Foo => global::VPNRouter.Core.Localization.Strings.Foo;`
- Method getters get parameter forwarding:
  `public static string Foo(int n) => global::VPNRouter.Core.Localization.Strings.Foo(n);`
- Comment blocks above the getter are preserved untouched.

### Inventory + classification

Built two scripts under `tools/`:
- `tools/dedup-strings.ps1` — parses both files, emits TSV report.
- `tools/dedup-strings-rewrite.ps1` — applies pass-through rewrite.

Both files use explicit UTF-8 (no BOM) read/write so Cyrillic in
"private static bool Ru ? \"…\" : \"…\"" survives the round-trip
(critical — first dry-run accidentally mojibake'd Russian text via
PowerShell's default Get-Content encoding).

| Verdict | Count | Action |
|---|---|---|
| DUPLICATE | 544 | rewritten as pass-through |
| TEXT-DRIFT | 2 | preserved (annotated app-only) |
| APP-ONLY | 42 | preserved (annotated app-only) |
| **Total** | **588** | brief expected 589, audit A said 547+42 |

(Brief expected 547 DUPLICATE / 42 APP-ONLY → got 544 / 42 + 2 TEXT-DRIFT.
Three of the audit-claimed "547 byte-identical" pairs were actually
TEXT-DRIFT (subtle differences) plus one new APP-ONLY block I caught
that wasn't in audit A.)

### TEXT-DRIFT entries — kept in App, annotated

1. **`ColPing`** (line 100 new file). Core has `Ru ? "Пинг" : "Ping"`
   (Bug-AND-016 bilingual fix). App stayed `"Ping"` only. Classified
   as TEXT-DRIFT so this pass doesn't silently change desktop behaviour.
   Follow-up: switch to pass-through — Core's version is strictly better.

2. **`SmpAdvCardSubtitle`** (line 924 new file). Core branches on
   `OperatingSystem.IsAndroid()`. On Windows the non-Android branch
   produces byte-identical output, but the IL differs, so the simple
   text-compare flagged it. Preserved verbatim to avoid an OS-branch
   conversion in this scope. Follow-up: pass-through is safe but
   requires care.

### APP-ONLY clusters — kept in App, annotated as `// app-only`

Five distinct clusters of desktop-only features (each annotated with
`// ── app-only: not in Core/Localization/Strings.cs ──` plus a
follow-up note about lifting to Core when Android grows the same
surface):

| Cluster | Members | Annotation header at |
|---|---|---|
| `AppsModeXxx` (v2.32 Include/Exclude toggle) | 5 | line ~624 |
| `ServersOrphanXxx` (v2.32 orphan marker) | 2 | line ~647 |
| `AutoFailoverXxx` (v2.32 F-E surface) | 4 | line ~657 |
| `ConflictKill/Ignore` (v2.32.1 Bug-r10) | 5 | line ~1080 |
| `EmergencyChannelXxx` (v2.32.2 W-4 wgturn) | 26 | line ~1127 |

Plus the two TEXT-DRIFT singletons (`ColPing`, `SmpAdvCardSubtitle`).

### Settable Lang shim — non-trivial caveat

Original line 8: `public static string Lang { get; set; } = "en";` —
a *settable* property. Naive pass-through (`=> global::...Lang;`) loses
the setter and would have broken
`MainWindowViewModel.cs:2943,6217` which do `Strings.Lang = ...`. Solved
with a hand-written get/set shim at the top of the App file:

```csharp
public static string Lang
{
    get => global::VPNRouter.Core.Localization.Strings.Lang;
    set => global::VPNRouter.Core.Localization.Strings.Lang = value;
}
```

This is actually *more correct* than the pre-dedup state — pre-dedup,
App and Core each kept their own `Lang` string, and `Ru` evaluation in
each project read its own copy. After this fix, App + Core share a
single `Lang`, so toggling language anywhere flips both projects'
`Ru` boolean. `MainWindowViewModel`'s call sites work unchanged.

The rewrite script (`tools/dedup-strings-rewrite.ps1`) was updated to
skip `Lang` so re-runs don't clobber the manual shim. Re-running on
the dedup'd file is now a true no-op (idempotent).

### Verification gate

- [x] Inventory: 588 App getters classified (1 short of brief's "~589")
- [x] DUPLICATE count: 544 (vs brief's "~547")
- [x] APP-SPECIFIC count: 42 APP-ONLY + 2 TEXT-DRIFT = 44 (vs brief's "~42")
- [x] **Gate 1**: `dotnet build VPNRouter.sln -c Release` → **0 errors, 0 warnings**
- [x] **Gate 2**: non-headless suite → 845 pass / 0 fail / 3 skip (1 known
      flake `SettingsLoaderRobustnessTests.Load_NoRecognizedKeys_…`
      reproduces *without* my changes too — pre-existing, unrelated).
- [x] **Gate 2b**: headless suites run per-class to avoid documented
      dispatcher-thread shutdown quirk in `VPNRouter.Tests/CLAUDE.md`:
      - HeadlessGuiTests: 8 / 8 pass
      - PageScreenshotTests: 19 / 19 pass
      - VisualDiffTests: 3 / 3 pass (in isolation)
      - Total: 30 / 30 individual passing
- [x] **Gate 6**: VisualDiffTests baseline match in isolation — strings
      and bindings produce byte-identical PNG text. (A pre-existing
      Light/Dark theme drift surfaces only when running the combined
      `~Headless|~PageScreenshot|~VisualDiff` filter — present *without*
      my changes too. Not a regression introduced here.)

### Tooling artifacts

Two scripts left in `tools/` (not committed — integrator can choose):
- `tools/dedup-strings.ps1` — re-classifies any future drift between
  App/Core Strings.cs. Useful for periodic audits.
- `tools/dedup-strings-rewrite.ps1` — idempotent rewriter; skips `Lang`
  and entries already in pass-through form.
- `tools/dedup-strings.report.tsv` — full TSV report (588 rows).

### Follow-ups for separate tasks

1. **Lift `ColPing` + `SmpAdvCardSubtitle` to pass-through** — they're
   currently TEXT-DRIFT but Core's versions are either strictly
   better (bilingual Пинг) or behaviour-equivalent (OS-branch helper).
   Trivial, one-line follow-up commit each.

2. **Lift `EmergencyChannelXxx` to Core** when wgturn Phase 2 lands
   the Android surface. 26-string block, mostly straight-forward.

3. **Lift `AutoFailoverXxx` + `AppsModeXxx` to Core** when Android
   grows the same UI. 9-string block.

4. **Pre-existing test flake**: `SettingsLoaderRobustnessTests.Load_…`
   intermittently fails on rapid runs (filesystem rename race?). Not
   in scope for this task — flagged here so a future hardening task
   can investigate.

5. **Pre-existing VisualDiffTests theme drift**: when run as part of
   a combined filter, theme defaults to Dark while baseline is Light.
   Per-class run is fine. Looks like dispatcher state pollution from
   earlier test classes. Not in scope.

**Follow-up**: this lays groundwork for Phase 3 modernization (System.Text.Json
migration won't touch Strings.cs because it's source-only).
