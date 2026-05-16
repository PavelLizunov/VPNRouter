## Android code review — 2026-05-16

Senior review of `VPNRouter.Android/` conducted during overnight autonomous Phase 4 work.

**Severity counts**: 2 Critical, 5 High, 4 Medium, 4 Low/Info.

### 1. Security & Privacy

#### Critical-1. Full sing-box config dumped to externally-readable storage
**File**: `MainActivity.cs:866-869, 972-975`

After each Connect, the *full* generated sing-box JSON is written to `GetExternalFilesDir(null)/config-dump.json` and the sing-box log goes to the same dir. JSON contains VLESS UUID, server address, Reality public key, short ID, SNI, and (custom mode) anything the user pasted. `GetExternalFilesDir()` resolves to `/sdcard/Android/data/<pkg>/files/` — readable by any device user, `adb pull`, or sideloaded app with `READ_EXTERNAL_STORAGE` on pre-API-30, and trivially extractable via USB/file-manager/backup on all API levels. No `--debug` gate.

**Fix**: write to `FilesDir` (private sandbox) by default; expose external dump only behind an explicit Settings toggle.

#### Critical-2. External-file override for VLESS URI (takeover surface)
**File**: `MainActivity.cs:895-919`

`StartTunnelService` reads `GetExternalFilesDir(null)/test-uri.txt` and uses its contents as the active VPN URI on every connect, overriding what the user configured. Any app with shared-storage write access (or USB / adb / file-manager) can drop a `test-uri.txt` and silently redirect all routed traffic through an attacker-controlled server. No debug gate, no Settings toggle, no banner.

**Fix**: surround with `#if DEBUG` or `ApplicationInfo.Flags.Debuggable` check; remove entirely from shipped builds.

#### High-1. Java crash-report scrub regex too permissive
**File**: `VpnRouterService.java:265-280` (`scrubSecrets`)

Strips `vless://...`, `https://...`, UUID-shaped tokens, `[A-Za-z0-9+/_-]{40,}` blobs. Does NOT strip server `host:port` (frequent in Go-side stack traces: `tcp 1.2.3.4:443: i/o timeout`), Reality short-id, or 32-hex public key. Crash file is in private `FilesDir/crashes/` (OK) but the "Share crash log" UX hands the unredacted text to whichever app the user picks.

**Fix**: add `\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b` for IP:port; add explicit Reality-key hex stripping.

#### High-2. Auto-update has no SHA verification
**File**: `AndroidUpdater.cs:280-307`

Hands `cacheDir/update.apk` to PackageInstaller via Intent. Doc claims signature gate ("system PackageInstaller validates APK signature against existing app") — true post-install, but MITM that replaces GitHub asset with different-signature APK throws `INSTALL_FAILED_UPDATE_INCOMPATIBLE`, which a careless user could resolve by uninstall+reinstall, defeating the gate. Desktop's UpdateChecker ships a separate `-sha256.txt` asset for exactly this.

**Fix**: publish `.sha256` companion on each release; fetch + verify before handing APK to PackageInstaller.

#### High-3. sing-box log path writes secrets externally
**File**: `MainActivity.cs:808-823`, `VpnRouterService.java:486-499`

Same surface as Critical-1: `singboxLogPath` defaults to `GetExternalFilesDir(null)/singbox.log`. sing-box at `level=info` emits remote server hostnames, UUIDs, Reality handshake metadata.

**Fix**: route to `FilesDir`, expose via Storage Access Framework on user request.

### 2. Hidden Bugs / Race Conditions

#### High-4. Static event subscriptions never unsubscribed
**File**: `AndroidApp.axaml.cs:501,505`

```csharp
MainActivity.IntentChanged += OnIntentChanged;
MainActivity.TunnelErrorReported += OnTunnelErrorReported;
```

No matching `-=` anywhere in the codebase. Every Activity recreation that triggers a fresh `AndroidApp` (e.g. theme/locale flip pre-fix) adds another subscriber rooted in a static field, retaining the previous `AndroidApp` instance and entire visual tree.

**Fix**: `WeakEventManager` wrapper OR explicit unsubscribe on view detach. Add to a `Dispose()`/`DetachedFromVisualTree`.

#### High-5. `_pendingExportContent`, `Pending*Callback` static fields unguarded
**File**: `MainActivity.cs:422-433, 589, 594`

4 static mutable fields as one-shot state across `StartActivityForResult` round-trips. Activity recreation mid-trip means surviving statics point to previous `AndroidApp`'s callbacks — first export callback fires into dead UI tree.

**Fix**: persist via `onSaveInstanceState`/`SavedStateHandle`; or block re-issue while a request is pending and verify Activity+callback target both alive before invoking.

#### Medium-1. `_appPickerSelected` reassignment race
**File**: `AndroidApp.axaml.cs:5558` (assign) vs `:5878-5880` (closure)

