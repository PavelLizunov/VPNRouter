# Native runtime dependencies

VPNRouter runs several third-party native components. They are NOT NuGet
packages and are NOT covered by Dependabot — they are downloaded artifacts (or
bundled binaries) pinned in code/CI. This file is the inventory + bump
procedure. For attribution / licenses see `NOTICE.md`.

## Inventory

| Component | Version / pin | Pinned where | Bump procedure |
|---|---|---|---|
| sing-box | upstream 1.13.10 (desktop) | sing-box updater version + `tools/build-libbox-aar.sh` (`SING_BOX_VERSION`) | bump version, re-test config generation + `sing-box check`, ship a candidate |
| libbox.aar | tooling release `tooling-libbox-singbox-1.13.10`, SHA256 `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6` | `.github/workflows/build-android.yml` (`LIBBOX_RELEASE_TAG` + `LIBBOX_AAR_SHA256`) | rebuild via `tools/build-libbox-aar.sh`, upload a new tooling release, then bump BOTH `LIBBOX_RELEASE_TAG` and `LIBBOX_AAR_SHA256` together |
| Zapret / winws | latest Flowseal release, fetched on demand | `ZapretUpdater` | upstream auto-resolved at runtime |
| Telegram proxy (tg-ws-proxy) | latest upstream release + Python 3.12.7 embed + PyPI wheels | `TgProxyUpdater` | upstream auto-resolved; Python zip pinned by `PythonZipSha256`, wheels by PyPI digest |

## Trust roots (download verification posture)

Several of these fetch + run **executable code under the user's account**, so
their integrity posture matters as much as the version pin:

- **libbox.aar** — hard sha256 CI gate (strongest). Trust root: `LIBBOX_AAR_SHA256`.
- **tg-ws-proxy (Python + wheels)** — fail-CLOSED sha256 at install time
  (dep-review P1-2, 2026-07-10): the Python embeddable is checked against the
  pinned `TgProxyUpdater.PythonZipSha256` (captured from python.org canonical;
  recompute on a `PythonVersion` bump), each PyPI wheel against the authoritative
  `urls[].digests.sha256` from PyPI's own metadata. Trust root: python.org
  canonical (one-time capture) + PyPI's published digests.
- **Zapret / winws** — **verification is download-SIZE only** (`ZapretUpdater`),
  a deliberate trade-off for auto-fresh DPI-bypass strategies from
  `Flowseal/zapret-discord-youtube`. Trust root: **the upstream GitHub account +
  TLS to github.com**, nothing more. Residual risk: a compromise of that upstream
  account ships a winws.exe payload (runs with admin) on the next auto-update.
  Accepted for now; hardening option = a known-good tag pin in the VPNRouter
  release + explicit user confirm before upgrading to an unverified tag (the
  version-check flow already exists). Tracked: OPEN-DEFECTS dep-review P2-1.

## Notes

- The libbox.aar SHA256 is the only native dep with a hard CI integrity gate
  (added 2026-05-30, commit 817a9ed). Keep `LIBBOX_AAR_SHA256` and
  `LIBBOX_RELEASE_TAG` in lockstep — a mismatch is a hard build failure by
  design, so a stale hash forces both to be updated together.
- sing-box and libbox are GPL-3.0; Zapret and tg-ws-proxy carry their own
  upstream licenses (see `NOTICE.md`).
