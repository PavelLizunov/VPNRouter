# Methodology Audit — VPNRouter (Phase 2)

> Provenance: `/methodology` Phase 2, run as a 7-lens multi-agent workflow
> (`vpnrouter-methodology-audit`, run `wf_3f0e23b4-bf1`, 2026-06-25). 8 agents,
> 43 findings, ~1.1M subagent tokens. Each finding self-verified against the repo
> before synthesis. Graded vs `C:/Users/x3d_mutant/methodology-toolkit/METHODOLOGY.md`.
> READ-ONLY audit — nothing installed; setup is gated on the user's go.

**Headline verdict:** VPNRouter's release plumbing is genuinely mature — SHA-pinned
actions, a fail-safe diagnostics redactor, a handle-leak gate, a real auto-update
integration gate, secret-scanning + push-protection on. But the gate *graph* has one
structural hole that already shipped a P0 to a user: **nothing connects the
deferred-defect ledger to the cut-stable gate**, the green build is **never kept green**
(no deny-warnings, ~194-300 warnings pass), and **the methodology's #1 layer — an
independent review-agent — is absent from every gate on the path that reaches users.**

---

## Implementation status (2026-06-25)

Installed this session — commits `072763f9..567d28a8` on main, each gate-passing + CI green:
- [x] 1 global.json SDK pin · [x] 2 Directory.Build.props NuGetAudit · [x] 3 doc fixes ·
  [x] 4 cut-stable-checklist re-sync · [x] 5 broaden pre-commit Gate 2 ·
  [x] 6 Dependabot alerts + security updates (gh api) · [x] 7 OPEN-DEFECTS ledger +
  check-open-p0.ps1 + cut-stable 6.5 gate · [x] 8 REVIEW_AGENT_PROMPT.md + ship review-diff
  HARD step · [x] 9 bug-hunt skill · [x] 10 tests on `v*` tags + cut-stable Step 5 wait ·
  [x] 12 Windows characterization CI job · [x] 13 TOLERATE_FAILURE allowlist + pre-push
  watcher · [x] 14 diagnose-config pin-as-test step · [x] 15 CodeQL advisory.
- [ ] **11 Harden mac/linux sing-box smoke to a HARD failure — DEFERRED.** Modifies the
  release build scripts (`build-mac.sh` / `build-linux.yml`), unverifiable on this dev box;
  making a release-path check hard-fail blind is the exact untested-critical-path
  anti-pattern this methodology forbids. Do it with a `workflow_dispatch` run on a real
  mac/linux runner to confirm the check passes BEFORE making it blocking.
- [ ] **16 Behavioral/concurrency fixes — DEFERRED to v2.44.3.** Failover restart (fresh
  token), `_isStopping`/SemaphoreSlim guard, AutoFailover ResetCycle-on-good-connect + flap
  rate-limit, LinuxFirewallManager explicit `isFullTunnel`. Needs VM integration testing;
  these are the substance the #7 ledger gate blocks on (`plans/OPEN-DEFECTS.md`).

## P0

**1. No gate links open deferred-P0s to the cut-stable decision** *(all 7 lenses)*
- Problem: a defect found by the bug-hunt, written as P0, deferred, can be promoted to stable because no gate reads the open-defect ledger — exactly how the auto-failover teardown shipped in v2.44.0/.1 and bit diag 20260624-235243.
- Evidence: `.claude/skills/cut-stable/SKILL.md:13-27` (6 conditions, none scan backlog) + `CLAUDE.md` rule #6 + `plans/v2.44-bug-hunt-deferred-2026-06-22.md:84-129`.
- Fix: a 7th hard pre-cut condition — `tools/check-open-p0.ps1` greps `plans/*deferred*.md`/`*bug-hunt*.md` for unresolved `^## P0|^## P1` and BLOCKS the cut unless each is fixed or per-item user-waived. **Cost M.**
- Risk: the bug-hunt is theatre without it — every deferred P0 (clash_api no-secret, kill-switch empty-list arm, subscription-leak Save) can ride a green gate to 100% of users.

