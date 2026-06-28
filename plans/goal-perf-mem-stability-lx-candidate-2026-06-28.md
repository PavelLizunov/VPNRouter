# Goal — perf/memory/stability hardening + sing-box-lx core → one rolling candidate

## Trigger
Codex read-only bug-hunt (perf / memory / connection-stability), 2026-06-28
(`plans/claude-code-readonly-bughunt-perf-memory-stability-2026-06-28.md`),
independently **validated against the tree** by a 4-lens verify workflow
(`wf_f915e575`, every cited `file:line` re-confirmed). This goal lands the
validated fixes **and** the new sing-box-lx core (AWG + XHTTP) in a **single
rolling `-rN` candidate** — the perf/mem/stability fixes are all managed C#
(VPNRouter.App ViewModels + VPNRouter.Core/Services), fully independent of the
core swap, so they ride the same candidate with zero interaction.

## Validation result (16 findings)
- **10 real** to fix here (2 ship-blocking + 8 non-blocking)
- **2 already-tracked** in `plans/OPEN-DEFECTS.md` (deferred P2s — no new action)
- **1 false-positive** (rejected with evidence)

| ID | Finding | Cat | Sev | Ship-block | Cost | Site |
|---|---|---|---|---|---|---|
| S1 | External sing-box treated as VPNRouter (false "connected" + takeover `Kill`s unrelated tunnel) | stability | P1 | **YES** | L | `RuntimeStatusDetector.cs:32`, `OrphanCleanup.cs:88`, `MainWindowViewModel.cs:3897/3961` |
| S2 | StrictDNS failover commits flag + fires event before hot-reload success (silent desync, no retry) | stability | P1 | **YES** | M | `HealthMonitor.cs:635/642/653` |
| M1 | TgProxy `StatsUpdated` lambda accumulates per toggle; `_tgProxy` never disposed | memory | P1 | no | S | `MainWindowViewModel.cs:6184`, `Dispose():7094` |
| M2 | Zapret `ImmediateExitDetected` handler + `_zapret` not detached/disposed in `Dispose()` | memory | P1 | no | S | `MainWindowViewModel.cs:4638/5055/5428` |
| M3 | Zapret probe `System.Threading.Timer` not stopped in `Dispose()`; callback not `_disposed`-guarded | memory | P1 | no | S | `MainWindowViewModel.cs:5129/5151` |
| M4 | Toast CTS (`_zapretAvBlockToastCts`/`_rulesToastCts`) + TgProxy toast `Task.Delay` not cleaned on dispose | memory | P2 | no | S | `MainWindowViewModel.cs:1324/2016/6483` |
| F1 | TgProxy detector runs `GetActiveTcpListeners()` every 2s tick even when TgProxy disabled | perf | P1 | no | S | `RuntimeStatusDetector.cs:61`, `MainWindowViewModel.RuntimeStatus.cs:122` |
| F2 | Conn-stats poll deserializes the full `/connections` array every 2s for totals+count; no visibility throttle | perf | P1(partial) | no | M | `MainWindowViewModel.ConnStats.cs:107`, `ClashSingBoxApi.cs:244` |
| F3 | AutoSelect resolution: extra HTTP round-trip + allocating LINQ sort every stats poll | perf | P2 | no | M | `MainWindowViewModel.ConnStats.cs:161/184` |
| F4 | FreeConfigs visible cap (`Take(300)`) applied AFTER building+sorting+grouping all VMs | perf | P2 | no | M | `FreeConfigsPageViewModel.cs:1901` |

### Already-tracked (deferred — no new action)
- AWG endpoint **content** validation (tag-only) — `OPEN-DEFECTS.md` (LeakProtection.cs:284). Residual reach: raw imported sing-box JSON only (generated path is parser-validated). P2.
- QUIC-reject keyed to `endpoints.Count>0` not capability — `OPEN-DEFECTS.md` (ConfigGenerator.cs:140). Latent (AWG is the sole UDP-native endpoint today). P2.

### Rejected (false-positive)
- **FreeConfigs "O(n²) batching"** — `queue` is a materialized `List<FreeConfigEntry>`
  (`FreeConfigsPageViewModel.cs:439` `.ToList()`); in .NET 8 `Skip(i)` over an `IList`
  returns an indexed `ListPartition`, so `Skip(i).Take(batch)` is O(1) seek, total O(N)
  not O(N²). Per-slice alloc is trivial next to the per-batch TCP/TLS deep-verify. No change.

