# Audit batch-1 — verification verdicts + execution plan

Date: 2026-07-09 (Fable, personal source-verification pass)
Input: `plans/audit-import-2026-07-09/01-audit-vector-map-batch1.md` (Google Drive import, written against r36 = a2deeea4)
Verified against: main = 4b413c96 (v2.46.0 stable + urltest verification Core units 1-5)

Every vector re-checked against CURRENT code before recording (the audit itself had
two self-corrections, so no claim was trusted without a file:line). Verdicts:
CONFIRMED / PARTIALLY REFUTED / STALE / VERIFICATION-GAP / NOT-YET-APPLICABLE.

---

## Verdict table

### 1. ApplyAsync lifecycle gate — CONFIRMED, P1 (top implementation candidate)
- Evidence: `VPNRouter.Core/Services/VpnEngine.cs:56-65` — the v2.44.3 comment says the
  gate serializes "public StartAsync, public Stop(), and the post-start failover restart".
  `ApplyAsync` (`VpnEngine.cs:579`) is NOT in that list and its body acquires nothing.
- Real risk: Apply racing Stop/failover can hot-reload a stale/disposed `_singBox`; the
  forceRestart branch re-engages true-split and could do so after a Disconnect.
- Fix shape (smallest diff): acquire `_lifecycleGate` at the top of `ApplyAsync`, check the
  session token (`_sessionCts` alive) inside the gate, bail if the session is gone.
  - Tests: Apply-during-Stop returns false and does not restart; Apply after Disconnect
    does not re-engage the split driver.
- Who: Core+tests, well-testable — safe to implement next (no UI, no network).

### 2. TwoPhaseStartCoordinator Phase B — PARTIALLY REFUTED, downgrade P1 -> P2
- Audit claimed: startTask completing in Phase B short-circuits typed `Connected` and the
  UI can show connected. Reality: the coordinator DOES return `StartTaskCompleted`
  (`TwoPhaseStartCoordinator.cs:217-224`), but the consumer does NOT flip state:
  `MainWindowViewModel.cs:4362-4371` awaits startTask (exceptions surface) and explicitly
  leaves state to `OnEngineStatus`, logging a warning. No false-connected.
- Residual (real, small): that branch never resets `IsConnecting` -> possible stuck
  "Connecting..." spinner if startTask returned cleanly and no status event follows; and
  `OnEngineStatus` flipping state off string statuses is a weak contract.
- Fix shape: reset `IsConnecting` in the `StartTaskCompleted` branch + one targeted test.

### 3. urltest / Auto trust boundary — CONFIRMED P0/P1; FOUNDATION DONE, wiring deferred
- The audit's central claim verified earlier today: `ConfigGenerator.cs:1448-1457` emits
  urltest with one `generate_204` probe — proves generic HTTP only, not RU protocol/DPI
  block, not UDP/app, not blocked-target reachability.
- DONE (main, 477bea12..4b413c96, pure/additive, ~80 tests): `ServerHealthClassifier`
  (phased verdicts incl. `ProtocolHandshakeBlockedLikely`, `OnlyControlWorks`,
  `AnalyzeProviderRisk`), `ServerHealthPhaseMapper` (guardrail: local sing-box failure is
  never a server verdict), `CanaryPolicy` (tiers/TTL/redaction), `ServerRankingScorer`
  (verdict + ASN-diversity), `Strings.ServerHealth.cs` (RU/EN copy incl. the audit's
  "Хост доступен, но VPN-протокол не прошёл проверку").
- REMAINING = R1-R6 in `plans/urltest-verification-deferred-risky-2026-07-09.md`,
  recommended order below. The audit's regression list maps ~1:1 onto existing tests.

### 4. OPEN-DEFECTS ledger drift — CONFIRMED, P1 process (safe to fix NOW, doc-only)
- Evidence of drift in `plans/OPEN-DEFECTS.md` ## Open:
  - `- [ ] P0 Auto-failover self-cancelling restart ... v2.44.3` and
    `- [ ] P0 VpnEngine has zero start/stop/failover synchronization` — both contradicted
    by `VpnEngine.cs:56-65` (the v2.44.3 `_lifecycleGate` + `_sessionCts` implementation
    exists precisely for these). Stale as written; the REAL residual is vector #1 above
    (ApplyAsync not gated) — a narrower, different defect.
  - `- [ ] P2 Path-MTU robustness` — likely superseded by Codex r6-r36 (HealthCheck
    path-MTU probe + MTU 1420 default + manual auto-pick with 1332 floor). Verify + close.
  - `- [ ] P1 AutoFailover ResetCycle no production caller` — marked v2.44.3, re-verify.
- Action: reconcile each entry to `- [x] ... RESOLVED vX.Y.Z (commit)` where history
  proves it; replace the two stale P0s with the precise ApplyAsync entry; keep genuinely
  open items only (clash_api secret P1, Unix kill-switch P1, subscription-leak P1).

### 5. Android versionCode for -rN — CONFIRMED from source; P2 in current practice
- Evidence: `VPNRouter.Android/VPNRouter.Android.csproj:37` — `_VpnVerCore` strips
  `-.*$` before computing `ApplicationVersion`, so 2.46.0-r35 and -r36 share versionCode
  (re-verified today while shipping the v2.46.0 APK: versionCode 2046000).
- Practice makes it P2: Android APKs attach at STABLE cuts (stable-to-stable is
  monotonic). It becomes P1 only if rolling -rN Android releases start.
