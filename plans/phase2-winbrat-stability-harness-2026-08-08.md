# Phase 2 — WINBRAT connection stability harness

**Owner**: Codex root session 019fc457-6058-7a21-a9e6-738b22054870  
**Branch**: `codex/winbrat-stability-harness`  
**Roadmap ref**: `plans/qol-interface-and-recovery-audit-2026-08-08.md` §6/§10  
**Effort**: 3-5 hours  
**Risk**: MEDIUM — remote lifecycle actions run only on the fixed test VM; probe and evidence surfaces must not expose configuration or endpoint data  
**Blast radius**: `tools/brat-verify.ps1`, one new coordinator, one contract-test file and audit ledgers; product/runtime code unchanged  
**Rollback**: revert the task commits or delete the branch; `-Mode Cleanup` returns WINBRAT to a disconnected state

## Why

The current remote verifier proves that a released build opens and that a UI
action can be invoked, but it cannot distinguish a stable VPN dataplane from a
healthy window. This is especially important in Split Tunnel: a PowerShell web
request may go direct and create a false PASS. The smallest useful addition is
a remote-only, redacted lifecycle and paired HTTP/UDP harness that first proves
its fixed probes route through `VPNRouter-TUN`, then measures cold reconnects
and a bounded soak without reading or changing the user's configuration.

## What

- Extend `tools/brat-verify.ps1` with three fixed-target actions:
  - `state`: compact JSON containing only owned GUI/core counts, TUN state,
    aggregate resources and `Tunnel|Direct|Unknown` route scope;
  - `probe`: fixed allowlisted HTTPS 204 and valid low-rate STUN transactions,
    with fixed packet sizes and enum-only failures; it refuses dataplane claims
    unless route scope is `Tunnel`;
  - `lifecycle`: map known recent log patterns to sanitized event enums and
    counts without returning raw log lines, paths, process identifiers or
    endpoints.
- Add `tools/brat-stability.ps1` with `ColdCycles`, `Soak` and `Cleanup` modes.
  It is a local coordinator only and may call no WinRM, process, screen or UI
  API directly; all remote work stays behind `brat-verify.ps1`.
- Add `VPNRouter.Tests/BratStabilityToolingContractTests.cs` to pin fixed-target
  ownership, redaction, action sets, coordinator isolation and fail-closed
  route gating.
- Record the tooling limitation and its disposition in `plans/OPEN-DEFECTS.md`.

## How

1. Add read-only remote state and fixed-destination route classification.
2. Add an HTTPS control probe plus RFC-compatible STUN binding requests at
   64 bytes for control and 64/512/1200/1392 bytes for boundary checks.
3. Add sanitized lifecycle classification over the bounded recent log window.
4. Add a mutex-protected coordinator with unconditional disconnect cleanup and
   JSONL summaries under ignored `artifacts/brat-stability/`.
5. Run syntax/contract tests, full build and full test suite.
6. Run exact Qwen read-only review and a manual security pass because the
   repository's named `security-review` skill is not installed in this session.
7. On fixed WINBRAT only, run identity/state, cleanup, ten cold cycles and — if
   the fixed probes are proven `Tunnel` — the two-hour soak. A `Direct` or
   `Unknown` route blocks dataplane assertions but does not invalidate the
   separately reported connect/disconnect lifecycle result.

### Tests written

- `BratVerify_StateAndProbeActions_AreFixedTargetAndRedacted`
- `BratVerify_ProbeRequiresTunnelRouteBeforeNetworkSamples`
- `BratVerify_LifecycleNeverReturnsRawLogLines`
- `BratStability_CoordinatorDelegatesAllRemoteWorkToBratVerify`
- `BratStability_CleanupIsUnconditionalAndEvidenceStaysIgnored`

### Verification approach

PowerShell parser validation and source contracts pin the security boundary;
the normal .NET build/test gates protect repository integration. Live evidence
is accepted only from `WINBRAT` (`100.115.182.0`) through the existing verified
session helper. No screenshots or raw log output are collected by default.

## Verification gate

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [x] **Gate 2 — Tests green**: full suite passes; new tooling contracts included.
- [x] **Gate 3 — Docs**: this Outcome and `OPEN-DEFECTS.md` are current; README/zone docs updated only if the operator contract changes.
- [x] **Gate 4 — Self-review**: ponytail review plus exact Qwen and manual security review; named `simplify`/`security-review` skills are unavailable.
- [x] **Gate 5 — Remote brat verify**: identity/state/cleanup and cold-cycle result recorded; soak runs only with tunnel-scoped probes.
- [x] **Gate 6 — Characterization diff**: N/A — tooling only, no god-file split.

## Acceptance

