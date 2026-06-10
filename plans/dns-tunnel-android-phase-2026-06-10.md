# DNS-tunnel (slipstream) on Android — implementation plan

**Created:** 2026-06-10 · **Status:** plan (desktop MVP shipped as v2.42.0-r1; Android deferred phase)

How to bring the DNS-tunnel transport to the Android port. Desktop ships
slipstream-client.exe as a sidecar process; Android can't use that model.
Workflow-researched (5 parallel readers over the Android architecture + slipstream
Android-build feasibility + loop-avoidance).

## Verdict: FFI library in-process, NOT a standalone binary

Build slipstream as a **JNI FFI library** (`libslipstream_jni.so` per ABI) and run
it **in-process** inside `VpnRouterService` — do NOT ship a standalone
slipstream-client binary in `nativeLibraryDir`. Three reasons:

1. **Android W^X.** Android 10+ forbids exec from the writable data dir; you can
   only exec from `nativeLibraryDir` as `lib*.so`. The whole `VPNRouter.Android`
   codebase has **zero** `ProcessBuilder`/`Runtime.exec` — everything native runs
   in-process via the libbox JNI. An exec'd binary fights both the OS hardening
   trend and the established app architecture.
2. **Same UID either way, but in-process is cleaner.** A spawned child IS the same
   UID (so it would inherit loop-avoidance — see below), but in-process means no PID
   to babysit across Doze / swipe-away, and no 6.7 MB exec'able to package + extract.
3. **The Rust side is already library-shaped.** `crates/slipstream-ffi` exposes
   `ClientConfig` / `ResolverSpec` / `configure_quic` + the picoquic FFI bindings;
   `slipstream-client/main.rs` is a thin CLI wrapper over it. libbox already proves
   the in-process native model on this exact service — slipstream slots in beside it.

## The biggest Android risk is ALREADY SOLVED (DNS loop-avoidance)

The slipstream sidecar's UDP:53 queries to the НСДИ resolvers must NOT loop back
through sing-box's TUN. On Android this is solved for free:
`VpnRouterService.java` calls `builder.addDisallowedApplication(getPackageName())`
**unconditionally in all three routing modes** (include / exclude / full —
lines ~817, 823), with the explicit comment "so VpnRouter's own traffic doesn't
loop through its own TUN." The kernel installs a netfilter rule that bypasses the
TUN for ALL traffic from the VPNRouter UID, **including in-process / same-UID
sidecar sockets.** So in-process slipstream's resolver queries exit the physical
interface, never tun0. **No new loop-avoidance code is needed** — this was the
single biggest Android-specific worry and it's a non-issue. (Desktop uses firewall
rules for the same goal because Windows has no VPN-package-UID interception.)

## UPDATE 2026-06-10 — Phase A is LESS work than the research feared

Reading the actual `slipstream-ffi/build.rs` + `build/openssl.rs` + `build_picoquic.sh`
corrected the plan's pessimism:
- **OpenSSL-for-Android is NOT a manual blocker.** `slipstream-ffi` has an
  `openssl-vendored` feature (`openssl-sys/vendored`) — `openssl-sys` cross-compiles
  OpenSSL from source using the NDK clang (wired by cargo-ndk) automatically. No
  hand-built per-ABI `libssl.a`.
- **picoquic auto-builds for Android.** `build.rs` (line 111-119): for non-Windows
  targets, `PICOQUIC_AUTO_BUILD` (default true) invokes `scripts/build_picoquic.sh`,
  which already has a full Android path (`ANDROID_NDK_HOME` →
  `build/cmake/android.toolchain.cmake`, `-DPTLS_WITH_FUSION=OFF`, `ANDROID_ABI`,
  `ANDROID_PLATFORM`). Uses cmake's default Makefiles generator → CLT `make`, no ninja.
- **The real constraint was just: must build on a Unix host.** `picoquic.rs` does
  `Command::new("build_picoquic.sh")` (bash) + openssl-vendored wants perl/make — a
  Windows host can't (Rust can't exec a `.sh`; WSL not installed on the VM). 
