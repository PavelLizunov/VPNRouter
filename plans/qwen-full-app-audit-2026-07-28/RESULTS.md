# Qwen full-application audit — 2026-07-28

## Outcome

Eighteen independent, read-only Qwen reviews covered the application by
subsystem. Codex then traced every reported path through the current working
tree and removed duplicates, already-tracked items, and claims without a
concrete failure mode.

| Severity | Confirmed | Meaning |
|---|---:|---|
| P0 | 1 | Release blocker: the desktop updater fetches a SHA256 but does not enforce it |
| P1 | 18 | Important correctness, security, lifecycle, packaging, or data-loss defects |
| P2 | 20 | Bounded defects and test/robustness gaps |
| Total | 39 | Unique, code-verified findings |

No product code, VPN process, service, installer, firewall, or ProgramData
state was changed during the audit.

Follow-up execution plan:
`plans/qwen-audit-remediation-prompt-pool-2026-07-28.md`.

## P0 — release blocker

| ID | Evidence | Verified failure | Minimum root fix |
|---|---|---|---|
| UPD-1 | `VPNRouter.Core/Services/UpdateChecker.cs:119,251`; `IUpdateSource.cs:60` | The desktop adapter discards `UpdateSourceInfo.AssetSha256` by constructing the legacy `UpdateInfo` with `FullChecksumUrl=null`. `DownloadAndStageAsync` therefore skips SHA256 validation on the normal `GitHubReleaseSource -> UpdateChecker` path. | Thread the already-fetched digest into staging and add a desktop SHA-mismatch contract test. |

## P1 — important