**2. No deny-warnings / strict-lint — the 0-warning build is incidental** *(Pre-commit, CI, Stack)*
- Problem: METHODOLOGY §3 layer 1 wants deny-warnings + formatter `--check`; no `Directory.Build.props`, no `.editorconfig`, no `global.json`, `dotnet format` runs nowhere → build + CI pass green with ~194-300 warnings (incl. 68 CS8602 null-deref + 2 CS8625 in product code).
- Evidence: `Grep TreatWarningsAsErrors|WarningsAsErrors|warnaserror` over all csproj/props = none; `.githooks/pre-commit:107` build has no `/warnaserror`.
- Fix: root `Directory.Build.props` flipping high-value warnings to errors first (`<WarningsAsErrors>CS8602;CS8625;CS0618;nullable</WarningsAsErrors>`, keep CA1416 a warning) + Gate 1b `dotnet format --verify-no-changes`; full `TreatWarningsAsErrors` after burndown. **Cost S.**
- Risk: green build hides 68 possible null-derefs; warnings accrete unbounded; fmt drift surfaces only on remote CI.

**3. No independent review-agent in any blocking gate on the ship/cut path** *(Skills, Pre-commit)*
- Problem: METHODOLOGY §5/§7.1 make an independent review-agent the FIRST blocking layer; the pre-commit hook has Gates 1-7 but no review, there is no `docs/REVIEW_AGENT_PROMPT.md`, and ship-rolling-candidate + cut-stable have zero review → every -rN and cut ships self-reviewed only (§18 self-preference bias).
- Evidence: `.githooks/pre-commit:18-29` (no review) + `Grep review-agent|reviewer|subagent .githooks/* = none`; review is only phase-task-launcher's *conditional* Gate 4, which excludes the ship path.
- Fix: `docs/REVIEW_AGENT_PROMPT.md` from the toolkit template filled with VPNRouter invariants (process_name case-sensitivity, AppVersion==tag incl -rN, ProcessQuery not GetProcessesByName().Length, all strings localized) + a HARD `review-diff` step in ship-rolling-candidate pre-flight, with the §5 ≤5-line/one-surface hotfix short-circuit. **Cost M.**
- Risk: logic bugs, async races, leaked handles, security holes (unauthenticated clash_api) ship unreviewed — the auto-failover P0 took exactly this gateless path.

**4. Stable tag cut re-runs ZERO tests on a clean runner — only build+upload** *(CI)*
- Problem: `test.yml`/`test-windows-update.yml` trigger on push/PR-to-main and `-r*` tags only; a stable `vX.Y.Z` tag fires only build-and-upload, and the lone required `verify` check asserts AppVersion-string + sha256 + asset-count==14 (never behavior) AND runs `release: published/edited` (after the artifact is public).
- Evidence: `test.yml:27-32`, `test-windows-update.yml:39-41` (tags `v*-r*` only), `build-*.yml` (tags `v*`, 0 tests), `verify-release-integrity.yml:62-64,202-238`, `.githooks/pre-push:34-39` (tag push → exit 0).
- Fix: an `on: push: tags: v*` job running the full `dotnet test` filter + the update integration test, gating the un-draft/`--latest` step so a red suite blocks promotion. **Cost M.**
- Risk: a defect the 765-test suite would catch ships to stable whenever the cut SHA differs from the last green -rN; auto-update pulls it within hours.

**5. The acknowledged self-cancelling failover restart is in v2.44.2 (candidate), guarded only by a comment, no behavioral test** *(Tests, Stack)*
- Problem: on a genuine outage `WireFailoverWithStop` calls `Stop()` then `StartAsync(..., innerCt)` where `innerCt == _probeCts` that `Stop()` just cancelled → replacement never starts; the only v2.44.2 test pins the *pure* `ShouldAutoFailoverAfterProbe` in isolation, never the wiring.
- Evidence: `VPNRouter.Core/Services/VpnEngine.cs:1165-1177` + `:1221-1242`; only test `VpnEngineProbeFailoverGateTests.cs:24-52`; `plans/...deferred...:119-129`.
- Fix: a failover-restart *integration* test (fake SingBoxManager/Clash-API seam: dead probe + unconfirmed warmup → assert replacement starts) — fails today, proving the self-cancel — then fix in v2.44.3 (restart under a fresh CancellationToken). **Cost M.**
- Risk: the v2.44.2 fix only suppresses the false-positive; genuine-outage failover is still broken and untested.

