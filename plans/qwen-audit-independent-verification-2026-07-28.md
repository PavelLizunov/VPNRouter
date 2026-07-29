# Qwen audit — independent adjudication (Prompt P00)

Date: 2026-07-29
Adjudicating engine: Qwen Code (code-only, read/search/edit-report mode)
Checkout under review: `C:\Project\VPNRouter-qwen-p00-2026-07-29`
Commit SHA: `b39a28c32fae26838e615b5080d183dc33ee551b`
Audit source: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (audit branch
`codex/qwen-full-app-audit-2026-07-28`, PR #48)
Plan: `plans/qwen-audit-remediation-prompt-pool-2026-07-28.md` §0 + Prompt P00
External checkpoint (treated as hypotheses only):
`C:\Users\x3d_mutant\.codex\attachments\c4709ba6-...\pasted-text.txt`

Method: five parallel read-only evidence groups ran against the checkout; the
main agent then re-opened and personally re-verified every P0/P1 survivor and
every refutation at the cited `file:line`. No builds, tests, binaries, services,
installers, firewall tools, or live checks were run. No product code, tests,
workflows, AGENTS/CLAUDE files, OPEN-DEFECTS.md, or audit inputs were modified.
The only write is this report.

Notation: `[FACT]` = read directly in the checkout at the cited line.
`[INFER]` = reasoning about runtime consequence (flagged, not executed).

---

## 1. Executive summary

All 39 audit IDs were independently adjudicated against commit `b39a28c3`.
Every original claim was first attacked (guard/owner/parser-constraint/platform-
contract search) before being confirmed.

Verdict counts:

| Verdict | Count |
|---|---:|
| CONFIRMED | 30 |
| PARTIALLY_CONFIRMED | 4 |
| REFUTED | 5 |
| STALE | 0 |
| DUPLICATE | 0 |
| INCONCLUSIVE | 0 |
| **Total** | **39** |

Final severity counts (independent reassessment; original severity NOT inherited):

| Final severity | Count | IDs |
|---|---:|---|
| P0 | 0 | — |
| P1 | 13 | UPD-1, UPD-2, FAIL-1, DATA-1, FLOW-1, CLI-1, CLI-2, AND-1, SUP-1, SEC-1, SEC-2, OBS-1, ZAP-1 |
| P2 | 18 | FW-1, FW-2, CFG-1, CFG-2, PROTO-1, DATA-3, DATA-4, DATA-6, NET-1, UI-2, PKG-1, SUP-2, SUP-4, SEC-3, OBS-2, ZAP-2, ZAP-3, TEST-1 |
| P3 | 4 | LIFE-1 (residual only), UI-1, SUP-3, PERF-1 |
| Refuted (no severity) | 4 | AND-2, DATA-2, DATA-5, PERF-2 |
| **Total** | **39** | |

Headline corrections versus the original triage:

- **UPD-1 downgraded P0 → P1.** The mechanism is real and the desktop adapter
  violates the `IUpdateSource` MUST-validate contract, but the `.sha256`
  sidecar is served from the same GitHub release over the same TLS trust root
  as the asset, so it provides no independent authenticity protection; size
  (≥90%), ZIP/tar CRC, and `ValidateExtractedContent` already cover transport
  corruption. The loss is defense-in-depth, not a takeover. It is also the same
  defect documented 2026-06-04 (`plans/macos-bug-audit-2026-06-04.md`, HIGH)
  and never fixed/tracked — canonical UPD-1, not a duplicate of another of the
  39 IDs.
- **LIFE-1 refuted in its P1 impact form** (semaphore is released every cycle;
  no cross-process block). Only a residual P3 handle-churn survives.
- **NET-1 downgraded P1 → P2** (DoS requiring a user to subscribe to an
  attacker-controlled URL; bounded by a 15 s timeout, not unbounded).
- **FW-1 / FW-2 downgraded P1 → P2** and marked PARTIALLY_CONFIRMED (mechanism
  real; reachability limited to bare-IPv6 via custom JSON / AWG; the "manual
  nft removal" claim is refuted by the automatic orphan sweep).
- **AND-2, DATA-2, DATA-5, PERF-2 fully refuted** by platform contract /
  source-generator option / parser constraint / singleton ownership.
- **OBS-2, PERF-1 marked PARTIALLY_CONFIRMED** (one sub-citation each is wrong:
  `DiagnosticsExporter.TailLines` is already bounded; the heavy ETW session is
  disposed via `using`).

---

## 2. Complete 39-row matrix

Columns: ID | Orig | Verdict | Final | Conf | Production evidence | Reason |
Minimum root fix | Regression check.

| ID | Orig | Verdict | Final | Conf | Production evidence | Reason | Minimum root fix | Regression check |
|---|---|---|---|---|---|---|---|---|
| UPD-1 | P0 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs:150-172` (sidecar fetched, `AssetSha256: sha`); `GitHubReleaseSource.cs:184` (`DownloadAsync`→`_installer.DownloadAndStageAsync`); `VPNRouter.Core/Services/UpdateChecker.cs:119` (`FullChecksumUrl = null`), `:173` (`checksumUrl = info.FullChecksumUrl`), `:251` (`if (!string.IsNullOrEmpty(checksumUrl))` SHA block); contract `UpdateSources/IUpdateSource.cs:59-66`; Android sibling validates at `SideloadSource.cs:189-205`; desktop wired via `PlatformServices.cs:152` → `UpdateNotificationViewModel.cs:117,248` | [FACT] Entry: desktop update check → `GitHubReleaseSource.CheckAsync` fetches `.sha256` into `AssetSha256` → `DownloadAsync` adapts to legacy `UpdateInfo` with `FullChecksumUrl=null` → legacy `DownloadAndStageAsync` gates its SHA block on the (null) checksum URL → SHA never verified on the normal desktop path. [FACT] Android `SideloadSource` DOES hash-compare, so desktop is the outlier violating the interface MUST. [INFER] Severity bounded: sidecar shares the release trust root (no authenticity gain) and size/CRC/content-presence guards exist (`UpdateChecker.cs:246`, `:334`/`:1292-1340`); loss is defense-in-depth corruption detection. Same defect documented 2026-06-04, never fixed. | Thread the already-fetched `info.AssetSha256` into `DownloadAndStageAsync` and fail-closed on mismatch before staging/apply; do not re-fetch the digest over HTTP. | Desktop digest match stages; digest mismatch refuses staging and deletes the asset; missing optional digest follows an explicit chosen policy. |
| UPD-2 | P1 | CONFIRMED | P1 | High | `VPNRouter.GUI/repair.go:50-56` (inline `Invoke-WebRequest … -OutFile $tmp … & $tmp`), `:58-62` (`exec.Command("powershell.exe", …, "-Command", bootstrap)`); fixed pattern in `VPNRouter.App/Services/SelfRepair.cs:122-126` (documents the ClickFix heuristic) + `:130-154` (temp `.ps1` + `-File`); shipped via `build.ps1:199,208-209,581-586`; reachable from `VPNRouter.GUI/main.go:132` | [FACT] Entry: GUI stub `RunRepair` (main.go:132) → repair.go builds an inline PowerShell bootstrap that downloads `install.ps1` and dot-executes it, launched via `-Command`. [FACT] This is exactly the inline download-and-execute shape `SelfRepair.cs:122-126` says triggers `Trojan:Win32/ClickFix.DCW!MTB`; the app path was migrated to temp-`.ps1`+`-File` but repair.go was not. [FACT] Compiled into the shipped `VPNRouter.GUI.exe` stub. | Reuse the SelfRepair temp-`.ps1` + `-File` pattern in repair.go; clean up the temp script; preserve quoting for paths with spaces. | Repair command uses `-File`, not inline `-Command`; path-with-spaces trampoline test; existing Android sideload tests stay green. |
| LIFE-1 | P1 | REFUTED | P3 | High | `VPNRouter.Core/Services/TunOwnershipLock.cs:108-127` (`Release()` gates on `_owned`/`_semaphore`, NOT `_disposed`), `:129-143` (`Dispose()` calls `Release()` then disposes+nulls semaphore and clears `_instance`), `:53-61` (`Instance()` recreates when `_instance is null || _instance._disposed`); caller order `SingBoxManager.cs:340` (`Stop()`) before `:348` (`_tunLock.Dispose()`), `StopInternal(releaseLock:true)` → `_tunLock.Release()` | [FACT] The claim ("2nd Dispose returns early, never releases the named semaphore, blocks other processes") is impossible: `Release()` does not gate on `_disposed`, `Dispose()` itself calls `Release()` before disposing the handle, the singleton is nulled and recreated, and `SingBoxManager.Dispose` runs `Stop()`→`Release()` before `_tunLock.Dispose()`. The named semaphore count is restored on every cycle. [INFER] Residual only: in a shared-singleton edge, `TryAcquire` (`:72`) can recreate `_semaphore` after dispose and that handle is never disposed (`Dispose` early-returns at `:135`) — a handle churn, not a semaphore hold. | None required for the claimed defect. Optional P3 hygiene: make `Dispose`/`TryAcquire` re-arm consistently so the recreated handle is tracked. | If pursued: two acquire/stop/dispose cycles release the named semaphore and leave no dangling handle. |
| FAIL-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/VpnEngine.cs:44` (`_failover` field, never reset to null), `:1495` (pre-start `_failover ??= … restart: StartAsyncInternal` — no teardown/gate/session-check), `:1527` (post-start `_failover ??= … ExecuteProbeFailoverRestartAsync`), `:495-515` (gate `:497`, `TeardownInternal` `:500`, session guard `:501-507`, session token `:508`); pre-start wiring only on dead config `StartupPipeline.cs:1034-1041`; later post-start callers `VpnEngine.cs:1406,1593` | [FACT] Entry: a pre-start dead-config failover (`StartupPipeline.cs:1035` `if (!preCheck.IsDead) return;` then `:1041 WireFailover`) writes the unsafe pre-start delegate into `_failover` via `??=`. [FACT] The field is never cleared, so every later post-start failover (`OnFailoverRequested` `:1406`, post-start probe `:1593`) reuses that delegate via `??=` and bypasses `TeardownInternal`, `_lifecycleGate`, and the session-cancel pre-check that `ExecuteProbeFailoverRestartAsync` provides. [FACT] The pre-start delegate calls `StartAsyncInternal` directly, which lacks the `HasLiveOrStartingSingBox` guard (only public `StartAsync` has it) and `SetSingBoxManager` overwrites `_singBox` without disposing the old manager → orphaned sing-box/TUN; it also races a concurrent user `Stop()` (gate at `:751`). [INFER] The "revive a disconnected tunnel" sub-claim is partially mitigated because post-start callers pass a session-linked token, so OCE usually aborts the restart — but the teardown/gate bypass and orphan path are real. v2.44.3/v2.46.1 notes fix a deadlock and gate-join, not this slot collision. | Give both phases the safe teardown/restart delegate (route pre-start restart through the gated teardown path), or replace the stored callback when the phase changes; ensure user disconnect cancels queued/in-flight failover and the old manager is disposed exactly once. | Pre-start failover then post-start failure uses the safe delegate; disconnect during that restart never resurrects the tunnel; old manager disposed before replacement; lifecycle gate serializes restart/stop. |
| FW-1 | P1 | PARTIALLY_CONFIRMED | P2 | Med-High | `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:226` (`ReadServerIps` accepts bare IPv6 via `IPAddress.TryParse`), `:205` (`var v4 = serverIps.Where(ip => !ip.Contains(':'))` strips IPv6), `:199-201` (`add table inet … policy drop`), `:135` (`nft -f`), `:144-150` (fail-open on load error); cleanup `:295-322` (marker-gated orphan sweep), `:157-175`; reachability: bare IPv6 via custom JSON or AWG (`ServerUriParser.cs:493-498`); VLESS path bracketed (`VlessUriParser.cs:44` `Uri.Host`) | [FACT] Mechanism proven: a bare IPv6 server literal is accepted by `ReadServerIps` then removed by the `!ip.Contains(':')` filter, while the `inet` table's drop policy discards all IPv6 → an IPv6-only server is never allow-listed and cannot reconnect under an armed kill-switch within the same session. [FACT] Impact overstated: (a) reachability is limited to bare literals (custom JSON / AWG parser strips brackets); the dominant VLESS-URI path yields a bracketed host that `IPAddress.TryParse` rejects into the hostname branch — a different failure shape; (b) the "until the nftables table is manually removed" claim is refuted by the automatic marker-gated orphan sweep on next launch (`:295-322`). [INFER] Existing firewall tests cover IPv4 only, so neither bug nor guard is exercised. | Emit an `ip6 daddr` accept rule for parsed IPv6 server addresses in the ruleset builder; do not weaken the default drop or turn DNS failure into allow-all. | Linux IPv4-only rules unchanged; IPv6-only server gets `ip6 daddr`; mixed-family output contains both families; bracketed input normalized or rejected before the ruleset. |
| FW-2 | P1 | PARTIALLY_CONFIRMED | P2 | Med-High | `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:359` (`ReadServerIps` accepts bare IPv6), `:339-340` (`pass out quick inet from any to {ip}` for every ip incl. IPv6), `:169` (`pfctl -a Anchor -f tmp`), `:177-184` (fail-open: log "NOT blocking", `ReleaseEnable()`, return); cleanup `:210-258`, `:438-477`; reachability same as FW-1 | [FACT] Mechanism proven: every server IP is emitted as an `inet` (IPv4-family) PF rule, so an IPv6 literal produces a malformed rule; if pfctl rejects the atomic ruleset, `EnableBlockRules` logs "NOT blocking" and returns → the kill-switch fails open (traffic flows freely on VPN death). [FACT] Reachability limited to bare-IPv6 inputs (custom JSON / AWG); VLESS-URI bracketed hosts are rejected earlier by `IPAddress.TryParse`. [INFER] Could not execute pfctl to confirm whole-ruleset vs single-rule rejection, but the fail-open path holds either way; cleanup paths refute any permanent brick. | Emit `inet6` rules for IPv6 addresses and cover mixed-family rulesets; keep atomic anchor load; improve the PF-load failure log. | Mac IPv6 uses `inet6`, not `inet`; mixed-family ruleset loads; no IPv6 traffic accidentally allowed globally. |
| CFG-1 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/ConfigGenerator.cs:995` (`Strategy = (ForceIpv4Only \|\| !Ipv6Enabled) ? "ipv4_only" : null`); `VPNRouter.Core/Services/CustomConfigInjector.cs:1532` (`if (forceIpv4Only) dns["strategy"]="ipv4_only"`), `:196`/`:1380` (no `Ipv6Enabled` param; 0 `Ipv6Enabled` matches in file), `:258-259` (full/exclude backstop only), `:263` (include-split sets `dns.final`, never strategy) | [FACT] The generated path forces `ipv4_only` when EITHER `ForceIpv4Only` OR `!Tun.Ipv6Enabled`; the custom include-split path forces it only on `ForceIpv4Only` and never consults `Ipv6Enabled`. [FACT] For include-split + `ForceIpv4Only=false` + `Ipv6Enabled=false`, both backstops are skipped → `dns.strategy` stays as authored/default (AAAA enabled) on an IPv4-only TUN — the exact stall/leak the G5 comment (`ConfigGenerator.cs:988-994`) warns about. Reachable via `StartupPipeline.cs:996`, `HealthMonitor.cs:1222/1232/1239`, `StartCommand.cs:281`, `AndroidConfigBuilder.cs:290`. | Reuse one DNS-strategy decision in generator and injector so `!Ipv6Enabled` also yields `ipv4_only` on the custom include-split path; do not overwrite a user-authored strategy without contract. | Custom IPv4-only TUN emits IPv4 DNS strategy; generated/custom parity test for the `ForceIpv4Only=false`+`Ipv6Enabled=false` combination. |
| CFG-2 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/CustomConfigInjector.cs:309` (`EnsureUrltest` bails only if an existing outbound is `type == "urltest"`), `:324` (`["tag"]="auto"`), `:332` (`outbounds.Insert(selectorIdx, urltest)`), `:335` (prepend `auto` to selector); `Validate()` `:340-392` has no duplicate-tag / reserved-`auto` check | [FACT] The guard is type-only: it never checks for an existing outbound TAGGED `auto` of another type. [FACT] Parser-constructible input — a custom JSON with a `selector` outbound (non-empty children) plus a second `{type:"direct"\|"vless", tag:"auto"}` outbound — passes `Validate`, then `EnsureUrltest` inserts a second outbound tagged `auto` → sing-box rejects duplicate outbound tags (FATAL). `auto` is a common clash-style tag, so the clash is plausible. | Choose a collision-free injected tag or reuse an existing urltest; preserve references to user outbound tags; add a reserved/duplicate-tag check. | Existing user `auto` outbound (non-urltest) does not create a duplicate; existing urltest reused; no duplicate-tag FATAL on supported custom config. |
| PROTO-1 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/ConfigGenerator.cs:1548` (`"dns-tunnel" => BuildDnsTunnelOutbound`), `:1562-1571` (targets `127.0.0.1`:`DefaultLocalPort`, uuid only, no TLS/Reality — sidecar provides transport); `VPNRouter.Core/Services/VlessDeepVerifier.cs:425-435` (switch falls to `_ => BuildVlessOutbound(s)` at `:434`), classifier `:223-258` returns `UnsupportedByVerifier` for AWG/xhttp/naive but 0 `dns-tunnel` matches; entry shape `ServerUriParser.cs:330` (`Server`=domain identity) | [FACT] The deep verifier has no dns-tunnel branch: a dns-tunnel entry falls to the default `BuildVlessOutbound`, which builds an ordinary VLESS outbound to `entry.Server` (the tunnel domain) with TLS, ignoring the slipstream sidecar that provides the real transport. [FACT] The probe therefore fails and yields `DeepVerifyResult.Failed`, condemning a valid server — violating the verifier's own "never condemn the server for our own gap" invariant that already grants `UnsupportedByVerifier` for AWG/xhttp/naive. | Either route dns-tunnel verification through its local sidecar, or add a typed `UnsupportedByVerifier` short-circuit for dns-tunnel; do not mark an unsupported-verifier result as a blocked server. | dns-tunnel verify returns the correct typed result; classifier does not ban a working dns-tunnel server. |
| DATA-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/SettingsLoader.cs:536` (`File.WriteAllText(configPath, serializer.Serialize(settings))` inside `Save`); no atomic helper / `File.Replace` in file (only backup `File.Move` at `:180,:217`); `Save` persists VLESS servers/subscriptions/CustomConfigs (`:514-517` comment) | [FACT] Entry: any settings save (UI toggle, subscription refresh, Smart Connect persist) → `Save` → `File.WriteAllText`, which truncates the destination then writes. [FACT] A crash or power loss between truncate and write-complete of a POPULATED `config.yaml` leaves a partial file → loss of all settings and VPN credentials. No temp+flush+rename. [FACT] The secondary "defaults written over zero-length file" sub-claim is NOT a data-loss vector: missing→example, unreadable→defaults with original untouched (`:137-152`), parse-fail→`.unloadable-*` backup (`:173-194`), validation-fail→`.invalid-*` backup then save (`:217-228`); a zero-length source carries no data. | Write a sibling temp file, flush, and atomically replace `config.yaml` (e.g. `File.Move(tmp, path, overwrite:true)`). | Interrupted save leaves the previous `config.yaml` intact; atomic-replace round-trip test. |
| DATA-2 | P2 | REFUTED | — | High | `VPNRouter.Core/Json/AppJsonContext.cs:122-126` (`[JsonSourceGenerationOptions(… MaxDepth = 32 …)]`, comment `:101-105`); `VPNRouter.Core/Services/ProfileManager.cs:262,349` (`JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.ProfileCollection)`), `:39-55` (`SafeJsonOptions` also `MaxDepth=32` and composes the same context) | [FACT] The literal observation ("uses the context, not `SafeJsonOptions`") is true, but the security property is preserved: the source-generated context carries `MaxDepth=32`, which the STJ source generator propagates to the generated options enforced by the `JsonTypeInfo<T>` overload that `ProfileManager` calls. [FACT] There is no JSON-DoS depth gap; the claim that production "bypasses the documented/tested MaxDepth=32" is false. | None. | None required; optionally a depth-guard test pinning the context behavior. |
| DATA-3 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/SettingsMigrator.cs:698` (`if (s.Tun.Mtu == 1280 \|\| == 1500 \|\| <= 0 \|\| > 1500) s.Tun.Mtu = TunSettings.DefaultMtu`); `VPNRouter.Core/Models/TunSettings.cs:8` (`DefaultMtu = 1420`); doc promise `:692,:705` ("explicit custom MTUs are preserved"); v6→v7 itself sets 1280 (`:676-683`) | [FACT] The condition is purely value-based with no "was-custom-set" flag, so an explicitly user-selected MTU 1280 is indistinguishable from a prior-migration default and is silently rewritten to 1420 — contradicting the inline preserve-custom promise. [FACT] 1280 is a legitimate deliberate value (the v6→v7 step calls it the IPv6 minimum MTU). | Preserve a custom-set marker or stop treating 1280 as a stale default; honor the documented preserve-custom contract. | Migration preserves an explicitly selected 1280; only true defaults are normalized. |
| DATA-4 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/FreeConfigAggregator.cs:170` (`existing.Configs.ToDictionary(c => c.Id, …)`), `:187` (`fresh.ToDictionary(c => c.Id, …)`), both inside `try/catch` `:166-191` returning `fresh` on throw; pool path non-deduped `FreeConfigPoolFetcher.cs:231-262` (`Id` verbatim, only non-empty check `:257`); fallback path DOES dedupe (`FreeConfigAggregator.cs:129`) | [FACT] `ToDictionary` throws `ArgumentException` on duplicate keys. [FACT] The server-side pool path passes entries non-deduped (`ParsePool` reads `Id` verbatim), so a duplicate `id` in pool.json makes `:187` throw → `PreservePreviousValidation` is skipped → previously-Verified entries the new pool dropped are lost (the regression v2.28.5-r2 prevents). A hand-edited/corrupted cache triggers the same via `:170`. [INFER] Carry-over merge loss occurs only when `:170` throws; the pool-duplicate path via `:187` loses the validation-preservation step. | Dedupe remote IDs before `ToDictionary` (reuse the existing `byId.ContainsKey` defense) or use a duplicate-tolerant lookup. | Duplicate remote IDs do not throw; verified-status preservation and cached carry-over survive a duplicate-id pool. |
| DATA-5 | P2 | REFUTED | — | High | `VPNRouter.Core/Services/SubscriptionFetcher.cs:285` (key `$"{Server}:{Port}:{Uuid}:{Flow}:{Username}:{Password}"` — Password LAST); `VPNRouter.Core/Services/ServerUriParser.cs:779-788` (`ParseNaive`: `colon = userinfo.IndexOf(':')`; `username = Substring(0, colon)`, `password = Substring(colon+1)`); `Username` set only by `ParseNaive` (`:805`), empty for all non-naive (`SubscriptionFetcher.cs:279-280`) | [FACT] Username is split on the FIRST colon and is therefore colon-free; Password is the LAST key component. [FACT] For two same-host naive entries the key reduces to `…:Username:Password`; because Username is colon-free it is exactly the prefix before the first `:` of the `Username:Password` substring, so string-equal substrings imply identical Username AND Password — a true equality, not a false collision. Password's internal colons create no ambiguity with any following field. No parser-reachable input constructs a collision. | None. | None required; optionally a dedupe-key test pinning naive credentials with colons in the password. |
| DATA-6 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/FreeConfigCache.cs:130-132` (`WriteAllText(tmp)` → `if (File.Exists(_path)) File.Delete(_path)` `:131` → `File.Move(tmp,_path)` `:132`); doc claim `:113-114` ("atomically"); correct pattern in family `FreeConfigPoolFetcher.cs:140` (`File.Move(tmp, _cachePath, overwrite:true)`) | [FACT] A crash between `:131` (delete) and `:132` (move) leaves `_path` deleted and `tmp` un-moved → cache lost, despite the comment claiming an atomic rename. [FACT] The atomic overwrite-move already exists in the same component family. [INFER] Impact is a regenerable cache (verified-status results lost, re-test required), not credentials → P2. | Replace delete-then-move with `File.Move(tmp, _path, overwrite:true)`. | Crash-window test: interrupted save leaves a readable prior cache. |
| NET-1 | P1 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/PolicyHttpClient.cs:112-119` (`SendAsync(…, HttpCompletionOption.ResponseContentRead, …)` + `Content.ReadAsByteArrayAsync`), handler `:69-71` (`AutomaticDecompression = DecompressionMethods.All`); 0 `MaxResponseContentBufferSize` matches in `VPNRouter.Core/Services`; subscription intake `SubscriptionFetcher.cs:66-69` (user-provided URL), consumed `:85`; bounded contrast `FreeConfigPoolFetcher.cs:37-38,115,131,179-181` | [FACT] An untrusted subscription response is fully buffered into a `byte[]` with no byte limit (default ~2 GB ceiling) and decompressed in-memory under `DecompressionMethods.All` (decompression-bomb surface); the only bound is the 15 s per-request timeout. [FACT] A malicious/compromised provider or MITM on a non-pinned URL can exhaust process memory. [INFER] Downgraded P1→P2: it is a DoS that requires the user to subscribe to an attacker-controlled URL, with a weak partial time bound — not an unauthenticated remote OOM. | Stream with a fixed maximum response size (and an expanded-size guard) before decoding, mirroring `FreeConfigPoolFetcher`'s bounded decompression. | Oversized / compressed-bomb response is aborted past the limit; normal subscription still parses. |
| FLOW-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:519` (`PickServer` winner), `:532` (branch fires when `chosen.Name != ActiveSubscriptionServer`), `:536` (winner stored), `:537` (`SaveSettings()`), `:542` (winner shown in status only), `:555` (`ToggleConnectionAsync`); `MainWindowViewModel.cs:3782-3783` (`SaveSettings` re-derives `SelectedSubscriptionServer ?? FirstOrDefault` → overwrites `ActiveSubscriptionServer`), `:4252-4263` (re-save, reload, hand `_settings.Vless.ActiveServer` to engine); probe logic `ServerHealthProbe.cs:94-106`, `ConnectionIntentScorer.cs:26-41` | [FACT] Entry: Smart Connect probes, picks a live winner, stores it (`:536`), then immediately calls `SaveSettings()` (`:537`), which re-derives the active server from the stale UI selection (`SelectedSubscriptionServer ?? FirstOrDefault`) and overwrites the winner (`:3782-3783`). Smart Connect never updates `SelectedSubscriptionServer`, so the stale/dead name is persisted and later handed to the engine (`:4263`); the winner is used only for the status string (`:542`). [FACT] The branch fires precisely when the active server is dead/unset (probe returns active only if `Alive`, else fastest-live) — i.e. the winner is discarded in exactly the case the feature exists for. | Update the selected-VM state to the winner before saving, or save without re-deriving the active server from the stale selection. | Smart Connect with a dead active server connects to the measured winner; selected VM state reflects the winner before persist. |
| UI-1 | P2 | CONFIRMED | P3 | High | `VPNRouter.App/Views/MainWindow.axaml:712-713` (`<Button … Content="↓ Update" …/>` hardcoded); localized string exists `VPNRouter.Core/Localization/Strings.cs:773` (`UpdateButton => Ru ? "Обновить" : "Update"`), surfaced `VPNRouter.App/Localization/Strings.cs:439` | [FACT] The update button uses a hardcoded English literal and does not bind the existing localized `UpdateButton` resource, so RU users see English. [INFER] Cosmetic localization defect with no runtime/data impact → P3. | Bind the button content to the localized `UpdateButton` string. | RU locale renders the localized update label. |
| UI-2 | P2 | CONFIRMED | P2 | High | `VPNRouter.App/Views/Pages/NetworkPage.axaml:1480` (`Grid ColumnDefinitions="20,70,140,*,Auto" ColumnSpacing="10" Margin="14,1"`), delete button `:1511-1518` (`Content="✕"`, no MinWidth); detail pane `:191` (`ColumnDefinitions="140,*"`), `ScrollViewer` `:230-232` (`HorizontalScrollBarVisibility="Disabled"`, `AllowAutoHide="False"`); window floor `MainWindow.axaml:14-15` (`Width="520"`, `MinWidth="360"`) | [FACT] Fixed columns sum to 230 px + 40 spacing + 28 margin = 298 px before the Value(`*`)/delete(`Auto`) columns. [FACT] At `MinWidth=360` the detail pane is ≈360−140(nav)−~14(reserved scrollbar) ≈ 206 px usable, and horizontal scroll is DISABLED, so overflow clips: the fixed portion alone exceeds the pane, the Value column collapses (ellipsizes), and the `✕` delete button is pushed off the right edge — unreachable at narrow widths. [INFER] Worse than the original "value clips" framing — the delete action itself is lost. | Make the read-mode rule row responsive (shrink/stack fixed columns or enable horizontal scroll) so the delete action stays reachable at MinWidth. | At MinWidth=360 the value and delete action remain visible/reachable; narrow-window layout test. |
| CLI-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.CLI/Commands/StopCommand.cs:11` (read state), `:23-24` (`Process.GetProcessById(state.SingBoxPid)` + `Kill(entireProcessTree:true)`), `:38` (`StateFile.Clear()`); `StartCommand.cs:198-204` (exits only on Ctrl+C), `:164-167` (`SingBoxStarted` handler returns early if state null); restart-on-crash `HealthMonitor.cs:252` (`Crashed += OnSingBoxCrashed`), `:728` (early-return only `if (_isStopping)`), `:753-754` (`if (RestartOnFailure) AttemptRestart()`) | [FACT] Entry: `vpnrouter stop` kills ONLY the recorded sing-box child PID and clears the state file; it sends no stop request to the running `start` process. [FACT] The still-running `start` sees the child death as a crash (`_isStopping` is false because only the Ctrl+C path sets it) and restarts sing-box. [FACT] The restart fires `SingBoxStarted`, whose handler returns early because stop already cleared the state file → the new PID is never re-recorded → the VPN runs untracked. No ownership/stop-request protocol exists. | Send a stop request to the owning engine/process (IPC/mutex/event) instead of killing its child directly; have the owner tear down and clear state itself. | `stop` causes the owning `start` to exit cleanly and clear state; no untracked restart; no orphan sing-box. |
| CLI-2 | P1 | CONFIRMED | P1 | High | `VPNRouter.CLI/Commands/StopCommand.cs:23-24` (`GetProcessById` + `Kill(entireProcessTree:true)`, no ownership check); existing ownership gate `VPNRouter.Core/Services/OrphanCleanup.cs:44` (`public static KillOrphans`), `:91` (`KillByName("sing-box", …, killOnly: ProcessOwnership.IsOwnedSingBox)`, comment notes "sing-box is a common third-party process name"); used by GUI (`Program.cs:405`, `MainWindowViewModel.cs:4156,4233`) but not by StopCommand | [FACT] Entry: `stop` kills the PID from the state file with NO image/path/name ownership validation. [FACT] If the recorded PID has been reused, `Kill(entireProcessTree:true)` terminates an unrelated process tree. [FACT] A suitable ownership gate (`OrphanCleanup.KillOrphans` / `ProcessOwnership.IsOwnedSingBox`) already exists and is used by the GUI but not by the CLI. | Revalidate executable identity with the existing `ProcessOwnership`/`OrphanCleanup` logic before killing. | Stop refuses to kill a PID whose image/path is not a VPNRouter-owned sing-box; PID-reuse test. |
| AND-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Android/VpnRouterService.java:682` (`Log.e(LOG_TAG, "startTunnel failed: " + e…getMessage(), e)`), `:683-685` (`Intent … putExtra(EXTRA_ERROR_MESSAGE, e…getMessage())` + `sendBroadcast`); existing scrubber `:465` (`scrubSecrets`, used only at `:448`), C# `AndroidDiagnosticsExporter.cs:236,270` (`RedactLogText`) | [FACT] Entry: a libbox `startTunnel` failure broadcasts and logs the RAW exception message. [FACT] libbox messages can embed server addresses, UUIDs, or config fragments. [FACT] A scrubber exists (`scrubSecrets` `:465`) but is applied only in the crash-report builder (`:448`), not on the `:682/:684` error path. Attacker/trust boundary: logcat readers and any app/UI consumer of the broadcast error extra. Impact: VPN credential/endpoint disclosure. | Scrub once (shared redactor) before both logging and broadcasting the error. | Error broadcast/log contains no server/UUID/config fragment; scrubber applied on the tunnel-error path. |
| AND-2 | P2 | REFUTED | — | High | `VPNRouter.Android/VpnRouterService.java:1277-1289` (`onRevoke`: `:1283 cancelScheduledRestart()`, `:1285-1288 submitLifecycle(… stopTunnel())`, `:1289 super.onRevoke()`); `stopTunnel` guard `:1257,:1263` (`if (!teardownTunnelResources()) return;`); `onStartCommand` returns `START_STICKY` `:582` | [FACT] `onRevoke` calls `super.onRevoke()`, i.e. the framework `VpnService.onRevoke()`, whose default implementation calls `stopSelf()` — so the sticky service IS stopped. [FACT] `START_STICKY` only recreates a service the SYSTEM kills under memory pressure, not one that called `stopSelf()`. [FACT] The double-fire guard (`:1263`) makes the second teardown (from `onDestroy`) a no-op, so the down-event fires exactly once. The claim ("stops tunnel but not sticky service → doomed restart + spurious error") is incorrect. | None. | None required. |
| PKG-1 | P1 | CONFIRMED | P2 | High (bug) / Med (reach) | `build-mac.sh:17` (`set -euo pipefail`), `:90` (`echo "…darwin-${ARCH}…"`), `:93` (`GOARCH="${ARCH}" … go build`), gate `:87` (`[ -n "$WGTURN_CORE" ] && [ -d …/cmd/wgturn-cli ] && command -v go`); `ARCH` never assigned anywhere; `.github/workflows/build-mac.yml` sets neither `ARCH` nor `WGTURN_CORE_DIR` (calls `./build-mac.sh "<ver>"`), go installed `:41`; `tools/wgturn-cli-cache/` not in git | [FACT] `ARCH` is referenced at `:90/:93` but never assigned; under `set -u` the wgturn branch aborts the whole macOS build the moment the gate (`:87`) passes. [FACT] Reachability is gated: the cache is absent from git and CI sets no `WGTURN_CORE_DIR`, so the branch fires only if `gh repo clone PavelLizunov/wgturn-core` (`:82`) succeeds on the runner — which current public CI does not exercise. [INFER] Real guaranteed-build-break but latent → P2, not P1. Shares the gate with SUP-4. | Derive `ARCH` once from the build target (e.g. `uname -m`→`arm64`/`amd64`) before the wgturn branch. | macOS build with the wgturn branch taken does not abort on an unbound variable. |
| SUP-1 | P1 | CONFIRMED | P1 | High | `.github/workflows/build-linux.yml:169` (`wget -q -O appimagetool "…/releases/download/continuous/appimagetool-x86_64.AppImage"`), `:170` (`chmod +x`), `:172` (`ARCH=x86_64 ./appimagetool --appimage-extract-and-run …`); `:283` `sha256sum` only emits sidecars for FINAL artifacts, not input verification | [FACT] Entry: the Linux release build downloads a mutable `continuous` (rolling) appimagetool ELF with NO digest verification and executes it with write access to the release pipeline. [FACT] A compromised AppImageKit release channel or CDN MITM yields arbitrary code execution inside the release build → artifact tampering. Attacker/trust boundary: upstream release-channel / CDN compromise crossing into the build. Impact: supply-chain takeover of all Linux artifacts. | Pin an immutable version AND verify a SHA256 before execution. | Build refuses an appimagetool whose digest does not match the pinned value. |
| SUP-2 | P1 | CONFIRMED | P2 | High | `.github/workflows/build-linux.yml:106` (`SINGBOX_VER="1.13.14"` pinned), `:107` (`curl -sSL -o /tmp/singbox.tar.gz "…/v${SINGBOX_VER}/sing-box-…-linux-amd64.tar.gz"`), `:109` (`tar -xzf`), `:110` (`cp …/libcronet.so publish/linux-x64/libcronet.so`); distribution comment `:103-105` ("deb/AppImage/tar.gz all `cp -R publish/linux-x64/.`") | [FACT] The sing-box/libcronet archive is downloaded and bundled into every Linux artifact with NO digest verification of the archive or `libcronet.so`. [FACT] The version tag IS pinned (stable URL), so this is a missing-digest integrity gap rather than a floating-ref exposure (contrast SUP-1). [INFER] Downgraded P1→P2: the attack requires GitHub release-asset compromise / CDN MITM, largely mitigated by the pinned tag + TLS; still a real unverified-third-party-binary-in-artifact gap. | Pin and verify the release archive digest before extraction. | Build refuses a sing-box archive whose digest does not match. |
| SUP-3 | P2 | CONFIRMED | P3 | High | `.github/workflows/sign-windows.yml:69` (`actions/upload-artifact@v4`), `:75` (`signpath/github-action-submit-signing-request@v1`); the only 2 unpinned `uses:` across all 39 in `.github/workflows` (rest SHA-pinned); inert guards: trigger `on: workflow_dispatch` only + "Guard - secrets present" `exit 1` without `SIGNPATH_API_TOKEN`; header "INERT until enrollment … MANUAL-ONLY" | [FACT] Exactly two actions use mutable major-version tags while the rest of CI SHA-pins. [FACT] The workflow is manual-only and hard-fails without SignPath secrets, so the supply-chain exposure is currently dormant. [INFER] Hygiene follow-up → P3. | SHA-pin the two actions when the signing workflow is enrolled. | No unpinned `uses:` remains once the workflow is active. |
| SUP-4 | P2 | CONFIRMED | P2 | High | `build-mac.sh:82` (`gh repo clone PavelLizunov/wgturn-core "…/wgturn-core"` — default-branch HEAD, no pin), `:88` (`WGTURN_SHA=$(git rev-parse --short=12 HEAD)`), `:94` (`-ldflags "-X main.version=$WGTURN_SHA"`); same gate `:87` as PKG-1 | [FACT] wgturn-core is cloned from floating HEAD; the captured SHA is mere `-ldflags` metadata, never asserted, checked out, or verified — the build uses whatever HEAD happens to be at clone time. [FACT] Shares PKG-1's reachability gate. [INFER] Reproducibility/integrity gap, currently latent → P2. | Pin wgturn-core to a commit/tag and assert it before bundling. | Build uses a pinned wgturn-core commit and fails on mismatch. |
| SEC-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/SubscriptionFetcher.cs:62` (`Information("[Subscription] Fetching {Url}", url)`), `:72,:88,:101-103,:106,:110,:324-326,:343-344` (raw `url`/`entry.Url` logged); only `ScrubSecrets` call is on a failing parse-line's CONTENT (~`:263`), never the URL | [FACT] Full subscription URLs — which commonly embed provider tokens in the path/query — are written verbatim to the primary log at many sites; no URL redaction exists on these paths. [FACT] Attacker/trust boundary: any local log reader, or a third party handed the raw `%ProgramData%\VPNRouter\logs\vpnrouter*.log` (support bundle/screenshot/upload); the on-disk log is written before any diagnostics redaction. Impact: subscription-provider credential disclosure. [INFER] `CrashReporter.ScrubSecrets`/`DiagnosticsExporter` redact crash/bundle copies but keep the domain and do not protect the primary log. | Log a redacted origin/identifier (scheme+host or a hash), never the full credential-bearing URL. | Log lines for subscription fetch contain no token/path/query. |
| SEC-2 | P1 | CONFIRMED | P1 | High (code) / Med (exploit) | `VPNRouter.Core/AppPaths.cs:112` (`Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter")`), `:99-108` (`EnsureDirectories` = plain `Directory.CreateDirectory`); `packaging/windows/install.ps1` creates `$DataRoot` + Defender exclusions only — 0 `icacls\|Set-Acl\|FileSystemAccessRule\|SetAccessControl` matches in `packaging/windows` | [FACT] `%ProgramData%\VPNRouter` is created with no restrictive ACL; install.ps1 adds Defender exclusions only, and runtime ensures plain directories. [FACT] Attacker/trust boundary: a local unprivileged user on a shared/multi-user box. [INFER] Impact: via standard Windows default `%ProgramData%` inheritance (Users get read on subfolders) such a user can read `config.yaml`/`current.json`/logs containing VPN credentials, UUIDs, and subscription tokens. No defeating guard. Exploitability rests on default-ACL inheritance, hence Medium confidence on exploitation. | Apply a restrictive install/runtime ACL (remove Users read on the data root) while preserving required service access. | ACL test: a non-admin local user cannot read `config.yaml`/`current.json`/logs. |
| SEC-3 | P2 | CONFIRMED | P2 | High (mech) / Med (input) | `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs:173-174` (`?? $"connect-url -url \"{config.WgturnUrl}\" -vk-link \"{config.VkLink}\""` interpolated into one `Arguments` string, no escaping); `Start()` validation `:84-90` (non-empty only, no scheme/quote check) | [FACT] User-controlled `WgturnUrl`/`VkLink` are interpolated inside double quotes into a single `Arguments` string with no escaping; a value containing `"` breaks out and can inject/override wgturn-cli flags. [FACT] Attacker/trust boundary: whoever controls those config values — normally the user's own config (weak model), but crosses a boundary if the value comes from an untrusted shared/imported profile. Impact: argument injection into wgturn-cli. | Validate/escape the values (reject quotes/control chars or use an argument list, not a single interpolated string). | A URL/`VkLink` containing quotes cannot inject or override wgturn-cli arguments. |
| OBS-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/ClashLogStream.cs:92` (`$"&token={Uri.EscapeDataString(secret)}"`), `:133` (`Information("[ConnHealth] Clash /logs stream connected ({Uri})", _logsUri)`); scrubber `VPNRouter.Core/Services/CrashReporter.cs:169-171` (`_proxyUriPattern` schemes `vless\|vmess\|trojan\|ss\|hysteria2?\|tuic\|naive\|amneziawg\|awg` — no ws/wss), `:173-175` (`_httpUrlPattern` = `https?://` only), `:181-183` (`_longBase64Pattern` ≥40 chars), `:191` (`ScrubSecrets`) | [FACT] Entry: on each Clash /logs reconnect the full `ws(s)://…/logs?level=info&token=<clash_api_secret>` URI is logged at Information. [FACT] The crash-report scrubber does not recognize `ws://`/`wss://` (scheme list lacks them), matches only `https?://` for HTTP URLs, and needs ≥40 chars for base64 — so a short clash token in a `ws://…&token=…` line is untouched. Attacker/trust boundary: log readers / shared crash reports. Impact: clash API secret disclosure. | Never log the token-bearing URI; extend the shared redactor to cover `ws(s)://` and `token=`/key-value forms. | Clash log line and crash report contain no `token=` secret; ws:// URLs are redacted. |
| OBS-2 | P2 | PARTIALLY_CONFIRMED | P2 | High | Real: `VPNRouter.Core/Services/CrashReporter.cs:131` (`var lines = File.ReadAllLines(logs);` then `:132-134` keeps last 200). Sub-citation refuted: `VPNRouter.Core/Diagnostics/DiagnosticsExporter.cs:525-540` (`TailLines` seeks to `EOF − MaxTailReadBytes`, 12 MB const `:37`, comment "audit MEDIUM, 2026-06-02") | [FACT] The crash handler reads the ENTIRE latest `vpnrouter*.log` into memory just to keep 200 lines → genuine OOM risk on a large/runaway log, exactly when diagnostics are needed. [FACT] The co-cited `DiagnosticsExporter.TailLines` is the BOUNDED implementation (seeks from EOF, capped at 12 MB) — that sub-citation is wrong/stale; DiagnosticsExporter is the fixed path, not the vulnerable one. Net: substantive claim holds at `CrashReporter.cs:131` only. | Make `CrashReporter` tail via a bounded reverse-seek (reuse the `DiagnosticsExporter.TailLines` pattern). | Crash tail read is bounded regardless of log size. |
| ZAP-1 | P1 | CONFIRMED | P1 | High | `VPNRouter.Core/Services/ZapretUpdater.cs:353` (`CopyDirectoryOverwrite(extractedRoot, ZapretDir, _logger)`), `:619-636` (per-file exceptions caught, "Skipped locked file" `:631`, continue), `:370` (`var version = ParseVersionFromServiceBat() ?? tagName`), `:371` (`try { File.WriteAllText(VersionFilePath, version); } catch { }`) | [FACT] Entry: a zapret update copies files but swallows per-file copy failures for locked files (e.g. an in-use `winws.exe`/`WinDivert64.sys`), then writes `version.txt` to the new version REGARDLESS. [FACT] Result: a mixed old-driver/new-executable installation is reported as current; subsequent update checks see the new version and never retry, leaving a silently-broken zapret install. | Mark the version only after every required file is replaced successfully; otherwise report/retain the prior version and retry. | A locked-file copy failure leaves `version.txt` at the old version and triggers retry. |
| ZAP-2 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/EmergencyChannel/EmergencyChannelManager.cs:188` (`_process` overwritten in `LaunchProcess` without disposing prior), `:208-225` (`OnProcessExited` sets State=Failed + fires Crashed + closes log writer, no Dispose/null), `:131-135` (`Stop()` early-return skips dispose when `HasExited`); owner level `EmergencyChannelEngine.cs:115` (`_manager = _managerFactory()` new manager each `StartAsync`), `:104-109` (Stop-first guard only for Connecting/Connected), `:187-193` (`OnManagerCrashed` sets State=Failed without disposing/nulling `_manager`) | [FACT] Exited wgturn `Process` instances are neither disposed nor cleared before the field is overwritten; at the owner level a crashed manager is orphaned undisposed because the Stop-first guard only fires for Connecting/Connected. [FACT] The engine is long-lived (Dispose only at teardown), so one undisposed `Process` handle (+ manager) leaks per crash→reconnect cycle. [INFER] Bounded per-cycle leak of a small handle set → P2. | Dispose and null the prior `Process`/manager before reassignment; dispose the crashed manager in `OnManagerCrashed`. | Repeated crash→reconnect cycles leave no undisposed Process/manager handles. |
| ZAP-3 | P2 | CONFIRMED | P2 | High | `VPNRouter.Core/Services/WgturnUpdater.cs:427` (`File.Delete(CliExePath)`), `:429` (`File.Move(tempBin, CliExePath)`), `:430-443` (catch → `WgturnDownloadException`), `:479-481` (`finally { if (File.Exists(tempBin)) File.Delete(tempBin); }`) | [FACT] Entry: the updater deletes the working wgturn-cli binary then moves the new download into place. [FACT] If the delete succeeds but the move throws, the working binary is gone with no replacement, and the `finally` then deletes `tempBin` (the only remaining copy) → no wgturn-cli binary and no recovery copy. [INFER] Wording note: `tempBin` is the new download, not a backup of the original, but the destructive outcome matches the claim. | Stage the new binary and atomically replace (`File.Move(tmp, path, overwrite:true)`) without a destructive delete-first; keep a recovery copy until success. | A failed replacement leaves the previous working wgturn-cli binary intact. |
| PERF-1 | P2 | PARTIALLY_CONFIRMED | P3 | High | `VPNRouter.Core/Services/VpnEngine.cs:832` (`try { _etw?.Stop(); } catch { }`), `:845` (`_etw = null;`); per-connect creation `StartupPipeline.cs:1370-1372` (`MonitorFactory()`) → `PlatformServices.cs:50` (`new EtwProcessMonitor(logger)`); `EtwProcessMonitor.cs:38` (`ManualResetEventSlim _sessionReady`), `:84` (Stop's `_sessionReady.Wait(1s)` allocates the WaitHandle), `~:122` (`using var session` disposes the heavy `TraceEventSession`), `:200-209` (`Dispose` is the only thing that disposes `_sessionReady`) | [FACT] A per-connect ETW monitor is Stopped (allocating a `ManualResetEventSlim` WaitHandle) and nulled without `Dispose`; only `Dispose` would dispose `_sessionReady`. [FACT] BUT the heavy `TraceEventSession` IS disposed deterministically via `using var session` when `Process()` returns; the residual is a single small `ManualResetEventSlim` whose inner `SafeWaitHandle` is finalizer-closed. [INFER] The dispose-omission is real and per-reconnect but is NOT an unbounded accumulation of heavy resources → downgrade P2→P3. | Call `Dispose` (not just `Stop`) on the ETW monitor at connect teardown. | Reconnect cycles dispose the monitor; no residual WaitHandle accumulation. |
| PERF-2 | P2 | REFUTED | — | High | `VPNRouter.Core/Services/FreeConfigAggregator.cs:24-32` (aggregator + 3 fetchers, none `IDisposable`); fetchers `FreeConfigFetcher.cs:21`, `FreeConfigPoolFetcher.cs:47/:59`, `FreeConfigGeoIp.cs:33` (undisposed `HttpClient`); graph built once `VPNRouter.App/ViewModels/FreeConfigsPageViewModel.cs:73`; VM created once `MainWindowViewModel.cs:2760` (`FreeConfigsVm` assigned exactly once, `{ get; private set; }`); `MainWindowViewModel` created once `App.axaml.cs:99`; `FreeConfigsPageViewModel.Dispose:153-160` only unsubscribes events | [FACT] The three `HttpClient` holders exist and are never disposed, BUT the graph is a singleton: the aggregator is built in `FreeConfigsPageViewModel`, which is created exactly once in the `MainWindowViewModel` constructor, itself created once at app startup. [FACT] There is no per-navigation recreation, so the "recreated view-model graphs retain three undisposed HttpClient pools" accumulation premise is false; three long-lived HttpClients for app lifetime is the recommended pattern. | None (disposal omission is technically true but harmless). | None required. |
| TEST-1 | P2 | CONFIRMED | P2 | High | `VPNRouter.Tests/…/StartupPipelineTests.cs:33-34` (header comment claims a `SetupFirewall_BlockOnFail_CreatesRules` test), `:277` (actual `SetupFirewall_NoBlockOnFail_SkipsRuleCreation`, runs HotReload — skips phase 6 — asserts `host.SetFirewall` null), `:469-471` (`TestStartupHost.FirewallFactory` THROWS if phase 6 runs); wiring `VPNRouter.Core/Services/StartupPipeline.cs:1090-1092` (`isFullTunnel` from `settings.App.RoutingMode` → `firewall.CreateBlockRules(scanResult.ProcessNames, isFullTunnel)`) | [FACT] The claimed regression test does not exist (grep finds only the comment); the sole firewall test runs HotReload mode and asserts the firewall is null, and the test host's `FirewallFactory` throws if phase 6 runs, so no test in this file can exercise the pipeline's `RoutingMode → isFullTunnel → CreateBlockRules` wiring. [FACT] Linux/Mac firewall tests call `CreateBlockRules(isFullTunnel:…)` on the impls directly but do not test the pipeline derivation. [INFER] Citation nuance: the misleading comment is the test-file header (`:33-34`), not `StartupPipeline.cs:1090`. | Add an executable pipeline-level regression test for full/split `isFullTunnel` kill-switch wiring (description only — not created here). | A pipeline test exercises both RoutingMode values and asserts the correct `isFullTunnel` reaches `CreateBlockRules`. |

---

## 3. Detailed control-flow proofs for final P0/P1

There are **no final P0** findings (UPD-1 downgraded). Full proofs for the 13
final P1 findings:

### UPD-1 (P1) — desktop update discards the fetched SHA256
- Entry point: `UpdateNotificationViewModel.cs:117` assigns the desktop source
  from `PlatformServices.cs:152` (`new GitHubReleaseSource(...)`); `:248` calls
  `_updateSource.DownloadAsync(_pendingUpdate, progress: null)`.
- `GitHubReleaseSource.CheckAsync` fetches the `.sha256` sidecar
  (`GitHubReleaseSource.cs:150-167`) and stores it (`:172` `AssetSha256: sha`).
- `GitHubReleaseSource.DownloadAsync` (`:184`) delegates to
  `_installer.DownloadAndStageAsync` → `UpdateChecker`'s explicit
  `IDesktopInstaller` impl, which builds a legacy `UpdateInfo` with
  `FullChecksumUrl = null` (`UpdateChecker.cs:119`); `info.AssetSha256` is never
  copied. The comment even admits "SHA already inlined via info.AssetSha256".
- Legacy `DownloadAndStageAsync`: `checksumUrl = info.FullChecksumUrl` → null
  (`:173`); the SHA block is gated `if (!string.IsNullOrEmpty(checksumUrl))`
  (`:251`) → skipped.
- Missing guard: the interface contract `IUpdateSource.cs:59-66` says
  `DownloadAsync` MUST validate against `AssetSha256` and cannot defer to the
  caller. Android `SideloadSource.cs:189-205` honors it (hash compare `:198`,
  wipe+throw `:201-204`); desktop does not.
- Consequence: a desktop in-app update can download, extract, and apply an
  asset without hash verification.
- Severity rationale (P1 not P0): the sidecar is served from the same release
  over the same TLS trust root → no independent authenticity protection;
  size ≥90% (`:246`), ZIP/tar CRC at extraction, and `ValidateExtractedContent`
  (`:334`/`:1292-1340`, a structural presence check) already catch transport
  corruption. Loss is defense-in-depth + a broken interface contract.

### UPD-2 (P1) — repair.go reintroduces inline `-Command` download-and-execute
- Entry point: GUI stub `main.go:132` → `RunRepair`.
- `repair.go:50-56` builds an inline bootstrap (`Invoke-WebRequest -Uri
  'https://vpn.ninitux.com/install.ps1' -OutFile $tmp … & $tmp`), executed via
  `exec.Command("powershell.exe", …, "-Command", bootstrap)` (`:58-62`).
- Missing guard: `SelfRepair.cs:122-126` documents that this exact inline
  download-and-execute shape triggers `Trojan:Win32/ClickFix.DCW!MTB`; the app
  path was migrated to a temp `.ps1` + `-File` (`:130-154`) but repair.go was
  not.
- Consequence: the shipped `VPNRouter.GUI.exe` stub (`build.ps1:199,208-209,
  581-586`) can trip Defender ClickFix heuristics on the repair path.

### FAIL-1 (P1) — shared `_failover ??=` slot with incompatible delegates
- Entry point: pre-start dead-config failover. `StartupPipeline.cs:1034`
  `preCheck = sanityCheck.CheckBeforeStart(configJson)`; `:1035`
  `if (!preCheck.IsDead) return false;`; `:1041` `var failover =
  _host.WireFailover(sanityCheck);`.
- `WireFailover` (`VpnEngine.cs:1491-1516`) writes `_engine._failover ??=
  new AutoFailoverEngine(... restart: async (innerCt) => { await
  StartAsyncInternal(...); return true; })` — NO `TeardownInternal`, NO
  `_lifecycleGate`, NO session pre-check, uses `innerCt`.
- `WireFailoverWithStop` (`:1525-1540`) writes the SAME field via `??=` with a
  restart that calls `ExecuteProbeFailoverRestartAsync` (`:495-515`): gate
  `:497`, `TeardownInternal` `:500`, session guard `:501-507`, session token
  `:508`.
- Missing guard: `_failover` (`:44`) is never reset to null (only `:1382`
  `ResetCycle()`), so first writer wins. Later post-start callers
  (`OnFailoverRequested` `:1406`, post-start probe `:1593`) reuse the won
  pre-start delegate.
- Consequence: the pre-start delegate calls `StartAsyncInternal` directly
  (no `HasLiveOrStartingSingBox` guard — that lives only in public `StartAsync`),
  and `SetSingBoxManager` overwrites `_singBox` without disposing the old
  manager → orphaned sing-box/TUN; it also races a concurrent user `Stop()`
  (gate at `:751`). The "revive disconnected tunnel" sub-claim is partially
  mitigated because post-start callers pass a session-linked token (OCE usually
  aborts), but the teardown/gate bypass and orphan path are real. v2.44.3/v2.46.1
  fixes address a deadlock and a gate-join, not this collision.

### DATA-1 (P1) — non-atomic settings save
- Entry point: any settings save (UI toggle, subscription refresh, Smart
  Connect persist) → `SettingsLoader.Save`.
- `SettingsLoader.cs:536` `File.WriteAllText(configPath,
  serializer.Serialize(settings))` — truncate-then-write; no temp+flush+rename,
  no `File.Replace` (only backup `File.Move` at `:180,:217`).
- Missing guard: no atomic replacement.
- Consequence: a crash/power loss between truncate and write-complete of a
  populated `config.yaml` leaves a partial file → loss of all settings and VPN
  credentials. (The zero-length "defaults overwrite" sub-claim is NOT a
  data-loss vector — backup-guarded and empty source.)

### FLOW-1 (P1) — Smart Connect winner overwritten by stale selection
- Entry point: Simple-mode Smart Connect. `SimpleMode.cs:519` `chosen =
  ConnectionIntentScorer.PickServer(...)`; `:532` branch fires when
  `chosen.Name != ActiveSubscriptionServer`; `:536` stores the winner; `:537`
  `SaveSettings()`; `:542` winner shown in status only; `:555`
  `ToggleConnectionAsync()`.
- `SaveSettings` (`MainWindowViewModel.cs:3782-3783`) re-derives `activeSub =
  SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault()` and writes
  `_settings.App.ActiveSubscriptionServer = activeSub?.Name ?? ""`, overwriting
  the winner. Smart Connect never updates `SelectedSubscriptionServer`.
- `ToggleConnectionAsync` (`:4252-4263`) re-saves, reloads, and hands
  `_settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer` (the
  stale name) to the engine.
- Missing guard: nothing propagates the probed winner into the selected-VM state
  before persist.
- Consequence: the UI displays the measured winner while the engine connects to
  the stale/dead entry — and the branch fires precisely when the active server
  is dead (`ServerHealthProbe.cs:94-106`, `ConnectionIntentScorer.cs:26-41`),
  i.e. exactly the case the feature exists for.

### CLI-1 (P1) — `stop` kills the child only; `start` restarts untracked
- Entry point: `vpnrouter stop` → `StopCommand.Execute`.
- `StopCommand.cs:23-24` `Process.GetProcessById(state.SingBoxPid)` +
  `Kill(entireProcessTree:true)`; `:38` `StateFile.Clear()`. No IPC/mutex/event
  signals the running `start` (which exits only on Ctrl+C: `StartCommand.cs:
  198-204`).
- The still-running `start` watches the child: `HealthMonitor.cs:252`
  `_singBox.Crashed += OnSingBoxCrashed`; `OnSingBoxCrashed` (`:726-754`)
  returns early only `if (_isStopping)` (`:728`, set true solely by the Ctrl+C
  path) → `:753-754` `if (RestartOnFailure) AttemptRestart()`.
- Re-record fails: the restart fires `SingBoxStarted` (`StartCommand.cs:164`),
  whose handler (`:166-167`) does `var existing = StateFile.Read(); if (existing
  == null) return;` — stop already cleared state, so the new PID is never
  re-recorded.
- Missing guard: no ownership/stop-request protocol between `stop` and `start`.
- Consequence: the VPN restarts and runs untracked after a `stop`.

### CLI-2 (P1) — PID killed without ownership validation
- Entry point: same `StopCommand.Execute`.
- `StopCommand.cs:23-24` kills the PID from the state file with NO image/path/
  name ownership check.
- Missing guard: an ownership gate already exists and is unused here —
  `OrphanCleanup.cs:44` `public static KillOrphans(...)`, `:91`
  `KillByName("sing-box", null, killOnly: ProcessOwnership.IsOwnedSingBox)`
  (comment: "sing-box is a common third-party process name"); used by the GUI
  (`Program.cs:405`, `MainWindowViewModel.cs:4156,4233`).
- Consequence: a reused PID makes `Kill(entireProcessTree:true)` terminate an
  unrelated process tree.

### AND-1 (P1) — raw libbox exception to logcat + UI
- Entry point: a libbox `startTunnel` failure in `VpnRouterService`.
- `VpnRouterService.java:682` logs the raw `e.getMessage()`; `:683-685`
  broadcasts it via `EXTRA_ERROR_MESSAGE` + `sendBroadcast`.
- Missing guard: a scrubber exists (`scrubSecrets` `:465`) but is applied only
  in the crash-report builder (`:448`), not on this error path; C#
  `AndroidDiagnosticsExporter.cs:236,270` redacts elsewhere.
- Attacker/trust boundary + impact: logcat readers and any UI/app consumer of
  the broadcast extra can receive server addresses, UUIDs, or config fragments —
  VPN credential/endpoint disclosure.

### SUP-1 (P1) — mutable appimagetool executed without digest
- Entry point: Linux release build, "Build AppImage" step.
- `build-linux.yml:169` downloads `continuous` (rolling, mutable) appimagetool;
  `:170` `chmod +x`; `:172` executes it. The `sha256sum` at `:283` only emits
  sidecars for final artifacts, not input verification.
- Missing guard: no pinned version, no digest verification before execution.
- Attacker/trust boundary + impact: a compromised AppImageKit release channel or
  CDN MITM yields arbitrary code execution inside the release build → tampering
  of all Linux artifacts (supply-chain takeover).

### SEC-1 (P1) — subscription URLs (provider tokens) logged in full
- Entry point: subscription fetch. `SubscriptionFetcher.cs:62` logs
  `"[Subscription] Fetching {Url}"` with the raw URL; also `:72,:88,:101-103,
  :106,:110,:324-326,:343-344`.
- Missing guard: the only `ScrubSecrets` call in the file is on a failing
  parse-line's content (~`:263`), never the URL.
- Attacker/trust boundary + impact: any local log reader, or a third party
  handed the raw `%ProgramData%\VPNRouter\logs\vpnrouter*.log` (support bundle/
  screenshot/upload), obtains subscription-provider credentials embedded in the
  URL; the on-disk log is written before any diagnostics redaction.

### SEC-2 (P1) — %ProgramData%\VPNRouter has no restrictive ACL
- Entry point: install + runtime directory creation. `AppPaths.cs:112` resolves
  `%ProgramData%\VPNRouter`; `EnsureDirectories` (`:99-108`) is plain
  `Directory.CreateDirectory`. `packaging/windows/install.ps1` creates the data
  root and adds Defender exclusions only — zero `icacls`/`Set-Acl`/
  `FileSystemAccessRule`/`SetAccessControl` in `packaging/windows`.
- Missing guard: no restrictive ACL at install or runtime.
- Attacker/trust boundary + impact: a local unprivileged user on a shared box,
  via standard Windows default `%ProgramData%` inheritance (Users get read on
  subfolders), can read `config.yaml`/`current.json`/logs containing VPN
  credentials, UUIDs, and subscription tokens. (Exploitation confidence Medium
  because it rests on default-ACL inheritance.)

### OBS-1 (P1) — clash API token logged; scrubber misses ws://
- Entry point: Clash /logs stream connect/reconnect. `ClashLogStream.cs:92`
  embeds `&token={secret}` into the URI; `:133` logs the full `_logsUri`
  (`ws(s)://…/logs?level=info&token=<secret>`) at Information.
- Missing guard: the crash-report scrubber scheme list (`CrashReporter.cs:
  169-171`) lacks ws/wss; `_httpUrlPattern` (`:173-175`) matches only
  `https?://`; `_longBase64Pattern` (`:181-183`) needs ≥40 chars — so a short
  clash token in a `ws://…&token=…` line is untouched by `ScrubSecrets` (`:191`).
- Attacker/trust boundary + impact: log readers / shared crash reports obtain
  the clash API secret.

### ZAP-1 (P1) — partial zapret update reported current
- Entry point: zapret update install. `ZapretUpdater.cs:353`
  `CopyDirectoryOverwrite(...)`; `:619-636` catches every per-file exception
  ("Skipped locked file" `:631`) and continues; `:370` derives the new version;
  `:371` `File.WriteAllText(VersionFilePath, version)` regardless of skipped
  files.
- Missing guard: version is not gated on complete replacement.
- Consequence: a locked `winws.exe`/`WinDivert64.sys` stays old while
  `version.txt` claims the new version → a mixed driver/executable install is
  reported current and never retried, leaving a silently-broken zapret install.

---

## 4. Refuted / downgraded findings and what the original audit missed

Fully REFUTED (5):

- **LIFE-1** (orig P1): the original audit read `Dispose`'s early-return in
  isolation and missed that `Release()` does NOT gate on `_disposed`
  (`TunOwnershipLock.cs:108-127`), that `Dispose` itself calls `Release()`
  before disposing the handle (`:129-143`), that the singleton is nulled and
  recreated (`:53-61`), and that `SingBoxManager.Dispose` runs `Stop()`→
  `Release()` before `_tunLock.Dispose()` (`SingBoxManager.cs:340`/`:348`). The
  named semaphore is released every cycle; there is no cross-process block.
  Only a residual P3 handle-churn survives.
- **AND-2** (orig P2): missed the Android platform contract — `super.onRevoke()`
  is `VpnService.onRevoke()`, whose default calls `stopSelf()`; `START_STICKY`
  only revives system-killed services, and the `:1263` double-fire guard makes
  the second teardown a no-op.
- **DATA-2** (orig P2): missed that `AppJsonContext.cs:122-126` carries
  `[JsonSourceGenerationOptions(MaxDepth = 32)]`, which the source generator
  propagates to the `JsonTypeInfo<T>` overload `ProfileManager` actually uses —
  the depth guard is preserved.
- **DATA-5** (orig P2): missed the parser constraint — naive username is split
  on the FIRST colon (`ServerUriParser.cs:779-788`) and is therefore colon-free,
  and Password is the LAST key component (`SubscriptionFetcher.cs:285`), so no
  parser-reachable input constructs a collision.
- **PERF-2** (orig P2): missed ownership — the Free Config VM graph is a
  singleton (built once at `MainWindowViewModel.cs:2760`, VM created once at
  `App.axaml.cs:99`), so "recreated view-model graphs" is false; three
  long-lived HttpClients for app lifetime is the recommended pattern.

PARTIALLY_CONFIRMED (4) — mechanism real, impact/scope overstated:

- **FW-1 / FW-2** (orig P1 → P2): the family-mismatch mechanism is real, but
  the original audit overstated reachability (only bare-IPv6 via custom JSON /
  AWG reaches it; the dominant VLESS-URI path yields a bracketed host that
  `IPAddress.TryParse` rejects) and overstated FW-1's consequence (the automatic
  marker-gated orphan sweep refutes "until the nftables table is manually
  removed"). FW-2's fail-open is the more consequential mechanism (defeats the
  kill-switch's purpose) but still requires a bare-IPv6 server.
- **OBS-2** (orig P2): the substantive OOM claim holds at `CrashReporter.cs:131`,
  but the co-cited `DiagnosticsExporter.TailLines` is the already-bounded
  (fixed) path — that sub-citation is wrong.
- **PERF-1** (orig P2 → P3): the dispose-omission is real and per-reconnect, but
  the heavy `TraceEventSession` IS disposed via `using var session`; the
  residual is a single small `ManualResetEventSlim` (finalizer-closed), not an
  unbounded accumulation of heavy resources.

Downgrades among CONFIRMED:

- **UPD-1** P0 → P1 (sidecar shares the release trust root; size/CRC/content
  guards exist; defense-in-depth loss, not a takeover; also a verbatim duplicate
  of the unfixed 2026-06-04 macOS HIGH finding).
- **NET-1** P1 → P2 (DoS requiring a user to subscribe to an attacker-controlled
  URL; weak 15 s partial bound; a bounded sibling already implements the correct
  pattern).
- **PKG-1** P1 → P2 (real guaranteed build-break but latent — the wgturn branch
  is not reached by current public CI and the cache is not in git).
- **SUP-2** P1 → P2 (version tag is pinned/stable; the gap is missing digest
  verification, not a floating ref — strictly less severe than SUP-1).
- **UI-1** P2 → P3 (cosmetic localization defect, no runtime/data impact).
- **SUP-3** P2 → P3 (two unpinned actions, but the signing workflow is
  manual-only and hard-fails without secrets — currently inert).

What the original audit systematically missed: (a) caller ordering / owner
lifetime that defeats leak claims (LIFE-1, PERF-2); (b) source-generator and
platform contracts (DATA-2, AND-2); (c) parser constraints on constructible
inputs (DATA-5, and the bracketed-vs-bare IPv6 reachability for FW-1/FW-2);
(d) the trust-root limitation that separates integrity from authenticity
(UPD-1); (e) automatic recovery/cleanup paths that refute "permanent brick"
framing (FW-1/FW-2); (f) bounded sibling implementations that already solve the
problem (NET-1, OBS-2).

---

## 5. Proposed execution order for confirmed survivors

Ordered by risk, dependency, and blast radius. Clusters map to the plan's
owner prompts (P01–P11).

1. **Data-loss / lifecycle / failover (P02, P05 first):** DATA-1 (atomic
   settings save) and FAIL-1 (failover callback ownership) — both are P1
   correctness/data-loss defects with broad reachability and small, well-scoped
   root fixes. FAIL-1 also de-risks every later lifecycle change.
2. **Desktop update integrity (P01):** UPD-1 (thread the fetched digest,
   fail-closed) then UPD-2 (repair.go `-File`). UPD-1 is the former P0 and a
   long-standing contract violation; UPD-2 removes a Defender heuristic
   regression. Independent files; can follow #1.
3. **Security / diagnostics (P09):** SEC-1 (URL redaction), SEC-2 (ProgramData
   ACL), OBS-1 (clash token + scrubber), AND-1 (Android error scrub) — cluster
   the redaction work (SEC-1, OBS-1, AND-1 share a redactor concept); SEC-2 is
   an installer/ACL change kept separate.
4. **CLI ownership (P07):** CLI-1 (stop-request protocol) then CLI-2 (reuse the
   existing `ProcessOwnership` gate). Same file (`StopCommand.cs`); do together.
5. **Supply chain (P08):** SUP-1 (pin+digest appimagetool) first (mutable ref,
   executed), then SUP-2 (archive digest), SUP-4 (pin wgturn-core), PKG-1 (derive
   `ARCH`) — PKG-1/SUP-4 share the `build-mac.sh` gate; SUP-3 is inert hygiene.
6. **Kill-switch IPv6 (P03):** FW-1 (Linux `ip6 daddr`) and FW-2 (macOS `inet6`)
   — pure ruleset-builder changes; coordinate the shared IPv6-normalization
   helper and the TEST-1 firewall-wiring test (P11).
7. **Config / protocol parity (P04):** CFG-1 (DNS strategy parity), CFG-2
   (collision-free injected tag), PROTO-1 (dns-tunnel typed skip) — independent
   root causes; split into two PRs if needed.
8. **Data/subscription bounded defects (P05 remainder):** DATA-3, DATA-4,
   DATA-6, NET-1 — small, independent; NET-1 reuses the existing bounded
   decompression pattern.
9. **Desktop UI (P06):** FLOW-1 (P1 — Smart Connect persistence; do early if UI
   capacity exists, otherwise slot here), UI-2 (narrow rule layout), UI-1
   (localization).
10. **Updater/resources hygiene (P10):** ZAP-1 (P1 — version-after-complete),
    ZAP-2, ZAP-3, PERF-1 — ZAP-1 first (silent broken-install), then the
    atomic-replacement and disposal cleanups.
11. **Cross-cut regression (P11):** TEST-1 (pipeline kill-switch wiring test),
    coordinated with the FW-1/FW-2 IPv6 tests.

Parallelism: clusters 2–10 touch disjoint files and may run in separate
`codex/<topic>` branches per plan §3.3, but none should modify a shared worktree
concurrently. P11/P12 integrate last.

---

## 6. Ledger corrections required (do NOT edit the ledger)

`plans/OPEN-DEFECTS.md` was not modified. The following corrections are required
when the ledger is next updated (P12):

- **UPD-1:** record as P1 (not P0); note it is the same defect documented
  2026-06-04 in `plans/macos-bug-audit-2026-06-04.md` (HIGH) and never
  tracked/fixed — add the cross-reference.
- **LIFE-1:** downgrade/remove the P1 lifecycle-block claim; if retained, record
  only as a P3 handle-churn hygiene item with the refutation rationale.
- **NET-1:** downgrade P1 → P2.
- **FW-1 / FW-2:** downgrade P1 → P2 and reword to drop the "manual nftables
  removal" / "fails open permanently" framing (automatic orphan sweep exists;
  reachability limited to bare-IPv6 inputs).
- **PKG-1:** downgrade P1 → P2 and mark latent (wgturn branch not reached by
  public CI).
- **SUP-2:** downgrade P1 → P2 (pinned version; missing-digest gap).
- **UI-1:** downgrade P2 → P3 (cosmetic localization).
- **SUP-3:** downgrade P2 → P3 (inert/manual-only signing workflow).
- **PERF-1:** downgrade P2 → P3 (heavy ETW session already disposed via `using`).
- **AND-2, DATA-2, DATA-5, PERF-2:** remove/close as REFUTED with the guard /
  platform-contract / parser-constraint / singleton-ownership rationale recorded.
- **OBS-2:** correct the evidence to cite `CrashReporter.cs:131` only; remove the
  `DiagnosticsExporter.TailLines` sub-citation (it is the bounded/fixed path).
- **TEST-1:** correct the misleading-comment citation to the test-file header
  (`StartupPipelineTests.cs:33-34`), not `StartupPipeline.cs:1090`.

---

## 7. Final invariant block

```text
Expected findings: 39
Processed findings: 39
Missing IDs: none
Duplicate IDs: none
```
