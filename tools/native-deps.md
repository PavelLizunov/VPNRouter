# Native runtime dependencies

VPNRouter runs several third-party native components. They are not NuGet packages and are not covered by Dependabot; they are source-built or downloaded artifacts pinned in code and CI. This file is the inventory and bump procedure. For attribution and licenses, see `NOTICE.md`.

## Inventory

| Component | Shipped or local-only pin | Pinned where | Bump procedure |
|---|---|---|---|
| sing-box-lx (desktop release core) | label `1.13.13-lx-awg`; LX commit `c7a2592e750406ade9ebaae1d0fdb7482fc0773e`; AmneziaWG runtime commit `0c0c10b5d3236796bd3832a6813223d6dc7d0bb1` | `tools/build-singbox-lx.ps1`, `tools/build-singbox-lx.sh` | pin immutable source commits, rebuild all desktop targets, prove required tags and AWG config checks, then update this inventory atomically |
| upstream sing-box desktop fallback | SagerNet `1.13.14`; non-upload local builds only when no LX binary is supplied | `build.ps1` (`SingBoxVersion`) | bump only after generated-config and packaging checks; release uploads must still provide the LX binary |
| sing-box-vpnctl core release (desktop shipment) | `PavelLizunov/sing-box-vpnctl` `v1.14.4-vpnctl.3`; archive SHA256 from the release's `SHA256SUMS` | `build.ps1` (`SingBoxVersion`, `SingBoxSha256`), `build-mac.sh` (`SB_DARWIN_VER`, `SB_DARWIN_SHA256`), `.github/workflows/build-linux.yml` (`SINGBOX_VPNCTL_VER`, `SINGBOX_VPNCTL_SHA256`), `test-windows-update.yml` cache key | take the new release's `SHA256SUMS`, run `gh attestation verify` on the archives, bump the pins together, then run the Windows update test and the packaging tests |
| libcronet (desktop naive runtime) | Windows x64 DLL and Linux x64 SO from the core's exact `SagerNet/cronet-go` library revision `4ecba4761b201f6c552aabd0462a94fe2a8f833c` | `build.ps1`, `.github/workflows/build-linux.yml`, `.github/workflows/test-windows-update.yml`, `packaging/arch/build_package.py` | read the library revision from the core's build metadata, pin each library's SHA256 and test native config validation with Naive |
| libbox.aar (Android shipment) | `libbox-legacy-1.14.4-vpnctl.2.aar` (API 21, no naive) from `v1.14.4-vpnctl.2`; SHA256 `4a058525f0235f4b3791ff636773911dc86b9069fa1fc898772f97392e0378a4` | `.github/workflows/build-android.yml` and `android-compile.yml` (`LIBBOX_SOURCE_REPO`, `LIBBOX_RELEASE_TAG`, `LIBBOX_ASSET`, `LIBBOX_AAR_SHA256`) | bump tag, asset and SHA256 together in both workflows; the workflows also run `gh attestation verify`; older tags keep using `tooling-libbox-singbox-1.13.10` |
| Zapret / winws | latest Flowseal release, fetched on demand | `ZapretUpdater` | upstream auto-resolved at runtime |
| Telegram proxy (tg-ws-proxy) | latest upstream release plus Python 3.12.7 embed and pinned PyPI wheel digests | `TgProxyUpdater` | upstream auto-resolved; bump Python only with its canonical SHA256 and retain authoritative wheel digests |

## Desktop sing-box-lx build contract

Desktop LX builds currently require:

```text
with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,
with_clash_api,with_naive_outbound,with_purego,badlinkname,
tfogo_checklinkname0,with_xhttp,with_awg
```

The build helpers fail closed unless build metadata reports `with_awg` and `with_xhttp`; the Windows helper also checks a minimal AWG endpoint. Release packaging rejects an upstream fallback and requires an LX binary carrying the required tags. The Android helper is a rebuild tool, not the shipped AAR pin; the workflow's immutable tooling tag and SHA256 are authoritative for Android.

## Trust roots

Several components fetch or run executable code under the user's account, so integrity posture matters as much as versioning:

- **Desktop sing-box-lx** is built from immutable LX and AmneziaWG commits. Build metadata and config checks prove feature tags; release artifacts still require the normal release checksum and platform gates.
- **libbox.aar** has a hard SHA256 CI gate and a build attestation check against the `sing-box-vpnctl` repository. Keep `LIBBOX_RELEASE_TAG`, `LIBBOX_ASSET` and `LIBBOX_AAR_SHA256` in lockstep; a mismatch is a hard failure.
- **tg-ws-proxy, Python, and wheels** fail closed on SHA256 at install time. Trust roots are python.org's canonical digest and PyPI's published `urls[].digests.sha256` values.
- **Zapret / winws** uses download-size verification only. Its trust root is the upstream GitHub account plus TLS to GitHub. A compromised upstream account can therefore ship an administrative payload; the accepted hardening option remains a known-good tag pin and explicit upgrade confirmation (tracked in `plans/OPEN-DEFECTS.md`).

## 1.14 readiness

No pin changes here authorize a migration. The evidence and mandatory desktop, Android, AWG 3.1, and DNS gates are recorded in `git show 6491be4c:plans/sing-box-lx-1.14-readiness-2026-09-01.md`.

sing-box and libbox are GPL-3.0; Zapret and tg-ws-proxy carry their own upstream licenses (see `NOTICE.md`).
