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
*(filled by agent after impl)*

**Expected commit shape**:
- App/Strings.cs: 1631 → ~230 LOC (-1400)
- All deletions go through `git diff` as `+1 pass-through line` / `-3 original ru-en lines`
- Per-getter idempotent — re-running script on already-converted file is no-op

**Follow-up**: this lays groundwork for Phase 3 modernization (System.Text.Json migration won't touch Strings.cs because it's source-only).