**6. Every blocking test gate uses a fixed allowlist that EXCLUDES the failover/lifecycle/firewall suites** *(Tests)*
- Problem: pre-commit Gate 2, ship pre-flight, and the cut regression checkbox all run `--filter ~PlaceholderGuard|GetEffective|Subscription|LeakProtection|Resolver` — matching NONE of `VpnEngineProbeFailoverGateTests`, `AutoFailoverEngineTests`, `VpnEngineLifecycleTests`, `ConfigSanityCheckTests`, `LinuxFirewallManagerTests`. Editing `VpnEngine.cs` runs Resolver tests but never the failover tests; the brand-new bug-fix regression pin runs in no local gate.
- Evidence: `.githooks/pre-commit:120-121` (verbatim) + `ship-rolling-candidate/SKILL.md:46-47` + `cut-stable/SKILL.md:17`; filter duplicated in `CLAUDE.md` + `AGENTS.md`.
- Fix: derive Gate 2 scope from staged paths (stage `VpnEngine.cs|AutoFailoverEngine.cs|HealthMonitor.cs|ConfigSanityCheck.cs|Platform/*/*Firewall*` → append matching test classes); for ship+cut replace the static list with the full Core/lifecycle set. **Cost S.**
- Risk: a regression in failover/lifecycle/kill-switch passes the local gate green and ships — the probe-gate fix could be reverted and no -rN gate would notice.

---

## P1

