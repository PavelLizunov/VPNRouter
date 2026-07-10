# Claude Code task: readonly bug-hunt follow-up

Date: 2026-06-28
Source: Codex readonly code review
Scope: performance, memory/resource leaks, connection stability

## Important constraints

- This report was produced from source review only.
- Codex did not edit source code while reviewing.
- Codex did not run the app, build, or tests while reviewing.
- Treat every item below as a candidate to verify against the current working tree before patching.

## Goal

Fix or explicitly reject with evidence the findings below:

- At least 3 performance defects.
- At least 3 potential memory/resource leak points.
- At least 3 connection stability defects.

## Global acceptance criteria

- `dotnet build VPNRouter.sln -c Release` passes.
- Relevant focused tests are added or updated for each fixed behavior.
- No `--no-verify`, no force push to `main`.
- If UI behavior is touched, verify the affected end-to-end scenario, not only that a tab renders.
- For any issue rejected as false positive, leave a short note in the final report with code evidence.

## Performance findings

### P1. TgProxy runtime detector enumerates all TCP listeners every connected poll

Evidence:

- `VPNRouter.Core/Services/RuntimeStatusDetector.cs:61`
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:93`
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:119`

Current behavior:

- The runtime status timer runs every 2 seconds.
- While any component is running, idle throttling is reset.
- `RuntimeStatusDetector.IsTgProxyRunning(port)` calls `IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()` every poll.
- This enumerates all TCP listeners on the UI timer path even when TgProxy is disabled and VPN/Zapret is what keeps the poll hot.

Impact:

- Persistent allocation and OS enumeration overhead during connected sessions.
- Avoidable UI-thread work every 2 seconds.

Suggested fix:

- Skip TgProxy port probing when TgProxy is disabled and no stale/unknown TgProxy state needs reconciliation.
- Or throttle the port probe independently from the VPN/Zapret process probes.
- Consider caching the last TgProxy probe result for a short interval.

Suggested tests:

- Unit-test that TgProxy probe is not invoked when TgProxy is disabled and VPN is running.
- Unit-test that an explicit TgProxy state transition still forces a refresh.

### P1. Connection stats poll does too much work every 2 seconds

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:96`
- `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:105`
- `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:107`
- `VPNRouter.Core/Services/ClashSingBoxApi.cs:223`

Current behavior:

- While connected, the same 2-second timer calls `MaybePollConnStats()`.
- The poll can call both `/proxies/proxy` and `/connections`.
- `/connections` deserializes the full response just to get aggregate totals and `Connections.Count`.

Impact:

- Persistent HTTP + JSON allocation hot path during every connected session.
- Cost grows with the number of active connections.

Suggested fix:

- Poll less often when stats are not visible or the UI window is inactive/minimized.
- Consider using a lighter Clash API endpoint if available for totals only.
- Consider adaptive polling: fast while traffic changes, slower when idle.

Suggested tests:

- Unit-test the in-flight guard remains intact.
- Add characterization around zero/failure snapshots so throttling does not regress stale-rate behavior.

### P2. AutoSelect server resolution allocates and sorts every stats poll

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:159`
- `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:184`
- `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs:189`

Current behavior:

- `MaybeRefreshAutoSelectedAsync()` runs during the stats poll when AutoSelect + subscribe mode are active.
- `ResolveAutoSelectedServer()` scans `SubscriptionServers`, filters by suffix, sorts by name length, then takes the first result.

Impact:

- On large subscriptions, this is a repeated LINQ allocation and sort every poll.

Suggested fix:

- Maintain a cache from generated urltest member tag to `ServerViewModel`.
- Rebuild the cache when subscription servers change, not every poll.
- If suffix matching must remain, use a simple best-match loop without LINQ sort.

Suggested tests:

- Server names containing `-` still resolve correctly.
- Longest suffix still wins.
- Cache invalidates when subscription list changes.