## Definition of done (acceptance gate for the candidate)
1. Phase 1–3 fixes landed; **focused tests green** for each behavior changed.
2. `dotnet build VPNRouter.sln -c Release` → 0 errors; full regression green.
3. **lx binary built + verified** (`tools/build-singbox-lx.ps1`: Tags contain `with_awg`+`with_xhttp`, `check` on an AWG config = 0 errors).
4. **Candidate `-rN` built bundling the lx core + all fixes** (`build.ps1 -SingBoxPath`), CI green (14 desktop assets / 16 w/ Android), pre-commit + pre-push gates pass.
5. MCP verify of the touched UI surface (connect/disconnect, TgProxy/Zapret toggles, FreeConfigs page) — not just "tab renders".
6. (separate, external) Live AWG pilot: tester selects the AWG server via subscription, plays Roblox from RU.

## Execution phases

### Phase 0 — core readiness — ✅ DONE
AWG + XHTTP client complete, bug-hunt-hardened (commits `13847450`, `8b2d06d2`,
`e2ff6c69`, `c25d86d6`); real vpnctl `awg://` passed offline pre-flight
(`Parse → Generate → sing-box-lx check` = 0 errors). lx binary verified.

### Phase 1 — ship-blocking stability (land FIRST)
**S2 — StrictDNS failover reorder (M).** In `HealthMonitor.ReconcileStrictDnsFailover`,
build the target-state config via an explicit override arg WITHOUT mutating
`_strictDnsFailedOver`; call `TryHotReloadViaApi` first; only on `reloaded==true`
set the flag + fire `StrictDnsFailoverChanged`. On failure leave the flag so the
next healthy tick retries (streak counters persist). Apply symmetrically to the
re-arm path. *Tests:* proxy-unreachable past threshold + `ReloadConfigAsync=false`
→ flag NOT set, no event; later `reload=true` applies it; symmetric re-arm.

**S1 — sing-box ownership tagging (L).** Tag VPNRouter-managed sing-box (pid file,
or reuse the `TunOwnershipLock` owner pid). In `OrphanCleanup.KillByName("sing-box")`
on the user-takeover paths (`respectTunLock:false`), only kill a pid that matches
the lock owner OR whose command line references `AppPaths.SingBoxConfigPath`
(`QueryFullProcessImageName` + `GetCommandLine` — the routed-exe approach from
`bfc661f7`). Gate `SyncConnectedWithVpnRuntime` on the same ownership check instead
of bare process presence (else show Idle). Keep startup OrphanCleanup conservative
(already TUN-lock-respecting). *Tests:* unrelated sing-box → NOT IsConnected, NOT
killed by a takeover sweep; VPNRouter/Service-owned one still detected + stoppable.

### Phase 2 — lifecycle / memory (Dispose hygiene)
All four extend `MainWindowViewModel.Dispose()` (cs:7094) in the existing
try/catch-with-debug pattern. The X-close path calls `Dispose()` directly (not
`Quit()`), so these matter on window rebuild/close.

- **M1 (S).** Hoist the TgProxy stats handler to a named `OnTgProxyStats(string)`,
  subscribe once in the `??=` branch; in `Dispose()` `-= OnTgProxyStats; _tgProxy.Dispose(); _tgProxy = null`.
- **M2 (S).** In `Dispose()` `_zapret.ImmediateExitDetected -= OnZapretImmediateExit; _zapret.Dispose(); _zapret = null`.
- **M3 (S).** Call `StopZapretProbeElapsedTimer()` from `Dispose()`; add `if (_disposed) return;` at the top of the timer callback.
- **M4 (S).** Cancel+dispose `_zapretAvBlockToastCts`/`_rulesToastCts` in `Dispose()`; bump `_tgProxyToastToken` (or CTS-cancel) so the pending `ContinueWith` no-ops.
- *Tests:* repeated TgProxy start/stop → one handler; `Dispose()` detaches handlers + disposes managers; late timer/toast callbacks after dispose are no-ops.

