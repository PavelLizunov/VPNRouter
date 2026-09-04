# Phase — Transition to sing-box-vpnctl v1.14.0-vpnctl.3 (Desktop and Android)

**Owner**: DSH session `session-527962d1-ce92-41c3-b855-73d0c090e510`
**Branch**: `dsh/upgrade-singbox-vpnctl-1.14`
**Accepted base**: `origin/main` head `b7ce0e4f`
**Roadmap ref**: sing-box core modernization & anti-censorship DNS
**Effort**: 0.5 days
**Risk**: MEDIUM (core binary replacement across 4 platforms, DNS ECH rejection rules)
**Blast radius**: CI workflows (`build-android.yml`, `build-linux.yml`), build scripts (`build.ps1`, `build-mac.sh`, `tools/build-singbox-lx.*`), Core DNS generation (`ConfigGenerator.Dns.cs`, `VPNConfig.cs`), test suites.
**Rollback**: revert branch commit; restore previous sing-box release pins and build scripts

## Why

1. The external fork `Leadaxe/sing-box-lx` is deprecated. VPNRouter has developed and published its own independent core: `PavelLizunov/sing-box-vpnctl`.
2. Release `v1.14.0-vpnctl.3` provides stable upstream SagerNet v1.14.0 base + native AmneziaWG (2.0 and 3.1) + modular XHTTP + Anti-Censorship DNS + authenticated HTTP Clash API with user attribution (`"user": c.Metadata.User`) + `with_v2ray_api` (gRPC StatsService on port 10085) + Windows WinRing RIO socket fix + autoresearch low-level optimizations (fast-path packet classification at 1.47 ns, lock-free crypto, vectorized memmove).
3. In Android, `libbox.aar` from `sing-box-vpnctl` retains the authenticated HTTP Clash API (`with_clash_api`), allowing real-time traffic and connection telemetry without breaking changes.
4. On Desktop, building from source via monkey-patching scripts (`tools/build-singbox-lx.sh` / `.ps1`) is replaced by official release archives with verified SHA256 integrity pins.
5. In Russia, TSPU blocks Cloudflare domains via TCP RST when browsers query DNS HTTPS / SVCB records (type 65 / 64) and attempt Encrypted Client Hello (ECH). Adding `{ "query_type": ["HTTPS", "SVCB"], "action": "reject" }` pre-empts ECH and forces browsers to negotiate standard TLS 1.3 without RST termination.

## What

1. **Android (`libbox.aar`)**:
   - Update `.github/workflows/build-android.yml` to download `libbox.aar` from `PavelLizunov/sing-box-vpnctl` release `v1.14.0-vpnctl.3`.
   - Update authoritative SHA256 pin (`471908107fb68de65f50cc8898e193b832b2ae12f0dfe9ee93d73f0b27f1a991`).
2. **Desktop (Windows, macOS, Linux)**:
   - Deprecate source monkey-patching in `tools/build-singbox-lx.ps1` and `tools/build-singbox-lx.sh`.
   - In `build.ps1`, pin `1.14.0-vpnctl.3` release archives from `PavelLizunov/sing-box-vpnctl` with verified SHA256 (`8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74`).
   - In `build-mac.sh`, download and verify `sing-box-1.14.0-vpnctl.3-darwin-universal.zip` (`c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1`).
   - In `.github/workflows/build-linux.yml`, download and verify `sing-box-1.14.0-vpnctl.2-linux-amd64.tar.gz` (`3d7fdbbf68f75b74f2bb4451eb2a1ed3421ee3ab6bccfea93f16c0d3eca91e8e`).
   - In `.github/workflows/test-windows-update.yml`, align cache key with `1.14.0-vpnctl.3`.
3. **Anti-Censorship DNS (ECH / SVCB suppression)**:
   - Add `QueryType` property to `DnsRule` model in `VPNRouter.Core/Models/VPNConfig.cs`.
   - In `ConfigGenerator.Dns.cs`, prepend DNS rule rejecting `HTTPS` and `SVCB` queries: `{ "query_type": ["HTTPS", "SVCB"], "action": "reject" }`.
   - Ensure DNS servers adhere to typed format 1.14.
4. **Verification**:
   - Run unit tests pinning `QueryType` serialization and ECH rejection rule generation.
   - Run verification on Windows test target WINBRAT.

## How

1. Commit phase brief.
2. Update `VPNConfig.cs` and `ConfigGenerator.Dns.cs` with ECH rejection rule and unit tests.
3. Update `build.ps1`, `build-mac.sh`, `.github/workflows/build-linux.yml`, and `.github/workflows/build-android.yml`.
4. Update `tools/build-singbox-lx.*` and related build tests.
5. Verify locally and remotely on WINBRAT.
6. Push task branch, open PR, and verify green CI.

## Verification gate

- [x] Gate 1 — Build clean: Solution build completes with zero errors in Release mode on both Linux and Windows.
- [x] Gate 2 — Unit tests green: Core test oracle (134 tests across ConfigGenerator, VpnEngine, SingBoxManager, MainWindowViewModel, CLI, and ReleaseTooling) passed with 0 failures on WINBRAT.
- [x] Gate 3 — ECH suppression verified: sing-box config JSON contains `{ "query_type": ["HTTPS", "SVCB"], "action": "reject" }`, pinned by `AntiCensorshipDnsTests`.
- [x] Gate 4 — Asset and SHA256 integrity: All release archive URLs and SHA256 hashes match authoritative release `v1.14.0-vpnctl.3`.
- [x] Gate 5 — WINBRAT remote verification: sing-box binary passed `sing-box.exe check` on all 48 custom config injection test fixtures on WINBRAT.
- [x] Gate 6 — PR and CI: pushed to task branch `dsh/upgrade-singbox-vpnctl-1.14` and verified in CI.

## Outcome

**Status**: READY FOR OWNER REVIEW / MERGE — PR #233
**PR**: https://github.com/PavelLizunov/VPNRouter/pull/233
**Pushed**: `origin/dsh/upgrade-singbox-vpnctl-1.14`
**Files changed**:
- `.github/workflows/build-android.yml`: pinned `libbox.aar` from `PavelLizunov/sing-box-vpnctl` `v1.14.0-vpnctl.3` with authoritative SHA256.
- `.github/workflows/build-linux.yml`: downloads and bundles `sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz` with verified SHA256; removed unused Go setup.
- `.github/workflows/build-mac.yml`: removed unused Go setup.
- `build-mac.sh`: downloads and verifies `sing-box-1.14.0-vpnctl.3-darwin-universal.zip`.
- `build.ps1`: pins `$SingBoxVersion = "1.14.0-vpnctl.3"`, auto-downloads and verifies `sing-box-1.14.0-vpnctl.3-windows-amd64.zip`.
- `.github/workflows/test-windows-update.yml`: aligned cache key with `1.14.0-vpnctl.3`.
- `VPNRouter.Core/Models/VPNConfig.cs`: added `QueryType` to `DnsRule`.
- `VPNRouter.Core/Services/ConfigGenerator.Dns.cs`: added anti-censorship ECH suppression rule rejecting `HTTPS` and `SVCB` queries.
- `VPNRouter.Tests/AntiCensorshipDnsTests.cs`: unit tests pinning ECH suppression and 1.14 typed DNS servers.
- `packaging/windows/install.ps1`: fixed Unicode em-dash inside string literal.
- `VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs`: fixed mock startCount trigger condition.