1. **`plans/cut-stable-checklist.md` (skill-not-loaded fallback) drifted to BROKEN commands** the skill disowns — globs `*-windows-x64.zip` (matches nothing), verifies via `ProductVersion` (always `1.0.0+sha`), says "12 assets" vs 14/16. `:6,63,70,104-114` vs `cut-stable/SKILL.md:60-108`. Re-sync or make it a pointer. **S.**
2. **Adversarial bug-hunt is improvised every session — no skill** (15 plans/ files, tasks #38/#41 re-run it). Create a `bug-hunt` skill with fixed persona/fan-out/triage + route survivors to the open-defect ledger (#1). **M.**
3. **Parallel `.agents/skills/` tree + `AGENTS.md` undocumented and already diverged** — `.agents/.../cut-stable` is the CORRECTED copy, `plans/cut-stable-checklist.md` the STALE one (3 copies, 2 correct). Add a "Mirror trees" note to CLAUDE.md + collapse the duplication. **S.**
4. **clash_api exposed with no `secret`; Android comment contradicts the deferred finding** — `VPNConfig.cs:710-714` (no secret) vs `AndroidConfigBuilder.cs:226-229` ("harmless") vs `deferred:10-26`. On Android any app can read connection metadata / issue control calls. Device-test, add per-session secret/Bearer (or unix socket), delete the stale comment. **M.**
5. **VpnEngine start/stop/failover concurrency untested; AutoFailover ResetCycle pins a DEAD method** — no lock/SemaphoreSlim/`_isStopping`; `ResetCycle()` has no prod caller → after 3 lifetime failovers auto-failover gives up permanently, yet its test is green. Add a concurrency race test + behavioral OnConnected-resets test + flap rate-limit. **L/M.**
6. **LinuxFirewallManager test PINS the ambiguous "empty list == arm global kill-switch"** — a split-tunnel user with a 30s scan timeout gets GLOBAL egress drop. Plumb `isFullTunnel` explicitly into `CreateBlockRules` + degraded-split regression test. `LinuxFirewallManagerTests.cs:66-82`. **M.**
7. **diagnose-config never closes the loop to a pinned regression test** — stops at root-cause; no step to pin the repro as a test before the fix ships (violates §6). Add a terminal "write failing test from the diag fixture" step. **M.**
8. **MainWindowViewModel characterization hash SOFT-FAILS on Linux — the only CI OS** — throws strictly only on Windows; on ubuntu it writes the sentinel and returns → the public-surface guard can NEVER fail in CI. `CharacterizationTests.cs:323-361`, `test.yml:36`. Hash the OS-invariant set strictly on every OS. **M.**
9. **pre-push runs NO tests on the push payload** — only checks `HEAD^1`'s remote CI; the payload's only local coverage is the narrow Gate 2. `pre-push:55-63`. Run the full `test.yml` filter on the payload before allowing push. **M.**
10. **Dependabot vulnerability ALERTS disabled + no NuGet audit on a public VPN** — `dependabot_security_updates:disabled`, alerts 403, no `<NuGetAudit>`. Toggle alerts on + add `<NuGetAudit>true</NuGetAuditMode>all`. **S.**
11. **sing-box version skew — desktop 1.13.13 vs Android 1.13.10, doc says "1.13.10 all 3 platforms"** — `build.ps1:48`/`build-linux.yml:100`/`build-mac.sh:63` (1.13.13) vs `build-android.yml:253` (1.13.10) vs `VPNRouter.Core/CLAUDE.md`. Hoist to one `SINGBOX_VERSION` source + fix the doc, or ADR the intentional skew. **M.**
12. **Workflows float dotnet `8.0.x` with no `global.json`** — CI installs whatever patch ≠ local 8.0.418 → analyzer-band drift breaks "CI == local gate". Add `global.json` pinning 8.0.418 + `rollForward: latestPatch`. **S.**
13. **macOS sing-box smoke test is `continue-on-error` AND skip-prone** — the lone Mac runtime gate fails-open + already silently skipped historically. Move `sing-box check` INSIDE `build-mac.sh` as a HARD failure; mirror on Linux. `build-mac.yml:79-120`. **M.**
14. **VPNRouter.Core/CLAUDE.md service map omits the entire `Platform/` tree incl. the Linux/macOS kill-switch** — points a kill-switch session at the Windows manager as if it's the whole story. Add a `Platform/` section + `IFirewallManager` + brick/IPv6 hazards. `Core/CLAUDE.md:16-41`. **S.**
15. *(folded)* No security-review wired into the routine despite a privileged surface (process exec / nftables NOPASSWD / local binds / untrusted YAML) — see P2 item and #3/#9. **M.**

---

## P2

1. **VisualDiffTests is CI-invisible by design** (Windows-only skip + Linux CI) — visual regression reaches users green; drift already slipped v2.37->v2.38. Add a windows-latest tag job. **M.**
2. **CLAUDE.md self-contradicts on whether stable cut is autonomous** — `:43,:82` "autonomous" vs rule #6 "НЕ autonomous"; AGENTS.md `:42` too. Fix the quick-ref line. **S.**
3. **pre-push gate defeatable by an unaudited `TOLERATE_FAILURE` env var; treats unexpected `skipped` as pass** — no trace in commit/push/release. Restrict to an allowlist + reason + `.ci-tolerated-<sha>` log; block unexpected skipped. `verify-last-commit-ci.ps1:14,54-92`. **S.**
4. **post-push CI watcher is opt-in via a non-default `git pushw` alias** — never runs under the documented `git push` flow. Launch it from the tail of `pre-push`. **S.**
5. **Two PR-triggered workflows declare no top-level `permissions:`** (`test.yml`, `grep-placeholder-fingerprints.yml`) — safe only because org default is `read`. Add `permissions: contents: read`. **S.**
6. **No CodeQL on a public network-security tool** — untrusted subscription YAML/URIs, helper.cmd generation, update extractor. Add advisory weekly `codeql.yml` (NOT a required check). **M.**
7. **Stale "will pin / floating @v4" comments contradict already-SHA-pinned actions** — invite a future un-pin. Delete/replace the comments. **S.**
8. **README.ru.md build-mac.sh version example stale** (`2.32.0` vs EN `2.43.0`) — have update-readme-versions assert EN==RU. **S.**
9. **No security-review wired into the routine despite a privileged surface** — add a path-glob-triggered security pass to the review/bug-hunt skill. **M.**

---

## Phase-2 setup plan (ordered, smallest-first; each lands through the existing pre-commit gate)

1. **`global.json` pinning 8.0.418 + `rollForward: latestPatch`** — *(#12)* — **S, AUTOMATE.** Prerequisite for #2.
2. **Root `Directory.Build.props`** `<WarningsAsErrors>CS8602;CS8625;CS0618;nullable</WarningsAsErrors>` + `<NuGetAudit>true</NuGetAuditMode>all` — *(#2,P1-10)* — **S, NEEDS JUDGEMENT** (which warnings first). + `dotnet format --verify-no-changes` Gate 1b.
3. **Doc fixes** (autonomous-cut contradiction; stale SHA-pin comments; README.ru; `.agents/` mirror note; Core `Platform/` section) — *(P2-2,7,8;P1-3,14)* — **S, AUTOMATE.**
4. **Re-sync `plans/cut-stable-checklist.md` to the skill (or pointer)** — *(P1-1)* — **S, AUTOMATE.**
5. **Broaden pre-commit Gate 2 to changed-file scope** + full Core set for ship/cut — *(#6)* — **S, AUTOMATE.**
6. **Toggle Dependabot alerts + security updates** (Settings UI) — *(P1-10)* — **S, NEEDS USER.**
7. **`tools/check-open-p0.ps1` + cut-stable condition #7** — *(#1, the GOLD gap)* — **M, NEEDS JUDGEMENT** (ledger format + waiver). **Highest-priority structural fix.**
8. **`docs/REVIEW_AGENT_PROMPT.md` + hard `review-diff` step in ship-rolling-candidate** — *(#3)* — **M, NEEDS JUDGEMENT.** Reuse toolkit template.
9. **`bug-hunt` skill** (implements #8 at session granularity; routes survivors to the #7 ledger; security-review path-glob) — *(P1-2, P2-9)* — **M, NEEDS JUDGEMENT.**
10. **`on: push: tags: v*` CI job** running full tests + update test, gating the un-draft step — *(#4)* — **M, NEEDS JUDGEMENT.**
11. **Harden Mac/Linux sing-box smoke into the build scripts as HARD failures** — *(P1-13)* — **M, AUTOMATE after #10.**
12. **Split MainWindowViewModel characterization hash** (strict cross-platform + soft platform-delta) or add a Windows CI job — *(P1-8)* — **M, NEEDS JUDGEMENT.**
13. **Restrict `TOLERATE_FAILURE` to an allowlist + audit log; block unexpected `skipped`; launch watcher from pre-push** — *(P2-3,4)* — **S, AUTOMATE.**
14. **`diagnose-config` terminal "pin as test" step** — *(P1-7)* — **M, AUTOMATE.**
15. **CodeQL weekly workflow** (advisory, NOT required) — *(P2-6)* — **M, AUTOMATE.**
16. **Behavioral/concurrency fixes (v2.44.3 code rework first, then tests)** — failover-restart integration test + fresh-CT fix; `_isStopping`/SemaphoreSlim guard + race test; AutoFailover OnConnected-reset + flap rate-limit; LinuxFirewallManager explicit `isFullTunnel` + degraded-split test — *(#5, P1-5,6)* — **L, NEEDS JUDGEMENT.** The substance the #7 gate must block on.

**Safe to fully automate now:** 1, 3, 4, 5, 13, 14, 15 (and 11 after 10).
**Need user judgement/sign-off:** 2, 7, 8, 9, 10, 12, 16.
**Need the user (not a commit):** 6.
