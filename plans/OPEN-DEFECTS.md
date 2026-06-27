# Open defect ledger

Canonical open-defect ledger read by `tools/check-open-p0.ps1` (the cut-stable
P0 gate, audit item 7, 2026-06-25). The `bug-hunt` skill appends survivors to
the **## Open** section below; the cut-stable gate (`cut-stable` skill pre-flight
condition 6.5) **BLOCKS a stable cut** while any `- [ ]` P0/P1 remains open here,
unless `tools/check-open-p0.ps1 -Waive '<reason>'` is run for that specific cut.

This file exists because a P0 found by the adversarial bug-hunt, written down and
deferred, reached v2.44.0/.1 stable and bit a user (auto-failover teardown, diag
20260624-235243) — nothing connected the deferred-defect record to the cut gate.

**Resolve** an entry: change `- [ ]` to `- [x]` and append `RESOLVED vX.Y.Z`.
Only the `## Open` section is gated; `## Resolved` is history. Keep entries to one
line: `- [ ] **P0** — <symptom> — <file:line or plan ref> — <target version>`.

## Open

- [ ] **P0** — Auto-failover self-cancelling restart: on a genuine outage `WireFailoverWithStop` restarts under the `Stop()`-cancelled `_probeCts`, so the replacement never starts and the engine is left stopped — `VPNRouter.Core/Services/VpnEngine.cs:1165` — v2.44.3
- [ ] **P0** — VpnEngine has zero start/stop/failover synchronization: a Disconnect racing a failover restart can resurrect the tunnel after disconnect — `VPNRouter.Core/Services/VpnEngine.cs` (no lock/SemaphoreSlim/_isStopping) — v2.44.3
- [ ] **P1** — AutoFailover `ResetCycle()` has no production caller: after 3 lifetime failovers auto-failover gives up permanently ("Все серверы недоступны") until app restart — `AutoFailoverEngine.cs:352` — v2.44.3
- [ ] **P1** — clash_api exposed with no `secret`: on Android any installed app can read live connection metadata / issue control calls — `VPNRouter.Core/Models/VPNConfig.cs:710` — TBD
- [ ] **P1** — LinuxFirewallManager / MacFirewallManager treat an EMPTY processNames list as "arm global kill-switch": a split-tunnel user with a 30s scan timeout can have the whole host egress dropped — `Platform/Linux/LinuxFirewallManager.cs` — TBD
- [ ] **P1** — AutoFailover persists the resolver-aggregated server list (subscription-leak class): `_store.Save(_settings)` serializes the aggregate into `vless.servers` YAML — `AutoFailoverEngine.cs:202` — TBD
- [ ] **P2** — build-singbox-lx.ps1 doesn't assert the wireguard-go fork HEAD == `$WG_COMMIT` / the go.mod `replace => ./submodules/wireguard-go` path (mostly mitigated 2026-06-28 by the new with_awg/with_xhttp Tags + `check` smoke assertion, but the explicit pin is still absent) — `tools/build-singbox-lx.ps1:58` — TBD
- [ ] **P2** — No SHA256 integrity pin of the bundled sing-box-lx.exe; build.ps1 `-SingBoxPath` override copies the binary with zero version/tag/checksum validation before bundling — `tools/build-singbox-lx.ps1:29` / `build.ps1:311` — TBD
- [ ] **P2** — LeakProtection.ValidateOutboundServersScopeAware never cross-checks the AWG peer endpoint IP (iterates only `config.Outbounds`; the AWG egress peer lives in `config.Endpoints[].Peers[]`) — defense-in-depth only, benign today — `VPNRouter.Core/Services/LeakProtection.cs:527` — TBD

## Resolved (history)

- [x] **P0** — Auto-failover false-positive teardown of a healthy connection (post-start delay-test 503 → tore down a working server) — RESOLVED v2.44.2 (warmup-confirmed gate, `VpnEngine.ShouldAutoFailoverAfterProbe`)
