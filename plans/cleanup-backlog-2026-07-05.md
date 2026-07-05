# Cleanup backlog — over-engineering / deadwood audit (2026-07-05)

Read-only ponytail-style audit (Codex, no edits/build/tests). Net estimate: **-3500 lines,
0 deps removable**. **NOT for the true-split release** — this is tech-debt hygiene; land the
feature (v2.46.0) stable first, then do this as its own focused pass (each shrink through
`phase-task-launcher` + characterization). Triaged below.

## Safe deadwood (low risk — clean delete, one pass)
- [ ] **#3 `PlayStoreSource` stub** — `return null` / `NotSupportedException` + tests on the stub;
  no real Play build variant. Delete until a Play variant exists.
  `VPNRouter.Core/Services/UpdateSources/PlayStoreSource.cs`
- [ ] **#5 `vpnrouter emergency-test` hidden CLI** — dev harness in the production CLI → move to a
  local script / test fixture. `VPNRouter.CLI/Commands/EmergencyChannelTestCommand.cs`
- [ ] **#6 Android boot-autostart no-op scaffolding** — UI already hidden; checkbox/locals/storage
  handlers left "for future BootCompletedReceiver". `VPNRouter.Android/AndroidApp.UiBindings.cs:971`
- [ ] **#8 `DeepVerifyConstants`** — 2 constants for 2 call-sites; folds into #4.
  `VPNRouter.Core/Services/DeepVerifyConstants.cs`

## Shrink-refactors (legit, but touch working code — each with tests + characterization)
- [ ] **#1 localization re-export wrappers** — App/Android `Strings` mostly proxy
  `Core.Localization.Strings` (907/1392 lines); alias to Core, keep only platform-bootstrap strings.
  `VPNRouter.App/Localization/Strings.cs`, `VPNRouter.Android/Localization.cs`
- [ ] **#4 duplicate deep-verify plumbing** — `VlessDeepVerifier` self-admits it mirrors
  `FreeConfigDeepVerifier`; outbound builders mirror `ConfigGenerator`. One probe runner + shared
  outbound builder. `VPNRouter.Core/Services/VlessDeepVerifier.cs`
- [ ] **#7 one profile-source builder** — `ProfileSourceFactory.Create`, `StartCommand.BuildDryRunSources`,
  and the Core path repeat one fallback order. `VPNRouter.CLI/Helpers/ProfileSourceFactory.cs`
  (relatedly: `ProcessImagePath.ResolveNameToPath` where.exe now duplicates `FirewallManager`'s
  where.exe-via-IProcessRunner — consolidate the two here.)

## Push-back — do NOT bulk-delete
- **#2 "phase/wave/version archaeology" (~4027 rg matches)** — most are **load-bearing "why"
  comments** the project deliberately keeps (session-0 fail-open rationale, `bug-hunt P1-x`
  invariants, lesson links). rg match count is not a deletable-line count. Methodology values the
  "why". At most trim genuinely-dead version-archaeology (refs to long-removed code), **per comment**,
  never as a sweep.

Source: user-relayed Codex audit, 2026-07-05.