- **Host SOLVED: the Mac build host** (`mm4.local`, macOS 15.5, **Apple Silicon
  arm64**) — reachable from the VM via SSH (`slovn@192.168.0.246`, id_ed25519). Ideal
  Android NDK cross-compile host.

**Build recipe (Mac, no brew/sudo, all under `~`):**
1. rustup + `aarch64-linux-android` target + `cargo-ndk` (DONE).
2. cmake 3.31.6 (Kitware tar.gz → `~/toolchains/cmake`) + Android NDK r27c (.dmg →
   `~/toolchains/android-ndk-r27c`) + clone `Mygod/slipstream-rust --recursive`.
3. `ANDROID_NDK_HOME=~/toolchains/android-ndk-r27c ANDROID_ABI=arm64-v8a
   ANDROID_PLATFORM=android-24 cargo ndk -t arm64-v8a -p 24 build --release
   -p slipstream-ffi --features openssl-vendored` (openssl-vendored + picoquic
   auto-build do the rest). Then the Phase-B JNI crate → `libslipstream_jni.so`.

**Revised effort:** Phase A drops from HIGH/HIGH to MEDIUM — the toolchain is the
work, not bespoke cross-compile scripting. Most of the lift is download + first-build
validation, not authoring build logic.

### Phase A cross-compile progress (2026-06-10) — RESUME STATE

Mac toolchain stood up; arm64 cross-compile iterated through real blockers. Build
scripts on the VM at `D:\build\mac_android_stage{1,2,3}.sh` (scp to Mac + run).
Blockers cleared in `mac_android_stage3.sh`:
1. cargo-ndk API-level flag is `-P` (capital), not `-p` (collided with cargo `-p package`).
2. `build_picoquic.sh:25` bash-4 `${VAR,,}` → `sed` it out (macOS bash 3.2; cargo sets the var to `1`).
3. picotls `find_package(PkgConfig)` REQUIRED (optional brotli) → stub `pkg-config` on PATH
   (`--version`→0.29.2, everything else exit 1; reproduces the no-brotli Windows build).
4. **picoquic `FindOpenSSL` couldn't find `libcrypto.a`** even with `OPENSSL_ROOT_DIR` set —
   the NDK android.toolchain sets `CMAKE_FIND_ROOT_PATH_MODE_LIBRARY=ONLY`, so `find_library`
   only searches the sysroot, not the openssl-vendored output. **Fix (staged): two-pass build**
   — pass 1 compiles openssl-vendored (picoquic fails), then discover the real
   `…/openssl-sys-*/out/openssl-build/install/lib/libcrypto.a` and re-run exporting
   `OPENSSL_CRYPTO_LIBRARY`/`OPENSSL_SSL_LIBRARY`/`OPENSSL_ROOT_DIR`/`OPENSSL_INCLUDE_DIR` as
   full paths (build_picoquic.sh forwards them to cmake as `-D…`, bypassing find_library).

Confirmed working before the blocker: openssl-vendored compiled OpenSSL 3.6.2 for
arm64-android + all rust deps (tokio/mio/socket2) compiled. So the chain is sound; only the
cmake↔openssl lib handoff needed the explicit-path fix.

**Mac build host went offline mid-iteration (AmneziaWG route flap — intermittent).** To
resume when reachable (`Test-NetConnection 192.168.0.246 -Port 22`):
`scp mac_android_stage3.sh slovn@192.168.0.246:~/ ; ssh … 'nohup bash ~/mac_android_stage3.sh > ~/stage3.log 2>&1 &'`
then poll `~/stage3.log` for `STAGE3_OK` + the `libpicoquic*.a` / `.a` artifacts. If green →
Phase A proven (arm64 native chain cross-compiles) → proceed to Phase B (JNI crate).

### Phase A + B COMPLETE (2026-06-10) — arm64 `.so` BUILT