- Before every cold cycle: exactly one owned GUI, zero owned cores, TUN not Up.
- After Connect: one owned core and TUN Up within the existing 120-second budget;
  the visible CTA is Disconnect.
- Each cycle remains up for 30 seconds and returns to zero owned cores/TUN not
  Up within 30 seconds after Disconnect. Any lifecycle miss fails the run.
- HTTP/UDP success is reported only when the fixed destinations resolve through
  the TUN interface. Split-mode direct probes are `BLOCKED`, never PASS.
- Ten cold cycles and the two-hour soak preserve locale, routing mode, selected
  row and configuration files. No screenshots, raw endpoints, raw log lines,
  PIDs, paths, IP/MAC/route data or secrets enter the JSONL evidence.
- STUN is described only as a low-rate UDP/packet-size sentinel, not Dota 2 or
  Roblox emulation. Endpoint auto-failover remains measurement-gated.

## Outcome (filled after implementation)

**Status**: IMPLEMENTED AND LIVE-VERIFIED — implementation, cold cycles and
the two-hour soak pass; remote GitHub CI remains the post-push gate.

**Commits**: prerequisite brief `e15dc431`; implementation `9036a632`;
final Outcome/PR reference in the follow-up docs commit.

**Pushed**: `codex/winbrat-stability-harness`; draft PR
[#123](https://github.com/PavelLizunov/VPNRouter/pull/123), stacked on the QoL
audit branch/PR #118.

**Test deltas**: +5 tooling contract tests; 5/5 pass.

**Files changed**: `.gitignore`, `tools/brat-verify.ps1`, new
`tools/brat-stability.ps1`, new `BratStabilityToolingContractTests.cs`, test
zone documentation and defect ledger

**Gate results:**

- [x] Gate 1: solution Release build passes with 0 warnings / 0 errors after
  normal restore in the fresh worktree
- [x] Gate 2: tooling contracts pass 5/5 and pre-commit scope passes 185/185.
  The non-elevated local full Windows run passed 2,679/2,706 and hit 25 existing
  ProgramData/global-lock environment failures; the authoritative GitHub run
  then passed the full Linux suite, Windows characterization and Go
  ([run 31251254061](https://github.com/PavelLizunov/VPNRouter/actions/runs/31251254061));
  placeholder grep also passed
  ([run 31251255062](https://github.com/PavelLizunov/VPNRouter/actions/runs/31251255062))
- [x] Gate 3: brief, test-zone map and `OPEN-DEFECTS.md` updated with final
  cold/soak evidence and explicit measurement boundaries
- [x] Gate 4: ponytail and manual security reviews complete; exact
  `qwen3.8-max-preview` design review and bounded implementation-contract review
  pass. A full-diff Qwen attempt timed out and is not counted as a pass
- [x] Gate 5: identity/deploy/clean-state pass; one shakedown and 10/10 cold
  cycles pass with Tunnel route, HTTPS 204 and STUN sizes 64/512/1200/1392.
  The 121-minute soak completed 357 paired samples (`355 HH`, two isolated
  `HNotU`, zero incidents), two successful boundary sweeps, zero fatal/unknown
  lifecycle errors, no restart/failover and a clean final GUI/core/TUN state
- [-] Gate 6: N/A — tooling only

**Surprises encountered**:

- Split Tunnel makes an ordinary shell probe untrustworthy unless its route is
  proven to traverse the VPN; the existing verifier did not expose that proof.
- Remoting automatically appended `PSComputerName`/runspace metadata to returned
  objects; every new action now reconstructs a strict local output schema.
- The first lifecycle serializer returned a remoted hashtable shape that could
  not be safely converted; event counts now cross the boundary as typed pairs.
- Self-review found and fixed cleanup-without-mutex ownership and a per-file
  rather than whole-window lifecycle cap before commit.
- Two isolated single-request STUN timeouts occurred roughly one hour apart
  while TUN, route and HTTPS remained healthy; both recovered on the next
  sample. This is measurement evidence, not endpoint/product attribution.
- The WinRM control channel reconnected once near the end; subsequent VPN
  probes, final boundary sweep and cleanup passed, so it is not counted as a
  VPNRouter dataplane incident.

**Follow-ups spawned**:

- Protocol-row automation (AWG then HY2/TUIC) waits for a non-secret stable row
  selector; visible row names currently include endpoint data.
- Sustained game-like UDP cadence waits for an operator-controlled echo target.

**Lessons for methodology doc**:

- Full Windows test results must distinguish product regressions from existing
  non-elevated ProgramData/global-mutex harness constraints, then use the normal
  GitHub runner as the full-suite authority; never relax production ACLs to make
  a local gate green.
