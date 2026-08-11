# v2.49 Phase 1 — stable connected Apply

**Owner**: Codex session 2026-08-11
**Branch**: `codex/v2.49-connection-stability`
**Base**: stacked on green prerequisite PR #130 (`codex/fix-publish-apt-yaml`)
**Roadmap ref**: owner request 2026-08-11 · v2.49 connection-stability audit
**Effort**: 4-6 hours
**Risk**: MEDIUM
**Blast radius**: connected Apply orchestration and generated routing fingerprint ·
approximately 3 production files and 2 test files · no schema or UI layout change
**Rollback**: revert the implementation commit or close the stacked PR

## Why

Connected Apply currently mutates `ActiveRoutingMode` and `TunFingerprint`
inside `StartupPipeline` before `VpnEngine` compares the old and new values.
The comparison can therefore miss a real structural change. The process-list
comparison also observes only scanner output, while `ConfigGenerator` may route
the explicit `RoutingAppsInclude` or `RoutingAppsExclude` list instead. The UI can
report success while the live tunnel still carries the prior routing semantics.

## What

1. Add one canonical, order-independent fingerprint for the effective per-app
   routing policy shared by generated and custom configs. It includes
   include/exclude mode, preserves process-name casing, and ignores app-list
   changes while full-tunnel routing makes them irrelevant.
2. Store the active app-routing fingerprint after a successful cold start.
3. Capture the active routing mode, TUN fingerprint, app fingerprint, profile,
   scanner result, config mode, and server address before HotReload mutates them.
4. Compare the captured baseline with the candidate state after config generation.
   Escalate to a full sing-box restart only when an effective structural value
   changed, including ConfigMode and explicit Include/Exclude edits.
5. Restore captured metadata only for failures before sing-box accepts the
   candidate. Publish the candidate immediately at the commit point and finish
   true-split reconciliation without caller cancellation after that point.

## How

1. Extract effective app-list selection into one internal `ConfigGenerator`
   helper reused by generated configs, custom configs, and the fingerprint.
2. Add a small pure structural-diff helper in `VpnEngine` for unit coverage.
3. Snapshot the active Apply baseline before `StartupPipeline.ExecuteAsync`, restore
   it on every failed exit, and commit the candidate baseline only after reload or
   restart succeeds.
4. Add dedicated tests covering explicit include, explicit exclude, fallback scanner,
   order normalization, case sensitivity, full-tunnel irrelevance, and each
   structural-diff axis.
5. Strengthen the existing Apply source guard so baseline capture must remain before
   pipeline execution and failure restoration cannot silently disappear.

## Deliberate exclusions

- No targeted Clash connection termination in this increment: feasibility is not
  proven and a global connection sweep would be more disruptive than the existing
  explicit Apply restart contract.
- No free-config verify-before-stop yet. Existing code explicitly warns that Deep
  Verify is unreliable while the main TUN is active, so a naive preflight could
  false-pass through the old tunnel. The safe rollback design is a separate v2.49
  increment.
- No version bump or release tag. `AppVersion.Version` remains `2.48.0` until the
  owner explicitly starts the first v2.49.0 rolling candidate.

## Verification gate

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` has 0 errors.
- [ ] **Gate 2 — Tests green**: affected tests and the full test suite pass; every
  behavior-changing path has a regression test.
- [x] **Gate 3 — Docs**: this brief Outcome and `VPNRouter.Tests/CLAUDE.md` inventory
  are updated; README and zone architecture docs remain unchanged unless scope grows.
- [x] **Gate 4 — Self-review**: Ponytail review plus mandatory `bug-hunt`; use Qwen
  read-only review as an independent fallback because the `simplify` skill is not
  installed in this environment.
- [x] **Gate 5 — MCP verify**: N/A for this commit because no view or localization
  surface changes; the end-to-end Applications Apply scenario remains mandatory in
  `post-ship-mcp-verify` after an explicitly requested rolling release.
- [x] **Gate 6 — Characterization diff**: N/A; this is a behavior fix, not a god-file split.
- [x] No stale TODOs, commented-out alternatives, new dependency, or version drift.
- [ ] Commit hooks and remote CI pass without bypass.

## Risk

**Justification**: a false positive structural delta causes a controlled reconnect;
a false negative preserves the current bug. The change therefore touches a sensitive
connected lifecycle path.
**Mitigation**: pure fingerprints and diff policy are unit-tested; active metadata is
restored on failure; no firewall, credentials, update, or persistence schema changes.
**Detection**: tests pin every delta axis; logs identify the exact restart reason;
post-ship verification checks PID change only when the effective policy changed.

## Outcome

Implementation candidate complete; remote verification is pending.

- Added one effective app-list resolver shared by generated and custom configs,
  plus an ordinal fingerprint that detects explicit Include/Exclude and
  case-only process changes without reacting to irrelevant full-tunnel edits.
- Connected Apply now compares the captured live baseline across ConfigMode,
  routing mode, TUN settings, and app routing. Candidate metadata is restored on
  pre-commit failures and published immediately after sing-box accepts it.
- Bug-hunt used three independent reviewers. Five unique important findings were
  verified and fixed in scope: ConfigMode escalation, case-sensitive matching,
  post-commit rollback, weak source pins, and the status-callback commit window.
  The duplicated custom-config selection path was also removed. Final review:
  no remaining critical or important findings.
- Local verification: solution build 0 errors; affected suites 87/87; mandatory
  v2.28 regressions 22/22. The full local suite reached 2687 passed and 25
  environment-only failures caused by protected `C:\ProgramData\VPNRouter` and
  an externally held global TUN semaphore; the clean CI runner is the final
  full-suite authority for Gate 2.
- Qwen read-only fallback was attempted with filesystem/shell/web/write disabled;
  it timed out without output or changes. Mandatory multi-review bug-hunt supplied
  the independent review instead.
- Commits: `d00d4274` (brief); implementation commit pending.