`ReseedAppPickerTabState()` does `_appPickerSelected = new HashSet<string>(...)`. Per-row checkbox lambdas capture via closure-over-this; safe today because reseed is synchronous on UI thread. Risk: async reseed + concurrent compute could interleave.

**Fix**: take local snapshot at top of each reader/mutator OR use `ConcurrentDictionary<string, byte>` and never replace ref (only `Clear()` + add).

#### Medium-2. `_advAppsCustomCategories` mutated + iterated on same thread, no snapshot
**File**: `AndroidApp.axaml.cs:6470-6476, 6567-6577`

`UpdateAllCategoryCounts` enumerates while `OnAdvAppsAddCategoryClicked` adds. Single-threaded today; future async path would crash.

**Fix**: `foreach (var cat in _advAppsCustomCategories.ToList())`.

#### Medium-3. `LaunchCameraForQr` temp JPEG leak path
**File**: `MainActivity.cs:629-694`

`tempFile.CreateNewFile()` before `StartActivityForResult`. If the call throws OR the OS kills the process between intent send + result, `_pendingQrTempFilePath` is reset to null but the JPEG persists. `OnDestroy` doesn't sweep.

**Fix**: startup cleanup pass deletes any `qr_scan_*.jpg` under `CacheDir`.

### 3. Memory Leaks

#### High-6. `CancellationTokenSource` for chip pulses canceled but never disposed
**File**: `AndroidApp.axaml.cs:3982-3984, 4040-4042, 4118-4145`

`SetVpnChipState` / `SetZapretChipState`: `_vpnPulseCts?.Cancel(); _vpnPulseCts = null;` — never `Dispose()`. Each chip state change leaks Timer + ManualResetEvent. Chips toggle on every connect/disconnect/DPI bypass change.

**Fix**: `var tmp = _vpnPulseCts; _vpnPulseCts = null; tmp?.Cancel(); tmp?.Dispose();`

#### Medium-4. Mascot bitmap loader leaks asset stream
**File**: `AndroidApp.axaml.cs:3730-3731`

`AssetLoader.Open(...)` returned Stream never disposed. One-shot per process, but copy-paste hazard for icon-loader pattern reused across file.

**Fix**: `using var stream = AssetLoader.Open(...);`

#### Low-1. `_diagnosticsTimer` reused but never released
**File**: `AndroidApp.axaml.cs:4174-4195`

Allocated once, held forever. Fine alone; aggravates static-event-leak (High-4).

### 4. Best Practices / API Drift

#### Medium-5. `startForeground` 2-arg overload on Android 14+
**File**: `VpnRouterService.java:330`

Target SDK 34 (Android 14). Should call 3-arg `startForeground(id, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED)` for forward-compat + ANR enforcement.

#### Low-2. `FOREGROUND_SERVICE_SYSTEM_EXEMPTED` may trigger Play Store review
**File**: `AndroidManifest.xml:25, 81`

The Play Store policy for this permission requires the system-exempted permission to be granted (flagged-app-only path). Most VPN apps declare neither — they rely on VpnService implicit recognition. Defensible per Google's doc, but verify if/when shipping to Play Store.

#### Low-3. Broad `ConfigChanges` mask hides config-change bugs
**File**: `MainActivity.cs:53-64`

ScreenSize / Density / FontScale / Locale / UiMode all declared, so Activity is NOT recreated on those changes — masks the event-leak (High-4). If declaration ever trimmed, leak becomes visible.

### 5. Library Versions

#### Info-1. Package set current as of 2026-05
- Avalonia 11.3.12 — latest stable in 11.3.x; no CVEs.
- Newtonsoft.Json 13.0.3 — latest.
- YamlDotNet 15.1.2 — current (16.x has breaking changes).
- Xamarin.AndroidX.Core 1.13.1.5 — behind upstream 1.16; no security gap.
- ZXing.Net 0.16.10 — latest pure-C# port.

#### Info-2. libbox.aar has no SBOM/integrity check
**File**: `Lib/libbox.aar`

Built locally via SagerNet gomobile fork. No CI integrity check, no signature, no version manifest committed alongside. If local toolchain compromised, backdoored aar ships silently.

**Mitigation**: pin upstream sing-box commit hash + recipe in repo; rebuild in CI from same recipe and compare hashes.

### Recommended fix order

1. **Critical-1, Critical-2, High-3** — single patch: change `GetExternalFilesDir(null)` → `FilesDir` everywhere; gate external-storage behind explicit Settings toggle.
2. **High-4** — explicit `-=` on view detach + `WeakEventManager`.
3. **High-6** — dispose CTS pulses.
4. **High-1, High-2** — scrub IPs/keys in crash log + add `.sha256` to release pipeline.
5. **Medium-***, **Low-***, **Info-*** — backlog.
