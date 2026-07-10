# Cleanup backlog — over-engineering / deadwood audit (2026-07-05)

Read-only ponytail-style audit (Codex, no edits/build/tests). Net estimate: **-3500 lines,
0 deps removable**. **NOT for the true-split release** — this is tech-debt hygiene; land the
feature (v2.46.0) stable first, then do this as its own focused pass (each shrink through
`phase-task-launcher` + characterization). Triaged below.

## Safe deadwood (low risk — clean delete, one pass)
- [x] **#3 `PlayStoreSource` stub — DONE v2.47.0-r6 (2026-07-10)**: no caller passed
  `preferPlayStore:true` (dead branch); deleted the stub + the `preferPlayStore` param/branch in
  `PlatformServices.CreateUpdateSource` + the 2 contract tests + all doc crefs. Build clean, 8
  update-source tests green.
- [x] **#5 `vpnrouter emergency-test` hidden CLI — DONE v2.47.0-r6 (2026-07-10)**: the
  EmergencyChannel engine is already unit-tested (`EmergencyChannel*Tests`), so the hidden CLI
  harness was a manual dev tool — removed the `AddCommand` registration + `EmergencyChannelTestCommand.cs`
  + the CLI CLAUDE.md line. CLI builds clean.
- [x] **#6 Android boot-autostart no-op scaffolding — STALE/already-done (verified 2026-07-10)**:
  no boot-autostart checkbox/handler/storage remains (grep for boot-complete/autostart-boot/
  KeyBootAutostart = empty) — swept by the Codex "Android dead-code purge" during the v2.46.0
  cycle. No change needed.
- [ ] **#8 `DeepVerifyConstants`** — NOT independently actionable: the 2 constants are SHARED by
  `VlessDeepVerifier` + `FreeConfigDeepVerifier` (that's why they were extracted); inlining would
  RE-duplicate them. Only makes sense folded into #4 (verifier consolidation). Leave until #4.
  `VPNRouter.Core/Services/DeepVerifyConstants.cs`

## Shrink-refactors (legit, but touch working code — each with tests + characterization)

> Assessed 2026-07-10 (Fable): these three consolidate WORKING, mostly security-adjacent code
> for LINE-REDUCTION only — no functional benefit — and each carries real regression risk on a
> critical path (localization/UI text, deep-verify verdicts, firewall + true-split path
> resolution). Per this file's own header they belong in "its own focused pass (each shrink
> through phase-task-launcher + characterization)", NOT a mid-candidate-cycle sweep. Left OPEN
> deliberately as a dedicated methodology-driven pass — not manufactured risk on v2.47.0.

- [ ] **#1 localization re-export wrappers** — App/Android `Strings` mostly proxy
  `Core.Localization.Strings` (907/1392 lines); alias to Core, keep only platform-bootstrap strings.
  `VPNRouter.App/Localization/Strings.cs`, `VPNRouter.Android/Localization.cs` — **DEDICATED PASS**:
  907-line surgical change to the UI-text layer bound EVERYWHERE (every XAML `{Binding L_X}` + code
  `Strings.X`); one missed alias = wrong/blank UI text; characterization + visual-diff catch structure
  but not per-string. Line-reduction only, no functional benefit → high blast radius on user-facing text.
- [ ] **#4 duplicate deep-verify plumbing** — `VlessDeepVerifier` SELF-ADMITS it mirrors
  `FreeConfigDeepVerifier` BY DESIGN (its own class doc: "Duplicates ... by design — FreeConfigs has
  its own status enum and result mutation pattern that doesn't fit ServerViewModel"). Consolidating
  fights a DELIBERATE, author-documented design choice, on the security-verdict path, for line-reduction.
  `VPNRouter.Core/Services/VlessDeepVerifier.cs` — **DEDICATED PASS** (respect the documented choice
  absent a bug) [#8 folds in here]
- [x] **#7 profile-source builder — PARTIALLY DONE v2.47.0-r6 (2026-07-10)**: the profile-source half
  IS done — `StartCommand.BuildDryRunSources` (a near-dup that silently dropped the %ProgramData%
  source) deleted; dry-run now calls `ProfileSourceFactory.Create(settings)`, so a dry-run previews
  the SAME source set a real start uses (behaviour fix + ~26 LOC gone, 0 tests pinned it, ProfileManager
  tests green). The where.exe half LEFT OPEN: `ProcessImagePath.ResolveNameToPath` (STATIC raw Process,
  used by true-split) vs `FirewallManager.ResolveProcessPath` (instance `IProcessRunner`, injectable for
  tests) — merging touches the LIVE-PROVEN session-0 kill-switch fail-OPEN fix + true-split path
  resolution (both security-critical, different testability) for ~10 LOC. Not worth the regression risk.

## Push-back — do NOT bulk-delete
- **#2 "phase/wave/version archaeology" (~4027 rg matches)** — most are **load-bearing "why"
  comments** the project deliberately keeps (session-0 fail-open rationale, `bug-hunt P1-x`
  invariants, lesson links). rg match count is not a deletable-line count. Methodology values the
  "why". At most trim genuinely-dead version-archaeology (refs to long-removed code), **per comment**,
  never as a sweep.

Source: user-relayed Codex audit, 2026-07-05.
