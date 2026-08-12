# Phase 2 — Executable post-ship MCP gate

**Owner**: Codex root session 2026-08-12  
**Branch**: `codex/post-ship-mcp-gate`  
**Roadmap ref**: `AGENTS.md` rules 1a, 12 and 13  
**Effort**: 3-5 hours  
**Risk**: MEDIUM — tooling installs and drives a published build only on the fixed WINBRAT VM; it must fail closed and never expose subscription data  
**Blast radius**: remote verification scripts, their source-contract tests and the post-ship skill; product runtime code unchanged  
**Rollback**: revert the task commits or delete the branch; the runner always disconnects WINBRAT in `finally`

## Why

The repository already has headless page screenshots/size diffs and a safe
remote WINBRAT UIA driver, but the release workflow still depends on an agent
manually composing individual commands. That allowed v2.49.0-r7 to be
published and initially described as ready before the existing Tailscale-based
WINBRAT path was exercised. One executable fail-closed gate must combine the
visual checks with the real subscription/TUN lifecycle and return a single
machine-readable PASS/PARTIAL/FAIL result.

## What

- Extend `tools/brat-verify.ps1` with the already live-proven, redacted
  `state`, fixed-destination `probe`, and sanitized `lifecycle` actions.
- Add `tools/post-ship-verify.ps1` as a thin coordinator over existing tools:
  page screenshot/visual-diff tests, CI/identity, SHA-verified deploy, two
  Connect/Stop cycles, TUN/route checks, HTTPS/UDP probes and clean teardown.
- Add source-contract tests that pin fixed targeting, redaction, no local UI or
  WinRM fallback in the coordinator, unconditional cleanup and nonzero exit on
  incomplete verification.
- Update both tracked post-ship skill copies so the coordinator is the default
  path and manual checklist commands remain feature-specific additions.

```diff
- ship -> manually compose UIA commands -> prose verdict
+ ship -> tools/post-ship-verify.ps1 -> JSON evidence + exit code
```

## How

1. Reuse the current fixed `100.115.182.0`/`WINBRAT` identity boundary and the
   live-proven state/probe/lifecycle implementations from the existing
   stability harness branch.
2. Keep the coordinator remote-API-free: it may call only repository test
   commands and `tools/brat-verify.ps1`.
3. Require two successful TUN lifecycles and clean teardown; keep fixed UDP
   probe failures visible and non-green instead of silently retrying them away.
4. Emit only counts, enums, booleans and timings to ignored artifacts; never
   print config values, endpoints, raw routes, process IDs or raw log lines.
5. Validate syntax/contracts locally, then execute the exact gate against the
   published v2.49.0-r7 binary on WINBRAT.

### Tests written

- `PostShipVerifier_DelegatesRemoteWorkAndAlwaysCleansUp` — coordinator cannot
  use local/remote UI APIs directly and disconnects in `finally`.
- `PostShipVerifier_RunsVisualAndTwoCycleVpnGates` — pins page/visual tests,
  deploy, two cycles, route/TUN/probe/lifecycle requirements and exit status.
- `BratVerify_StateProbeLifecycleActions_AreFixedTargetAndRedacted` — pins the
  remote driver's sanitized action contract.

### Verification approach

Run PowerShell parser validation, focused tooling tests, the full solution
build/test suite, page screenshots and Windows visual diff. Then run the new
coordinator for `2.49.0-r7` on fixed WINBRAT using the already configured test
subscription. Evidence is stored only under ignored `artifacts/post-ship/`.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: full suite passes; new tooling contracts included.
- [ ] **Gate 3 — Docs**: brief Outcome filled; both post-ship skill copies match.
- [ ] **Gate 4 — Self-review**: ponytail + Qwen read-only review; bug-hunt/security pass for remote process/file/network tooling.
- [ ] **Gate 5 — MCP verify**: exact published r7 run on fixed WINBRAT with headless screenshots and sanitized evidence; no local fallback.
- [ ] **Gate 6 — Characterization diff**: N/A — tooling only, no god-file split.

## Outcome (filled after implementation)

**Status**: IMPLEMENTED — the gate is ready; the tested r7 candidate remains blocked by UDP dataplane failure
**Commits**: `116c969b` (brief) + implementation commit containing this outcome
**Pushed**: `codex/post-ship-mcp-gate`, draft PR #146
**Test deltas**: 15 focused post-ship contracts; full suite 2758 passed, 3 skipped, 0 failed (2761 total)
**Files changed**: post-ship/BRAT/CI/deploy scripts, isolated screenshot-test safety, Windows CI contract job, mirrored skill/checklists and documentation

**Gate results:**
- [x] Gate 1: solution Release build — 0 warnings, 0 errors
- [x] Gate 2: full suite — 2758 passed, 3 skipped, 0 failed; focused tooling contracts — 15/15
- [x] Gate 3: both skill trees are byte-identical; parser and forbidden remote-screenshot contracts pass
- [x] Gate 4: ponytail review complete; three independent correctness/security/test reviews report no P0/P1. Qwen read-only review was attempted but unavailable because its local runtime repeatedly timed out
- [x] Gate 5: exact published v2.49.0-r7 ran for two cold cycles on fixed WINBRAT. TUN, proxy HTTPS, hold, disconnect, lifecycle and sanitized log checks passed; all UDP sizes timed out, so the new gate correctly returned nonzero FAIL and left WINBRAT disconnected
- [-] Gate 6: N/A — tooling only

**Surprises encountered**:
- The current main branch retained the remote deploy/UIA verifier but not the
  later state/probe/lifecycle actions, despite those actions having passed a
  prior two-hour WINBRAT soak on a stacked branch.
- Existing screenshot tests could initialize background services and touch the
  host TUN state; the suite now isolates data/process seams and explicitly
  disables background services only under the test switch.
- Remote desktop screenshots are not a safe headless proof on WINBRAT. Visual
  coverage now runs in the isolated screenshot tests, while the live VM path
  uses semantic UIA state plus route/proxy/lifecycle evidence.
- A nominal HTTPS request from the WinRM process could leave through `direct`.
  The gate now requires the Clash proxy delay endpoint and an observed outbound;
  UDP remains the one failing candidate check.

**Follow-ups spawned**:
- Diagnose the r7 UDP proxy path before any next rolling candidate or stable
  promotion. Do not describe r7 as fully verified while this gate is red.