### P2. Free configs batching repeatedly walks the queue

Evidence:

- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:520`
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:542`

Current behavior:

- Batch slices are built via `queue.Skip(i).Take(batchSize).ToList()`.
- The next prefetch slice uses the same pattern.

Impact:

- For large queues this becomes repeated traversal from the beginning, roughly O(n^2) across batches.

Suggested fix:

- Use `queue.GetRange(start, count)` if `queue` is a `List<T>`.
- Or materialize once into an array/list and slice by index.

Suggested tests:

- Batch boundaries still match existing behavior.
- Last partial batch is preserved.

### P2. Free configs visible cap is applied after building/sorting/grouping all items

Evidence:

- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1901`
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1907`

Current behavior:

- The code creates `FreeConfigItemViewModel` for every filtered config.
- Then it sorts and groups everything.
- Only after that it applies `Take(300)`.

Impact:

- The visible cap does not cap the expensive part of the work.
- Large free config caches still pay full VM allocation + sort/group cost.

Suggested fix:

- Deduplicate/select best entries before creating item VMs where possible.
- Keep only top candidates during selection instead of sorting all entries.

Suggested tests:

- Best entry per IP is still selected.
- Visible list stays capped at 300.

## Memory and resource leak findings

### P1. TgProxy StatsUpdated subscriptions accumulate

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:6184`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:6185`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:6091`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:7094`
- `VPNRouter.Core/Services/TgProxyManager.cs:46`
- `VPNRouter.Core/Services/TgProxyManager.cs:658`

Current behavior:

- `_tgProxy ??= new TgProxyManager(_logger)` reuses the manager.
- Every start adds a new lambda to `_tgProxy.StatsUpdated`.
- Stop calls `_tgProxy?.Stop()` but does not unsubscribe.
- `MainWindowViewModel.Dispose()` does not call `_tgProxy.Dispose()`.

Impact:

- Repeated TgProxy stop/start can duplicate UI updates.
- The event subscription can keep the VM alive.
- Manager-owned resources are not released on VM disposal.

Suggested fix:

- Store the handler in a field so it can be unsubscribed.
- Subscribe once per manager lifetime.
- In `Dispose()`, unsubscribe and call `_tgProxy.Dispose()`.

Suggested tests:

- Starting/stopping TgProxy multiple times results in one stats update per manager event.
- VM dispose calls manager dispose or otherwise releases the handler.

### P1. Zapret ImmediateExitDetected handler and manager are not disposed by VM

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:4638`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5055`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5428`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:7094`
- `VPNRouter.Core/Services/ZapretManager.cs:66`
- `VPNRouter.Core/Services/ZapretManager.cs:371`

Current behavior:

- `_zapret.ImmediateExitDetected += OnZapretImmediateExit` is wired in multiple paths.
- The code only wires when `_zapret == null`, so duplicate subscription is less likely than TgProxy.
- But `MainWindowViewModel.Dispose()` does not unsubscribe, stop, dispose, or null `_zapret`.

Impact:

- A live `ZapretManager` can keep a stale VM alive through the event.
- Process handle/resource cleanup depends on explicit stop paths and is not guaranteed on VM disposal.

Suggested fix:

- In `Dispose()`, detach `ImmediateExitDetected`, call `_zapret.Dispose()`, and null the field.
- Ensure quit/stop paths remain idempotent.

Suggested tests:

- Dispose detaches `ImmediateExitDetected`.
- Immediate exit after VM dispose does not mutate VM toast state.