**Phase A PROVEN.** Two-pass `mac_android_stage3.sh` → `STAGE3_OK`, `CARGO_EXIT=0`,
`libpicoquic-core.a` cross-compiled for aarch64-linux-android (OpenSSL 3.6.2 vendored +
picoquic + picotls, 19s). The whole native chain cross-compiles.

**Phase B core BUILT.** New crates authored + compiled on the Mac (`mac_android_stage4.sh`):
- `crates/slipstream-client/src/lib.rs` — lib facade (`pub mod` + `pub use run_client`) so
  the JNI crate reuses the tunnel loop without the CLI (bin unchanged).
- `crates/slipstream-android/` — cdylib JNI crate. `nativeStart(cert,domain,port,resolvers[])`
  marshals JNI → `ResolverSpec[]` (Recursive) + `ClientConfig` → tokio current-thread runtime on
  a worker thread, `tokio::select!{ run_client, shutdown.notified() }`; `nativeStop` fires the
  Notify + joins (run_client has no cancel param → drop-the-runtime cancellation). Bind
  `127.0.0.1` (not `::1`). PEM passed as string (no on-disk active-cert on Android).
- Root `Cargo.toml` += `crates/slipstream-android` member.
- **Result: `libslipstream_jni.so` (5.1 MB)** for arm64, exports
  `Java_com_ninitux_vpnrouter_SlipstreamNative_native{Start,Stop}`, NEEDED = only `libc.so` +
  `libdl.so` (zero surprise deps), SHA256 `33ba55df…664c4c`. Pulled to VM
  `tools/slipstream-android-cache/arm64-v8a/` (gitignored).

**VPNRouter.Android side started:**
- `SlipstreamNative.java` — `isAvailable()` dlopen-probe (fail-closed) + `nativeStart/nativeStop`.
- `.csproj` — `<AndroidJavaSource Include="SlipstreamNative.java"/>` + Exists-guarded
  `<AndroidNativeLibrary … arm64-v8a>` (bundled like libbox, local-only).

**REMAINING (Phases D-E + build/test):**
- **D — VpnRouterService lifecycle**: on dns-tunnel server, `SlipstreamNative.nativeStart` →
  poll `127.0.0.1:7001` (~5s, fail-closed) → only then `libbox.start`; on stop libbox then
  `nativeStop`. Plumb domain/resolvers/cert from `AndroidConfigBuilder` to the service.
- **E — gate flip**: `AndroidApp` init sets `ServerUriParser.SlipstreamRuntimeAvailable =
  SlipstreamNative.isAvailable()` (the gate is `internal set`); server-type badge parity.
- **Build + on-device**: build the APK (needs libbox.aar local + Android workload), deploy to
  the connected phone, packet-capture confirm DNS-to-resolvers exits wlan0/cellular (not tun0).
- Later: armv7 + x86_64 ABIs (re-run stage3/4 with `-t armeabi-v7a` / `-t x86_64`).

Build scripts: `D:\build\mac_android_stage{3,4}.sh`. The Mac toolchain (NDK r27c, cmake,
rust+cargo-ndk, slipstream-rust clone) persists for fast re-builds.

## slipstream-rust is ~70-80% Android-ready already

- `crates/slipstream-ffi/build/android.rs` exists — detects android targets, maps
  ABIs to `clang_rt.builtins` variants.
- `scripts/build_picoquic.sh` already accepts `ANDROID_NDK_HOME / ANDROID_ABI /
  ANDROID_PLATFORM` and configures the CMake toolchain + disables FUSION crypto
  (`-DPTLS_WITH_FUSION=OFF`) for Android. (The Windows `.ps1` has no Android path.)
- `slipstream-core` / `slipstream-dns` are pure Rust, compile unchanged.
- The unscripted blocker: **OpenSSL-for-Android** (static `libssl.a`/`libcrypto.a`
  per ABI), and the picoquic+NDK CMake path is present but CI-untested.

## What carries over from desktop slices 1-5 (unchanged)

