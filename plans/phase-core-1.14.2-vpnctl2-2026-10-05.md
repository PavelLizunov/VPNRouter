# Core update: sing-box-vpnctl v1.14.2-vpnctl.2 on desktop and Android, release 2.50.1

## Why

The owner asked to move VPNRouter to the new core (`PavelLizunov/sing-box-vpnctl` `v1.14.2-vpnctl.2`, Latest) and to release 2.50.1. Desktop bundles `v1.14.0-vpnctl.5`, Android uses the old libbox 1.13.10 (`tooling-libbox-singbox-1.13.10`, 11.7 MB, arm64 only, minSdk 23). The new release is sing-box 1.14.2 plus the fork layer (AmneziaWG 2.0 and 3.1, client XHTTP) and embedded WireGuard/AWG fixes, with a dependency bump (grpc, x/crypto). The core repository is not changed from here: a core defect is described with reproduction steps and the work stops at that item.

## Facts checked before the plan (2026-10-05)

- Release assets: sing-box archives for Windows (amd64, arm64), Linux (amd64, arm64, armv7), macOS (universal zip and tar.gz), `libbox.aar` (API 24, 119 MB, four ABIs), `libbox-legacy.aar` (API 21, no naive, 99 MB, four ABIs), `SHA256SUMS`, a `.sha256` per file. `SHA256SUMS` matches the downloaded files; `gh attestation verify` exits 0 on the Windows zip.
- Archive names keep the pattern the build scripts already use (`sing-box-<ver>-windows-amd64.zip`, `-linux-amd64.tar.gz`, `-darwin-universal.zip`), so only version and SHA-256 change. The vpnctl Windows and Linux archives carry no `libcronet`.
- The bundled `libcronet.dll` and `libcronet.so` come from upstream `SagerNet/sing-box v1.13.14`. The new core uses the `cronet-go` binding of upstream v1.14.2 (`20260912`), and upstream v1.14.2 ships its own `libcronet.dll` (sha256 `3217c626...`, different from the pinned `c7434cfa...`). The runtime library must come from upstream v1.14.2.
- Naive is not supported on Android (`SmpQrNaiveUnsupportedAndroid`), so `libbox-legacy.aar` (API 21, no naive) loses nothing and keeps minSdk 23.
- libbox API against 1.13.10: removed `RedirectStderr`, `SetLocale`, `SetMemoryLimit`, `CommandClient.URLTest`, `PlatformInterface.SystemCertificates`; `TunOptions.GetDNSServerAddress` now returns a `StringIterator`; 13 new `PlatformInterface` methods (`CancelNotification`, `CheckPlatformShell`, `CloseNeighborMonitor`, `CreateBridge`, `LookupSFTPServer`, `LookupUser`, `OpenShellSession`, `ReadSystemSSHHostKey`, `RegisterMyInterface`, `StartNeighborMonitor`, `TailscaleHostname`, `UsePlatformBridge`, `UsePlatformShell`), `TunOptions.GetDNSMode`, new `SetupOptions` fields. Both `VpnRouterService.java` and `AndroidDeepVerifyBox.java` implement `PlatformInterface` and call `Libbox.redirectStderr` / `getDNSServerAddress`.
- The AWG outbound builder already raises s1..s4 to 12 when a header protection key is set. Nothing in the generator sets an XHTTP session alphabet or HTTP version.

## What

1. Desktop pins: `build.ps1` (version, SHA-256, upstream cronet archive and DLL hashes), `build-linux.yml` (version, SHA-256, upstream cronet tarball), `build-mac.sh` (version, SHA-256), `test-windows-update.yml` cache key, packaging characterization tests that carry the version, `docs/guide/*architecture*`.
2. AWG import and generator: reject profiles the new core refuses (jc > 128, jmax > 65507, jmin > jmax, rekey_after_time 0 or above 4294967295) with a clear message instead of a start failure; tests.
3. Android: `libbox-legacy.aar` of the new release, pinned by SHA-256 and verified with `gh attestation verify` in `build-android.yml` and `android-compile.yml`; replace the `tooling-libbox-singbox-1.13.10` download; the two Java `PlatformInterface` implementations; `redirectStderr` replacement; `.github/SECRETS.md`; release APK limited to `arm64-v8a` if the size demands it (measure first).
4. Version `2.50.1-r1` (candidate), then verification, then stable `2.50.1` only on the owner's word.

## Android scope estimate

Medium. About 250 to 350 changed lines in two Java files, four pins, one doc, no C# behavior change expected. Main risks: libbox API semantics that the compiler cannot check (DNS iterator, setup options, crash log path), APK size (libbox.so is 70 MB per ABI uncompressed against 11.7 MB for the whole old aar), and no real device.

## Not covered

An Android device, real Windows/macOS machines other than WINBRAT, a real Xray server, an official AmneziaWG peer. WINBRAT has Hysteria2, VLESS, AWG and AWG3 test servers in its subscription, so those are exercised through Deep verify and connects.

## Verification

Release solution build (0 errors, warning count compared with 229), focused tests and the full suite on WINBRAT, Android Release APK build on WINBRAT and install on the emulator of the Linux worker (x86_64 libbox is present now, so a real tunnel on the emulator is possible), live scenarios on the installed candidate, post-ship, static checks of macOS and Linux packages, an independent read of the diff by another model family, exact-head CI.

## Rollback

Revert the PR. A published candidate or stable is never replaced; a fix is a new version.

## Outcome

Pending.