### P1. Zapret probe timer can keep VM alive after disposal

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5134`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:5151`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:7094`

Current behavior:

- `StartZapretProbeElapsedTimer()` creates a `System.Threading.Timer`.
- The callback captures the VM and posts to the UI thread.
- `StopZapretProbeElapsedTimer()` exists but is not called from `Dispose()`.

Impact:

- Closing/recreating the VM during a Zapret probe can keep the stale VM rooted.
- Timer callbacks can post UI updates after disposal.

Suggested fix:

- Call `StopZapretProbeElapsedTimer()` from `Dispose()`.
- Guard callback with `_disposed`.

Suggested tests:

- Dispose during active probe disposes the timer.
- No elapsed update is posted after dispose.

### P2. Toast CTS/tasks are not cleaned up on VM disposal

Evidence:

- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:1324`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:1342`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:2016`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:2025`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:6483`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:7094`

Current behavior:

- `_zapretAvBlockToastCts` and `_rulesToastCts` are swapped/disposed during new toasts.
- They are not cancelled/disposed in `MainWindowViewModel.Dispose()`.
- TgProxy toast uses `Task.Delay(2500).ContinueWith(...)` with a token counter but no dispose cancellation.

Impact:

- Short-lived retention of disposed VM and delayed UI posts after dispose.
- Low severity compared with manager events, but easy to clean up.

Suggested fix:

- Cancel/dispose active toast CTS fields in `Dispose()`.
- Make delayed callbacks check `_disposed` before posting.

Suggested tests:

- Dispose clears active toast CTS fields.
- Delayed toast continuation does not change properties after dispose.

## Connection stability findings

### P1. StrictDNS failover state changes before hot-reload success

Evidence:

- `VPNRouter.Core/Services/HealthMonitor.cs:630`
- `VPNRouter.Core/Services/HealthMonitor.cs:635`
- `VPNRouter.Core/Services/HealthMonitor.cs:642`
- `VPNRouter.Core/Services/HealthMonitor.cs:653`
- `VPNRouter.Core/Services/HealthMonitor.cs:1011`
- `VPNRouter.Core/Services/HealthMonitor.cs:1178`
- `VPNRouter.Tests/HealthMonitorStrictDnsFailoverTests.cs:107`
- `VPNRouter.Tests/Fakes/FakeSingBoxApi.cs:112`

Current behavior:

- `ReconcileStrictDnsFailover()` decides a fail-open/re-arm action.
- It sets `_strictDnsFailedOver = failOpen` before generating config and attempting reload.
- It logs and fires `StrictDnsFailoverChanged` even if `TryHotReloadViaApi()` returns `false`.
- Tests assert the private flag but do not cover reload failure.

Impact:

- HealthMonitor/UI can believe StrictDNS failover was applied while running sing-box still has the old config.
- Future policy decisions may see `currentlyFailedOver=true` and stop retrying the action.
- User can remain in broken DNS state after a failed reload.

Suggested fix:

- Build config for the target state without committing `_strictDnsFailedOver` first.
- Commit `_strictDnsFailedOver` and fire `StrictDnsFailoverChanged` only if reload succeeds.
- Or roll the flag back and retry on the next tick if reload fails.

Suggested tests:

- Proxy unreachable twice + `ReloadConfigAsync=false` does not set `_strictDnsFailedOver`.
- No `StrictDnsFailoverChanged` event is raised when reload fails.
- A later successful reload applies the state.
- Re-arm path has the same failure handling.

### P1. AWG endpoint preflight accepts any endpoint tagged `proxy`

Evidence:

- `VPNRouter.Core/Services/LeakProtection.cs:289`
- `VPNRouter.Core/Services/LeakProtection.cs:304`
- `VPNRouter.Core/Services/ConfigSanityCheck.cs:107`
- `VPNRouter.Core/Services/ConfigSanityCheck.cs:216`
- `VPNRouter.Core/Models/VPNConfig.cs:128`
- `VPNRouter.Core/Models/VPNConfig.cs:139`
- `VPNRouter.Core/Models/VPNConfig.cs:162`

Current behavior:

- Endpoint-based AmneziaWG proxy is treated as valid when an endpoint has `tag == "proxy"`.
- `LeakProtection` validates proxy outbounds but not proxy endpoints.
- `ConfigSanityCheck.HasProxyEndpoint()` checks only the tag.

Impact:

- A malformed custom/imported endpoint can pass pre-start checks and fail later at sing-box config load.
- Examples: empty `private_key`, no `address`, no `peers`, invalid peer `address`/`port`/`public_key`, empty `allowed_ips`.

Suggested fix:

- Add endpoint structural validation for `tag == "proxy"` and `type == "wireguard"`.
- Keep generated AWG parser validation, but do not rely on it for custom/imported JSON.
- Return clear pre-start errors before launching sing-box.

Suggested tests:

- Endpoint with only `{ "tag": "proxy" }` fails validation.
- Empty private key fails validation.
- Missing peers fails validation.
- Invalid peer port/public key fails validation.
- Valid generated AWG endpoint still passes.

### P1. Runtime detector and cleanup can treat external sing-box as VPNRouter tunnel

Evidence:

- `VPNRouter.Core/Services/RuntimeStatusDetector.cs:32`
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:119`
- `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs:217`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3897`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3961`
- `VPNRouter.Core/Services/OrphanCleanup.cs:88`