| ID | Evidence | Verified failure | Minimum root fix |
|---|---|---|---|
| LIFE-1 | `VPNRouter.Core/Services/TunOwnershipLock.cs:114` | The singleton remains `_disposed` after its first lifecycle. A second acquire succeeds, but the second `Dispose` returns early and never releases the named semaphore, blocking other VPNRouter processes until app exit. | Make the lock lifecycle re-armable or replace the singleton instance after disposal. |
| FAIL-1 | `VPNRouter.Core/Services/VpnEngine.cs:1495,1527`; `StartupPipeline.cs:1041,1181` | Pre-start and post-start failover use the same `_failover ??=` slot with incompatible restart delegates. If pre-start wiring wins, later failover bypasses teardown, the lifecycle gate, and the session cancellation guard; it can orphan sing-box/TUN state or revive a user-disconnected tunnel. | Give each phase the safe teardown/restart delegate, or replace the stored callback when the phase changes. |
| FW-1 | `VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs:205,226` | IPv6 server literals are accepted and then removed from the allow-list while the `inet` policy drops all IPv6. After a crash, an IPv6-only server cannot reconnect until the nftables table is manually removed. | Emit IPv6 allow rules for parsed IPv6 server addresses. |
| FW-2 | `VPNRouter.Core/Platform/macOS/MacFirewallManager.cs:340,359` | IPv6 server literals are emitted as `inet` PF rules. PF rejects the atomic ruleset, so the kill-switch fails open. | Emit `inet6` rules for IPv6 addresses and cover mixed-family rulesets. |
| DATA-1 | `VPNRouter.Core/Services/SettingsLoader.cs:536` | `config.yaml` is overwritten with `File.WriteAllText`; a crash or power loss during save can truncate all settings and credentials. | Write a sibling temporary file, flush, and atomically replace the destination. |
| NET-1 | `VPNRouter.Core/Services/PolicyHttpClient.cs:112-118`; `SubscriptionFetcher.cs:85` | An untrusted subscription response is buffered completely with no byte limit. A provider or intercepted endpoint can exhaust process memory. | Stream with a fixed maximum response size before decoding. |
| FLOW-1 | `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:536`; `MainWindowViewModel.cs:3783` | Smart Connect stores the probed winner, then `SaveSettings` immediately overwrites it from the stale selected server. The app connects to the stale/dead entry instead of the measured winner. | Update the selected VM state before saving, or save without re-deriving the active server. |
| UPD-2 | `VPNRouter.GUI/repair.go:58`; `VPNRouter.App/Services/SelfRepair.cs:122` | The repair trampoline still uses inline PowerShell `-Command` download-and-execute, reintroducing the Defender ClickFix heuristic already removed from the app repair path. | Reuse the temporary `.ps1` plus `-File` pattern. |
| CLI-1 | `VPNRouter.CLI/Commands/StopCommand.cs:23`; `StartCommand.cs:165` | `vpnrouter stop` kills only the recorded child PID. The still-running `start` process treats it as a crash, restarts sing-box, and then cannot re-record state because the state file was cleared; the VPN runs untracked. | Send a stop request to the owning engine/process instead of killing its child directly. |
| CLI-2 | `VPNRouter.CLI/Commands/StopCommand.cs:23` | The PID from the state file is killed without verifying image/path ownership. PID reuse can terminate an unrelated process tree. | Revalidate executable identity with the existing process-ownership logic before kill. |
| AND-1 | `VPNRouter.Android/VpnRouterService.java:682` | Raw libbox exception messages are sent to logcat and the UI. Those messages can include server addresses, UUIDs, or config fragments even though a scrubber already exists elsewhere. | Scrub once before both logging and broadcasting. |
| PKG-1 | `build-mac.sh:90,93` | `${ARCH}` is never defined under `set -u`. The macOS build aborts whenever the wgturn source/build branch becomes reachable. | Derive `ARCH` once from the build target before the branch. |
| SUP-1 | `.github/workflows/build-linux.yml:169` | A mutable `continuous` appimagetool executable is downloaded without a digest and executed inside the release build. | Pin an immutable version and SHA256. |
| SUP-2 | `.github/workflows/build-linux.yml:107` | The sing-box/libcronet archive is downloaded and bundled into all Linux artifacts without an integrity check. | Pin and verify the release archive digest before extraction. |
| SEC-1 | `VPNRouter.Core/Services/SubscriptionFetcher.cs:62,72,88,101,106,110,324,343` | Full subscription URLs, commonly containing provider tokens, are written to logs. | Log a redacted origin/identifier, never the full credential-bearing URL. |
| SEC-2 | `VPNRouter.Core/AppPaths.cs:112`; `packaging/windows/install.ps1` | `%ProgramData%\VPNRouter` inherits an ACL that lets local Users read `config.yaml`, `current.json`, and logs containing VPN credentials. | Apply a restrictive install/runtime ACL and preserve required service access. |
| ZAP-1 | `VPNRouter.Core/Services/ZapretUpdater.cs:347-374,619-636` | Copy failures for locked files are swallowed, but the new `version.txt` is still written. A mixed old-driver/new-executable installation is reported current and will not retry. | Mark the version only after every required file is replaced successfully. |
| OBS-1 | `VPNRouter.Core/Services/ClashLogStream.cs:133`; `CrashReporter.cs:169-199` | The full WebSocket URI, including `?token=<clash_api_secret>`, is logged. The crash-report scrubber does not recognize `ws://` URLs or this key/value form. | Never log the token-bearing URI and extend the shared secret redactor. |

## P2 — bounded findings

