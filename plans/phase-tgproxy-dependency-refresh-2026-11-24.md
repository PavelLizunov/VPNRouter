# TgProxy dependency refresh, 2026-11-24

## Intent and scope

Fix the release-blocking optional Windows TgProxy runtime so first install, start,
autostart and update cannot keep using the compromised certifi 2026.7.22 or the
advisory-affected cryptography 46.0.5 merely because package `__init__.py`
markers exist. Scope is `TgProxyUpdater`, the TgProxy launch/autostart callers
and TgProxy tests only. Shared `plans/OPEN-DEFECTS.md`, the Standard prerelease
plan and dependency audit receipts remain owned by the parent.

## Pins verified independently

All artifacts were fetched data-only into
`/tmp/opencode/tgproxy-deps-evidence-20261124` outside the repository. No
package was installed and no TgProxy source download was executed.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `python-3.14.10-embed-amd64.zip` | 12,619,664 | `90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159` |
| `cffi-2.1.1-cp314-cp314-win_amd64.whl` | 187,949 | `3222ba5d678f80a030e6afbcc33dc1ae5cb45facabb61cee2c7016b8432fde48` |
| `cryptography-50.0.2-cp311-abi3-win_amd64.whl` | 3,824,692 | `7afa5a6602a9f29af1f3a2965f831bae7c9d5d597b7cbb716d41ab3b7d89879c` |
| `certifi-2026.11.20-py3-none-any.whl` | 127,913 | `7422df25243767b9877031a2248cb14d09ddac9beee846f4cef3d5c9191d488f` |
| `pycparser-3.0-py3-none-any.whl` | 48,172 | `b727414169a36b7d524c1c3e31839a521725078d7b2ff038656844266160a992` |

The previous Python 3.12.7 pin was also rehashed from python.org and matched the
existing `0d57bb6c...bf44` constant.

Compatibility checks from the official wheel metadata:

- `cryptography 50.0.2` requires Python `>=3.9`, declares CPython 3.14 support,
  and requires `cffi>=2.0.0`. The `cp311-abi3-win_amd64` wheel is the correct
  regular-ABI choice for CPython 3.14; the `cp314-cp314t` wheel is free-threaded
  only.
- `cffi 2.1.1` has a regular `cp314-cp314-win_amd64` wheel and requires
  `pycparser` on CPython. `cffi 2.1.1` satisfies cryptography's `>=2.0.0`.
- `certifi 2026.11.20` and `pycparser 3.0` are pure-Python wheels and declare
  CPython 3.14 support.

OSV queries returned no advisories for certifi 2026.11.20 or cryptography
50.0.2. The former pins are covered by `GHSA-6q48-6c7w-9gw2`,
`PYSEC-2026-4320` and the cryptography advisories already recorded in the
defect ledger.

## Implementation invariants

- `IsInstalled` now requires `python.exe`, `python314.dll`, the pinned Python
  version marker of 3.14.10 and a dependency layout matching the pinned dist-info
  versions and runtime binaries. A legacy `certifi/__init__.py` marker alone is
  not installation.
- Dependency readiness requires `certifi/cacert.pem`, the cryptography
  `_rust.pyd`, `cffi/__init__.py`, the single cffi `cp314` ABI binary, the
  pycparser module and exactly one expected dist-info directory per package.
  Old cffi ABI files or conflicting dist-info directories force repair.
- Migration stages a clean Python tree with all four verified wheels whenever
  the current Python or dependencies are stale. An explicit source update can
  reuse a current Python. Both paths import the hash-verified source using
  the selected interpreter and verify module paths, package versions, cffi and AES.
- After the probe succeeds, a same-volume journal covers python (if replaced),
  proxy and version.txt. A file lock serializes installers across
  application instances. Readiness requires the current source version and entry
  point and rejects a pending activation. A retry restores the previous entries
  before any network request. Other files in the runtime directory are preserved.
- A legacy installation requires one online repair. Desktop and service autostart
  skip stale files, and the settings page offers Update.

## Verification and open items

- Added runtime tests for legacy marker-only state, old package versions,
  conflicting cffi ABI, missing cryptography binary, pinned Python marker,
  pinned dependency layout and whole-directory replacement.
- No build, install, compiled test or commit was run on the workstation per the
  owner instruction. The changed source and test diff were reviewed statically.
- Actual Windows imports and TgProxy 1.10.0 startup on Python 3.14.10 must be
  exercised on WINBRAT during parent acceptance. Metadata shows no packaging
  incompatibility, and the Python 3.14 documentation supports Windows 10+ with
  explicit Server 2019 support. A read-only inspection of the local 1.10.0 audit
  copy found only stable asyncio, ssl and cryptography interfaces; the live
  import/start is still the runtime gate.
- Regression tests cover source-only and full activation, restart with a partially
  moved interpreter or source, conflicting journals, and a stale source marker.
  The native inventory lists the five replacement artifacts.