Android source-links `VPNRouter.Core/**/*.cs` directly and the dns-tunnel surface is
gated behind strict `Protocol=="dns-tunnel"`, so Core carries over as-is:
- `ServerUriParser.ParseDnsTunnel` — short-key production schema `{cert,d,fp,r,uuid,v}`
  + long-key fallback, `NormalizeHex`, leaf-PEM-in-profile cert model. 27 parser
  tests are platform-agnostic.
- `VlessServerEntry.{DnsDomain,DnsResolvers,DnsLeafCertPem,DnsLeafFingerprint}`.
- `ConfigGenerator` dns-tunnel branch → plain VLESS to `127.0.0.1:7001`, no
  TLS/flow/transport. **`AndroidConfigBuilder.BuildConfigJson` already feeds this
  exact loopback front to libbox** — identical on both platforms.
- `LeakProtection` no-TLS-local awareness (Android runs it warn-only).
- Fingerprint cross-check + `ComputeLeafSha256Hex`.

Does NOT carry over: `SlipstreamManager`'s process-spawn machinery (IProcessRunner,
TcpListener pre-flight, SuppressExitedEvent, EnsureBinaryProvisioned, netstat
owner-hint) → replaced by a thin JNI start/stop + a Java socket-listening probe.
`VpnEngine.StartDnsTunnelTransport` lifecycle coupling → Android doesn't use
VpnEngine; re-implement the fail-closed ordering in `VpnRouterService.java`.

## Phases

| Phase | Work | Effort / Risk |
|---|---|---|
| **A — cross-compile `.so` for ABIs** (long pole) | NDK toolchain on D:. OpenSSL-for-Android static per ABI (the blocker). Validate `build_picoquic.sh` Android path (aarch64 first). cargo-ndk drives the new crate. Force-static (no missing-dep bug, same as Windows lesson). Record recipe + SHA per ABI. | **HIGH / HIGH** |
| **B — `slipstream-android` JNI crate** (~200-400 LOC Rust) | `nativeStart(cert,domain,port,resolvers[])` / `nativeStop()` → `ResolverSpec[]` (Recursive) + `ClientConfig` → existing tokio runtime on a thread + shutdown channel. android_logger → logcat. Bind `127.0.0.1` (NOT `::1` — IPv6-less devices). PEM passed as string (no on-disk active-cert on Android). | MED / LOW |
| **C — package + Java binding** | Per-ABI `.so` into APK (`AndroidNativeLibrary` `%(Abi)` / jniLibs). Tiny `SlipstreamNative.java` (`System.loadLibrary` + native decls) via existing `<AndroidJavaSource>`. Bundle from `tools/slipstream-android-cache/` (NOT on-demand — same circular-dep reasoning as Windows). No new permissions. | LOW |
| **D — lifecycle wiring** | In `VpnRouterService` start path, when protocol==dns-tunnel: `nativeStart` → poll `127.0.0.1:7001` (Java Socket, ~5s) → fail-closed (broadcast error + stopSelf if dead) → only then `libbox.start`. On stop: libbox first, then `nativeStop`. Plumb domain/resolvers/cert from `AndroidConfigBuilder` to the service. Loop-avoidance already handled (self-disallow). Defensive test that `getPackageName()` is never in the include list. | MED |
| **E — flip gates + UI + tests** | `SlipstreamRuntimeAvailable` + `IsSupportedScheme` true on Android — **as a runtime dlopen-capability probe** (fail-closed if the `.so` is absent), not a blind flip. Server-type badge parity. Update CLAUDE.md + this doc + build provenance. Parser/config/leak tests already platform-agnostic; add Android-intake-accepts test; re-pin AndroidApp characterization if `SlipstreamNative.java` lands in the hashed surface. | LOW |

Overall: feasible, ~3-4 weeks dominated by Phase A toolchain plumbing. NO
architectural redesign of picoquic / the protocol / the Core integration. Stays
inert behind `Protocol=="dns-tunnel"` → cannot regress existing Android servers.

## Open questions (need user)

1. **ABI matrix**: arm64-v8a only first (de-risk Phase A, ~all modern phones,
   lighter APK) and add armeabi-v7a + x86_64 later, OR all three up front?