Current behavior:

- `RuntimeStatusDetector.IsVpnRunning()` returns true for any process named `sing-box`.
- UI can mark `IsConnected=true` and show "Connected via service".
- Stop/Connect takeover paths call `OrphanCleanup.KillOrphans(... respectTunLock: false)`.
- `OrphanCleanup` kills processes by name: `KillByName("sing-box", null)`.

Impact:

- A user-owned or third-party `sing-box` process can be mistaken for VPNRouter.
- Stop/Connect can kill an unrelated tunnel.
- This is both a false UI state and a connection stability issue.

Suggested fix:

- Identify VPNRouter-owned sing-box by command line/config path, parent/lock ownership, or a pid file.
- In Stop/Connect takeover paths, only kill external sing-box after confirming it is VPNRouter-managed or user explicitly requested kill-conflict.
- Keep startup orphan cleanup conservative.

Suggested tests:

- Fake process query: unrelated sing-box does not make UI connected via service.
- Orphan cleanup does not kill unrelated sing-box when command line does not match VPNRouter config path.
- Service-owned VPNRouter sing-box is still detected/stoppable when intended.

### P2. QUIC reject disablement is tied to any endpoint, not endpoint capability

Evidence:

- `VPNRouter.Core/Services/ConfigGenerator.cs:140`
- `VPNRouter.Core/Services/ConfigGenerator.cs:1845`
- `VPNRouter.Core/Services/ConfigGenerator.cs:1922`

Current behavior:

- `proxyIsUdpNative` is passed as `endpoints != null && endpoints.Count > 0`.
- `BuildRoute()` skips QUIC reject when `proxyIsUdpNative` is true.

Impact:

- Today this likely works because the only endpoint path is AWG, which is UDP-native.
- Future endpoint types, or malformed endpoint routing, could disable QUIC reject for a proxy path that is not actually UDP-native.
- That can reintroduce HTTP/3/QUIC stalls for apps routed over a TCP-only proxy.

Suggested fix:

- Derive `proxyIsUdpNative` from the selected proxy type/capability, not from "any endpoint exists".
- Keep the AWG path explicitly true.

Suggested tests:

- AWG endpoint still skips QUIC reject.
- VLESS/TCP-only proxy still gets QUIC reject when `BlockQuicOnTcpProxy` is enabled.
- A non-AWG endpoint does not automatically disable QUIC reject unless marked UDP-native.

## Notes from the readonly pass

- Older AWG issues mentioned in earlier reports appeared already fixed in the current source:
  - AWG URI parser preserves `+` and validates required key/address fields.
  - AWG same-host selection with mixed VLESS/AWG entries appears covered by tests.
  - `SingBoxFeatures` now probes bundled binary first and drains stdout/stderr asynchronously.
- Do not spend time refixing those unless current code or tests contradict this note.