### Phase 3 — perf hot-path (the 2s poll)
- **F1 (S).** Only call `IsTgProxyRunning` when TgProxy is enabled in settings; do
  NOT default `tgPort` to 1443 when unconfigured. *Test:* probe skipped when TgProxy
  off + VPN running; explicit state transition still forces a refresh.
- **F2 (M).** Parse `/connections` with `Utf8JsonReader`/`JsonDocument` to read
  `downloadTotal`/`uploadTotal` + array length without allocating a
  `List<JsonElement>` per connection (or skip the array if only totals+count needed).
  Add a visibility/minimized throttle. Keep the in-flight guard. *Tests:* zero/failure
  snapshot re-baseline preserved.
- **F3 (M).** Cache `Dictionary<memberTag, ServerViewModel>` rebuilt on
  `SubscriptionServers` CollectionChanged; look up by tag (no per-poll LINQ sort);
  throttle the `GetGroupNowAsync` round-trip below the traffic cadence. *Tests:*
  names with `-` resolve; longest suffix wins; cache invalidates on list change.
- **F4 (M).** Dedup-by-host on raw `FreeConfigEntry` (keep best latency/status),
  order, `Take(300)`, THEN `Select(e => new FreeConfigItemViewModel(e))`. Caps VM
  allocation at ≤300. *Tests:* best entry per host still wins; list capped at 300.

### Phase 4 — ship the candidate (the pre-release with the new core)
1. `powershell -File tools/build-singbox-lx.ps1` → `sing-box-lx.exe` (asserts
   `with_awg`+`with_xhttp` Tags + `check`).
2. Bump `AppVersion.Version` to the candidate (proposed **`v2.45.0-r1`** — minor:
   new core + AWG/XHTTP protocol; final version is the user's call at ship time).
3. `powershell -File build.ps1 -Version "2.45.0-r1" -SingBoxPath "<lx-exe>" -Upload`
   → Windows artifacts **bundling the lx core + all Phase 1–3 fixes**; tag triggers
   Mac/Linux CI. NOTE: Mac/Linux build scripts download upstream SagerNet sing-box —
   to ship a fully-lx fleet, mirror the lx build in `build-mac.sh`/`build-linux.yml`
   (deferred; the Windows tester is the pilot, so Windows-lx is enough for the pilot).
4. Finalize prerelease notes, restore previous stable as Latest, MCP verify, report.

## Notes
- This candidate is a **prerelease (`-rN`)** — autonomous per CLAUDE.md rule #1.
  Stable cut stays gated on the user's explicit "cut" + the live update gate.
- The 2 ship-blocking P1s (S1, S2) are added to `plans/OPEN-DEFECTS.md` so the
  cut-stable gate blocks a stable promotion until they're fixed + marked resolved.

## Outcome (2026-06-28)
**Phases 1–3 implemented + tested + pushed (all CI-green on main):**
- S2 StrictDNS failover reorder — `f22b7351`
- S1 sing-box ownership (ProcessOwnership) — `b60d33fe`
- M1–M4 Dispose hygiene — `aacb393b` (+ Linux `#if` fix `086c1438`)
- F1 TgProxy probe gate — `6c2994a6`
- F2/F3 streaming `/connections` + single-pass auto-select — `69affadd`
- F4 FreeConfigs cap-before-VM — `d18002cf`
- The two ship-blocking P1s (S1, S2) marked RESOLVED in `OPEN-DEFECTS.md`.
- Deferred micro-opts (F2 visibility throttle, F3 HTTP throttle) → `OPEN-DEFECTS.md`.

**Remaining: Phase 4** — build the lx core (`tools/build-singbox-lx.ps1`) and ship
the `-rN` candidate (`build.ps1 -SingBoxPath`) bundling the new core + all these
fixes. Gated on the user's "собирай кандидат" + (for the AWG pilot) vpnctl's
UA-gate + `hidden=0`.

## Cross-refs
- Source report: `plans/claude-code-readonly-bughunt-perf-memory-stability-2026-06-28.md`
- AWG/XHTTP fork: `plans/amneziawg-fork-implementation-plan-2026-06-27.md`, `plans/OPEN-DEFECTS.md`
- Roblox tester goal: `plans/roblox-tester-fix-goal-2026-06-27.md`