2. **CI vs local-only build**: libbox.aar already can't build in CI (NU1102,
   local-only). Accept the same for `libslipstream_jni.so` (provision from
   `tools/slipstream-android-cache/`), or invest in a runner that cross-compiles it?
   Ties to open Task #140 (Android CI unblock).
3. **Gate semantics**: runtime dlopen-capability probe (recommended — fail-closed
   when the `.so` is missing) vs unconditional flip (FATALs at start if absent).
4. **APK size**: bundle-in-APK adds ~2-6 MB per bundled ABI. OK vs on-demand fetch
   (which defeats the "needed when network is hostile" point)?
5. **DnsLeakLockdown**: confirm via on-device packet capture that nothing blocks
   UDP:53 from the excluded UID to the resolvers (expected clean — UID is TUN-excluded).

---

## Status 2026-06-10 EOD — Phase D/E DONE, native fix landed, on-device library-load PROVEN

**Code (committed this session):**
- `VpnRouterService.java` — `EXTRA_DNS_TUNNEL_{DOMAIN,RESOLVERS,CERT,PORT}` + pending fields;
  `startSlipstreamIfNeeded()` (before `startLibboxService()`): `isAvailable()` →
  `nativeStart(cert,domain,7001,resolvers)` → `waitForLocalPort(7001, 8s)` FAIL-CLOSED;
  `stopSlipstreamIfRunning()` in `stopTunnel()` after libbox; persist/load of the 4 params
  for Always-on / swipe recovery.
- `MainActivity.cs` — `ExtraDnsTunnel*` consts + `DispatchTunnelStart(configJson, dnsTunnelEntry)`
  forwards params when `Protocol=="dns-tunnel"` (subscription path passes `entry`; custom path null).
- `AndroidApp.axaml.cs` — `OnFrameworkInitializationCompleted` flips
  `ServerUriParser.SlipstreamRuntimeAvailable` via `JavaSystem.LoadLibrary("slipstream_jni")`.
- Config-gen needed NO change: `AndroidConfigBuilder` → `ConfigGenerator.BuildDnsTunnelOutbound`
  already emits the 127.0.0.1:7001 VLESS front.

**Native fix (the real blocker — NOT our wiring):** first APK deploy dlopen-failed
`cannot locate symbol slipstream_mixed_cc_algorithm`. Root cause: `slipstream-ffi/build/cc.rs
resolve_cc()` for android reads only `RUST_ANDROID_GRADLE_CC`→`CC`→`"cc"`; cargo-ndk sets the
target-scoped `CC_aarch64-linux-android` (not plain CC), so the CC shims compiled with the host
macOS clang → Mach-O objects (`_`-prefixed symbols) → lld skipped them → undefined dynamic import
that `-shared` permits. FIX: `mac_android_stage5.sh` exports `RUST_ANDROID_GRADLE_CC` = the NDK
per-API wrapper `aarch64-linux-android24-clang` (resolve_cc reads it first; cargo-ndk never touches
it; plain CC does NOT work — cargo-ndk overrides it with bare clang → no sysroot). New .so SHA
`630ab75f…`, nm: zero undefined `slipstream_*`, NEEDED only libc+libdl.

**On-device proof (phone 54499112209, via Mac adb):** `nativeloader: Load …libslipstream_jni.so
… : ok` + `dns-tunnel: native Slipstream library available — dns-tunnel:// enabled`.

**REMAINING (blocked on phone PIN lock):** full connect data-plane test — add the real `#main-brat`
link → Connect → VPN consent → logcat `Slipstream front is listening on 127.0.0.1:7001` → libbox →
TUNNEL_UP. Secured keyguard (PIN keypad) blocks the UI + consent dialog; needs the phone unlocked.
Also TODO: armv7/x86_64 ABIs (re-run stage3-5 with `-t armeabi-v7a` / `-t x86_64`); APK manifest
versionName bump (stale 2.38.2 vs internal 2.42.0-r1).