- Fix shape when needed: widen the encode to reserve rN digits, e.g.
  `code = M*10^7 + m*10^4 + p*10^2 + min(rN,98)` with stable = 99 (stable > any rN,
  monotonic across the cycle). Requires a one-time jump (2046000 -> 20460099-style) —
  design carefully, PackageInstaller only ever allows increases. + `aapt2 dump badging`
  guard in `verify-release-integrity.yml`.

### 6. Custom-config AWG/XHTTP gate bypass — CONFIRMED; P1 on Android, P2 desktop
- Evidence: `CustomConfigInjector.cs` has ZERO references to endpoints/xhttp/
  `SingBoxFeatures` (grep) — raw custom JSON with fork-only constructs is injected as-is.
  `SingBoxFeatures` is consumed only by parsers/ConfigGenerator.
- Severity split: all 3 desktops bundle the lx fork since v2.45.x (constructs supported;
  FATAL only if a user swaps in an official binary). Android has a custom-config surface
  (`AndroidApp.CustomConfig.cs`) on upstream libbox (no AWG/XHTTP) -> real opaque-FATAL.
- Fix shape: in `CustomConfigInjector` validation, detect top-level `endpoints` (type
  wireguard/awg) and `transport.type == "xhttp"`; if the active core lacks the tag ->
  friendly "unsupported on this core" validation error instead of sing-box FATAL.
  Pure validation + tests — safe to implement next.

### 7. Unix kill-switch empty-list = full-tunnel — CONFIRMED + ALREADY LEDGERED
- Already tracked: OPEN-DEFECTS ## Open P1 line (LinuxFirewallManager/MacFirewallManager
  empty processNames arms global kill-switch) + documented in Core CLAUDE.md (audit P1-6).
- No new verification needed; schedule the fix (explicit routing-intent parameter to
  firewall managers; empty list must not mean full-tunnel unless mode confirms it) as
  the audit's Batch 3. Touches fail-closed firewall logic -> needs Linux VM live-verify
  (debian-xfce), not just unit tests.

### 8. Android per-app package filtering e2e — VERIFICATION GAP (not refutable from source)
- By design Android uses `VpnService.Builder` package filters, invisible to Core route
  tests. Action = add e2e modes/logging (audit Batch 4); needs the Mac-hosted phone.

### 9. Android LAN route invariant after TunOptions — VERIFICATION GAP
- Same class: live-proof on device (`ip route get $LAN_IP`, LAN probe while connected).
  Audit Batch 4, device required.

### 10. AWG / realtime-games / DNS cluster — PARTIALLY STALE
- WSAENOBUFS burst-drop + MTU inversion: FIXED v2.45.0-r11 (ledger shows [x]).
- Path-MTU P2: likely superseded by Codex HealthCheck probe + auto-pick (see #4).
- Still real: DeepVerify AWG/XHTTP parity (deep verify can't spawn AWG/XHTTP configs ->
  today those land as Unknown via the mapper's local-infra guardrail, which is correct
  but means AWG servers are never deep-verified). Fold into R1: add an explicit
  `UnsupportedByVerifier` phase outcome so UI can say "not verifiable on this build"
  instead of silently Unknown.
- AWG DNS leak audit (`plans/tz-codex-awg-dns-leakprotection-audit-2026-06-28.md`):
  keep as its own open thread; not re-verified in this pass.

### 11. AOT-safe JsonNode Android post-processing — NOT-YET-APPLICABLE
- Android csproj has NO `PublishTrimmed`/`RunAOTCompilation`/R8 props today (grepped
  while building the v2.46.0 APK); B4 (Profiled AOT) is still a pending backlog task.
- Action: fold the "source-gen-aware JsonNode serialization" requirement into the B4
  task itself; zero work now.

---

## Execution plan (recommended order)

### Wave 0 — safe now, no approval needed (Core/doc + tests only)
1. **Ledger reconcile** (#4, #10-stale) — doc-only; replaces stale P0s with the precise
   ApplyAsync entry. Restores trust in the cut gate.
2. **ApplyAsync lifecycle gate** (#1) — Core + tests; the one real P1 code defect this
   audit surfaced that is verified and testable without live/UI.
3. **Custom-config fork-gate validation** (#6) — pure validation + tests.
4. **IsConnecting reset on StartTaskCompleted** (#2 residual) — 2-line + 1 test.

### Wave 1 — urltest R-sequence (behavior-changing; each needs explicit go per the
deferred file; live verify on windows-brat, never dev box)
- **R1** typed DeepVerify failure phases (+ `UnsupportedByVerifier` for AWG/XHTTP parity,
  #10) -> mapper reads types, not strings. Gate: App characterization hash re-pin.
- **R2** wire mapper->classifier into the probe pipeline + server-row verdict chips + RU
  copy (strings already exist). Gate: brat MCP verify + visual-diff baseline refresh.
- **R5** Auto ranking consumes verdicts (penalize blocked-likely, ASN diversity) + rename
  Auto as quick web selector + show selected member/test age.
- **R3** ASN metadata (offline DB only) -> feeds `AnalyzeProviderRisk`.
- **R4** blocked-target canaries (via-VPN only by default, opt-in direct, TTL'd list).
- **R6** release: user-gated as always.

### Scheduled batches (from the audit, unchanged where still valid)
- Batch 3: Unix kill-switch intent (#7) + DNS-restore regression tests — Linux VM verify.
- Batch 4: Android gates (#5 versionCode encode, #8 per-app e2e, #9 LAN route) — device.
- B4-time: #11 AOT JsonNode hardening rides with the AOT task.

## What this pass did NOT verify (honest gaps)
- #8/#9 are live-device claims — unverifiable from source by design.
- AWG DNS leak state (#10) — separate audit thread, not re-read here.
- `ResetCycle` production-caller status (#4) — flagged for the ledger reconcile to check.
