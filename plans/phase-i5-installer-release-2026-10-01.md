# I-5: the Windows installer becomes a release asset (18 assets)

## Why

The Inno Setup installer (I-2, #417) builds on a worker but no release carried it.
The owner asked for r11 to include the installer.

## What

- `build.ps1 -Upload` (or `-Installer`) calls `tools/build-installer.ps1` on the install zip and stages
  `VPNRouter-Setup-vX.Y.Z.exe` plus its `.sha256` with the other Windows assets.
- The inventory is 18 assets (9 binaries, 9 sidecars) in `verify-release-integrity.yml`,
  `tools/post-ship-verify.ps1`, the tests and the release skills/docs.
- The integrity workflow checks the installer is a PE file (`MZ`) and that its version resource
  carries the release version. The payload is compressed, so the version text comes from the new
  `VersionInfoTextVersion`/`VersionInfoProductTextVersion` lines in `vpnrouter.iss`.
- README.md and README.ru.md describe the installer; it is unsigned (see the signing decision).

## Verification

On the Windows worker (scratch dir `C:\android-build\i5test`, not Program Files; CPU 25 percent, 13.3 GB free RAM,
17.8 GB free disk, no builds or emulator running): `build-installer.ps1 -Version 2.50.0-r11` produced a
50.3 MB exe; the workflow's own `versions()` scan returned exactly `2.50.0-r11`; sidecar matches `sha256sum`.
Exact-head CI runs the release tooling tests (`characterization-windows`).

## Outcome

Pending.
