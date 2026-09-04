# Phase — Transition to sing-box-vpnctl v1.14.0-vpnctl.2 (Desktop and Android)

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
2. Release `v1.14.0-vpnctl.2` provides stable upstream SagerNet v1.14.0 base + native AmneziaWG (2.0 and 3.1) + modular XHTTP + Anti-Censorship DNS + authenticated HTTP Clash API.
3. In Android, `libbox.aar` from `sing-box-vpnctl` retains the authenticated HTTP Clash API (`with_clash_api`), allowing real-time traffic and connection telemetry without breaking changes.
4. On Desktop, building from source via monkey-patching scripts (`tools/build-singbox-lx.sh` / `.ps1`) is replaced by official release archives with verified SHA256 integrity pins.
5. In Russia, TSPU blocks Cloudflare domains via TCP RST when browsers query DNS HTTPS / SVCB records (type 65 / 64) and attempt Encrypted Client Hello (ECH). Adding `{ "query_type": ["HTTPS", "SVCB"], "action": "reject" }` pre-empts ECH and forces browsers to negotiate standard TLS 1.3 without RST termination.

## What

1. **Android (`libbox.aar`)**:
   - Update `.github/workflows/build-android.yml` to download `libbox.aar` from `PavelLizunov/sing-box-vpnctl` release `v1.14.0-vpnctl.2`.
   - Update authoritative SHA256 pin (`d451446c237266e101e71f309a7610949a8bdc9fb6a5d7af455e89b9ce746998`).
2. **Desktop (Windows, macOS, Linux)**:
   - Deprecate source monkey-patching in `tools/build-singbox-lx.ps1` and `tools/build-singbox-lx.sh`.
   - In `build.ps1`, pin `1.14.0-vpnctl.2` release archives from `PavelLizunov/sing-box-vpnctl` with verified SHA256 (`58cc175a7f5accec33b922e33e50b129e1f623dc51b7b91d9f67eea5f14b34ea`).
   - In `build-mac.sh`, download and verify `sing-box-1.14.0-vpnctl.2-darwin-universal.zip` (`70aa907936c4760b88b8b263d75909a6062b161751a798f74372aff14b53c40b`).
   - In `.github/workflows/build-linux.yml`, download and verify `sing-box-1.14.0-vpnctl.2-linux-amd64.tar.gz` (`22d0018b3039a241eceb814722405caf9a3af1f5615cf9047558f0349d56ccdc`).
   - In `.github/workflows/test-windows-update.yml`, align cache key with `1.14.0-vpnctl.2`.
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
- [x] Gate 4 — Asset and SHA256 integrity: All release archive URLs and SHA256 hashes match authoritative release `v1.14.0-vpnctl.2`.
- [x] Gate 5 — WINBRAT remote verification: sing-box 1.14.0-vpnctl.2 binary passed `sing-box.exe check` on all 48 custom config injection test fixtures on WINBRAT.
- [x] Gate 6 — PR and CI: pushed to task branch `dsh/upgrade-singbox-vpnctl-1.14` and verified in CI.

## Outcome

**Status**: READY FOR OWNER REVIEW / PR
**Pushed**: `origin/dsh/upgrade-singbox-vpnctl-1.14`
**Files changed**:
- `.github/workflows/build-android.yml`: pinned `libbox.aar` from `PavelLizunov/sing-box-vpnctl` `v1.14.0-vpnctl.2` with authoritative SHA256.
- `.github/workflows/build-linux.yml`: downloads and bundles `sing-box-1.14.0-vpnctl.2-linux-amd64.tar.gz` with verified SHA256; removed unused Go setup.
- `.github/workflows/build-mac.yml`: removed unused Go setup.
- `build-mac.sh`: downloads and verifies `sing-box-1.14.0-vpnctl.2-darwin-universal.zip`.
- `build.ps1`: pins `$SingBoxVersion = "1.14.0-vpnctl.2"`, auto-downloads and verifies `sing-box-1.14.0-vpnctl.2-windows-amd64.zip`.
- `.github/workflows/test-windows-update.yml`: aligned cache key with `1.14.0-vpnctl.2`.
- `VPNRouter.Core/Models/VPNConfig.cs`: added `QueryType` to `DnsRule`.
- `VPNRouter.Core/Services/ConfigGenerator.Dns.cs`: added anti-censorship ECH suppression rule rejecting `HTTPS` and `SVCB` queries.
- `VPNRouter.Tests/AntiCensorshipDnsTests.cs`: unit tests pinning ECH suppression and 1.14 typed DNS servers.
- `packaging/windows/install.ps1`: fixed Unicode em-dash inside string literal.
- `VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs`: fixed mock startCount trigger condition.
