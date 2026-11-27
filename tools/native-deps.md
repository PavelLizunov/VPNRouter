# Native runtime dependencies

VPNRouter runs several third-party native components. They are not NuGet packages and are not covered by Dependabot; they are source-built or downloaded artifacts pinned in code and CI. This file is the inventory and bump procedure. For attribution and licenses, see `NOTICE.md`.

## Inventory

| Component | Shipped or local-only pin | Pinned where | Bump procedure |
|---|---|---|---|
| sing-box-lx (local rebuild helpers) | label `1.13.13-lx-awg`; LX commit `c7a2592e750406ade9ebaae1d0fdb7482fc0773e`; AmneziaWG runtime commit `0c0c10b5d3236796bd3832a6813223d6dc7d0bb1` | `tools/build-singbox-lx.ps1`, `tools/build-singbox-lx.sh` | pin immutable source commits, rebuild all desktop targets, prove required tags and AWG config checks, then update this inventory atomically |
| upstream sing-box desktop fallback | SagerNet `1.13.14`; historical local fallback | `build.ps1` (`SingBoxVersion`) | bump only after generated-config and packaging checks; current release packaging uses the sing-box-vpnctl pin below |
| sing-box-vpnctl core release (desktop shipment) | `PavelLizunov/sing-box-vpnctl` `v1.14.5-vpnctl.1`; archive SHA256 from the release's `SHA256SUMS` | `build.ps1` (`SingBoxVersion`, `SingBoxSha256`), `build-mac.sh` (`SB_DARWIN_VER`, `SB_DARWIN_SHA256`), `.github/workflows/build-linux.yml` (`SINGBOX_VPNCTL_VER`, `SINGBOX_VPNCTL_SHA256`), `test-windows-update.yml` cache key | take the new release's `SHA256SUMS`, run `gh attestation verify` on the archives, bump the pins together, then run the Windows update test and the packaging tests |
| libbox.aar (Android shipment) | `libbox-legacy-1.14.5-vpnctl.1.aar` (API 21, no naive) from the same core release; SHA256 `0db1d467356c67df353b5826e48c09a0ca669576135e605b7dfe5ff4cb9cbf99` | `.github/workflows/build-android.yml` and `android-compile.yml` (`LIBBOX_SOURCE_REPO`, `LIBBOX_RELEASE_TAG`, `LIBBOX_ASSET`, `LIBBOX_AAR_SHA256`) | bump tag, asset and SHA256 together in both workflows; the workflows also run `gh attestation verify`; older tags keep using `tooling-libbox-singbox-1.13.10` |
| Zapret / winws | latest Flowseal release, fetched on demand | `ZapretUpdater` | upstream auto-resolved at runtime |
| Telegram proxy (tg-ws-proxy) | Flowseal v1.10.0, CPython 3.14.10 embeddable x64, cffi 2.1.1 (cp314), cryptography 50.0.2 (cp311-abi3), certifi 2026.11.20 and pycparser 3.0 | `TgProxyUpdater` | pin the source archive, interpreter and exact wheel filenames by SHA256 and verify fresh and upgraded installations on Windows |

## Local sing-box-lx build helpers

Desktop LX builds currently require:

```text
with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,
with_clash_api,with_naive_outbound,with_purego,badlinkname,
tfogo_checklinkname0,with_xhttp,with_awg
```

The build helpers fail closed unless build metadata reports `with_awg` and `with_xhttp`; the Windows helper also checks a minimal AWG endpoint. The shipped desktop and Android cores use the sing-box-vpnctl release pins
in the inventory. Both include TrustTunnel and Mieru. The release build profile
excludes NaiveProxy outbound and Cronet.

## Trust roots

Several components fetch or run executable code under the user's account, so integrity posture matters as much as versioning:

- **Desktop sing-box-lx** is built from immutable LX and AmneziaWG commits. Build metadata and config checks prove feature tags; release artifacts still require the normal release checksum and platform gates.
- **libbox.aar** has a hard SHA256 CI gate and a build attestation check against the `sing-box-vpnctl` repository. Keep `LIBBOX_RELEASE_TAG`, `LIBBOX_ASSET` and `LIBBOX_AAR_SHA256` in lockstep; a mismatch is a hard failure.
- **tg-ws-proxy, Python, and wheels** fail closed on SHA256 at install time. The client pins the GitHub source archive, python.org interpreter archive and each wheel digest. PyPI metadata must also match the pinned wheel name and digest.
- **Zapret / winws** checks the GitHub release asset size and SHA256 before extraction and validates the required PE and driver files. The resolved release still depends on the upstream account. Record its tag, archive digest and installed winws.exe digest during acceptance.

## 1.14 readiness

No pin changes here authorize a migration. The evidence and mandatory desktop, Android, AWG 3.1, and DNS gates are recorded in `git show 6491be4c:plans/sing-box-lx-1.14-readiness-2026-09-01.md`.

sing-box and libbox are GPL-3.0; Zapret and tg-ws-proxy carry their own upstream licenses (see `NOTICE.md`).