| ID | Evidence | Confirmed defect |
|---|---|---|
| CFG-1 | `CustomConfigInjector.cs:1532`; `ConfigGenerator.cs:995` | Custom include-split config leaves AAAA enabled when `Tun.Ipv6Enabled=false` unless `ForceIpv4Only` is also set. |
| CFG-2 | `CustomConfigInjector.cs:323` | Injected urltest tag `"auto"` can collide with an existing non-urltest outbound and make sing-box reject duplicate tags. |
| DATA-2 | `ProfileManager.cs:262,349` | Production profile deserialization bypasses the documented/tested `SafeJsonOptions(MaxDepth=32)` and uses the source-generated context default instead. |
| DATA-3 | `SettingsMigrator.cs:698` | The v7→v8 migration rewrites an explicitly selected MTU 1280 to 1420 despite the promise to preserve custom values. |
| DATA-4 | `FreeConfigAggregator.cs:170,187` | Duplicate remote IDs make `ToDictionary` throw, skipping verified-status preservation and cached carry-over. |
| DATA-5 | `SubscriptionFetcher.cs:285` | Colon-joining username/password in the dedupe key lets distinct Naive credentials collide. |
| DATA-6 | `FreeConfigCache.cs:131` | Delete-then-move creates a crash window that loses the cache; another pool component already uses overwrite-move. |
| PROTO-1 | `VlessDeepVerifier.cs:434`; `ConfigGenerator.cs:1563` | DNS-tunnel is verified as ordinary VLESS instead of through its local sidecar and therefore produces a false failure. |
| UI-1 | `VPNRouter.App/Views/MainWindow.axaml:713` | The update button is hardcoded English although a localized Update string exists. |
| UI-2 | `VPNRouter.App/Views/Pages/NetworkPage.axaml:1482` | Read-mode rule rows have fixed columns wider than the minimum detail pane and can clip the value/delete action. |
| AND-2 | `VpnRouterService.java:1285,577` | `onRevoke` stops the tunnel but not the sticky service, allowing a doomed restart attempt and spurious error. |
| SUP-3 | `.github/workflows/sign-windows.yml:69,75` | Two signing workflow actions use mutable major-version tags while the rest of CI SHA-pins actions. |
| SUP-4 | `build-mac.sh:82,88` | wgturn-core is cloned from floating HEAD; the commit is recorded but not pinned or asserted before bundling. |
| SEC-3 | `EmergencyChannelManager.cs:174` | User-controlled URLs are interpolated into a single `Arguments` string, so quotes can inject or override command arguments. |
| PERF-1 | `VpnEngine.cs:832,845`; `EtwProcessMonitor.cs:198` | A per-connect ETW monitor is stopped but not disposed, retaining its disposable synchronization state across reconnects. |
| PERF-2 | `FreeConfigAggregator.cs:27` and its three fetchers | Recreated Free Config view-model graphs retain three undisposed `HttpClient` pools and supporting synchronization state. |
| TEST-1 | `StartupPipelineTests.cs:33,469,538`; `StartupPipeline.cs:1090` | The full/split `isFullTunnel` kill-switch wiring has no executable regression test despite a comment claiming one. |
| ZAP-2 | `EmergencyChannelManager.cs:131,208` | Exited wgturn processes are neither disposed nor cleared before the field is overwritten. |
| ZAP-3 | `WgturnUpdater.cs:427` | Delete-then-move can destroy the working CLI binary if replacement fails, and cleanup then removes the temporary recovery copy. |
| OBS-2 | `CrashReporter.cs:131`; `DiagnosticsExporter.TailLines` | The crash handler reads the entire daily log just to keep 200 lines, risking OOM exactly when diagnostics are needed. |

## Consolidated or rejected raw claims

- The macOS `${ARCH}` issue was reported by two reviewers and counted once.
- Zapret size-only verification and Wgturn's missing published checksum sidecar
  were already present in `plans/OPEN-DEFECTS.md`; they were not duplicated.
- SignPath enrollment was already present in the ledger; only the distinct
  mutable-action-tag issue remains above.
- A `StatusCommand` process handle, a method-local `SemaphoreSlim`, and the
  “Automatic (Delayed)” label mismatch were dropped because they do not create
  a material application failure in their current short-lived paths.
- Five reviewers initially returned prose instead of their promised array and
  were rerun in parallel with a stricter output-only instruction. One further
  response contained a valid array after a prose prefix; that array was
  normalized without changing its findings. Final files are in
  `raw-results-rerun/`.

## Audit artifacts

- `prompts.json` — the 18 subsystem prompts plus the shared read-only contract.
- `rendered-prompts/` — exact prompts sent to Qwen.
- `raw-results-run2/` — first complete parallel result set.
- `raw-results-rerun/` — corrected output-only reruns for five reviewers.
- `raw-errors-run2/` and `raw-errors-rerun/` — stderr captured per process.

The raw result severity labels were not accepted at face value. The P0/P1/P2
levels above are the post-verification triage.
