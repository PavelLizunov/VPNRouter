# I2: the Windows installer (Inno Setup `.exe`), built from the same tree as the zip

## Why

Owner-approved proposal (`plans/phase-installer-proposal-2026-09-30.md`, 2026-09-30): Windows has no installer file,
only `iwr | iex` and a zip; uninstall needs the network; Defender exclusions are added and never removed; nothing offers
a folder, language, autostart or service choice. The owner chose Inno Setup and said to go ahead ("делай"). The
`cleanup` command (I3) is the first step; this step is the installer itself, not yet wired into the release.

## What

- `packaging/windows/vpnrouter.iss` (UTF-8 with BOM, Inno Setup 6): machine-wide install to `Program Files\VPNRouter`
  (`PrivilegesRequired=admin`, x64 only), fixed `AppId`, English and Russian, `VPNRouter-Setup-vX.Y.Z.exe`. It installs the
  same tree as the zip (`Start VPN.cmd`, `README.txt`, `app\`) and the Start Menu shortcut points at
  `app\VPNRouter.GUI.exe`, the target the app's `ShortcutSelfHeal` expects.
- Tasks, all unchecked by default: desktop icon, autostart (the same `HKCU\...\Run` value the app writes), the Windows
  service (`VPNRouter.CLI.exe service install` and `start`), and Microsoft Defender exclusions for the install and data
  folders (unchecked because exclusions for an unsigned binary are a security trade-off the user should choose; the
  `install.ps1` script adds them unconditionally).
- Upgrade over an existing install: files are overwritten in place, nothing else in the folder is deleted, so the
  updater's `app.bak`, `.update-backup.lock` and the receipt in `%ProgramData%` survive and the in-app updater (which
  replaces files in `app\` from `VPNRouter-update-*-win.zip`) keeps working; the running app, its service and any process
  that runs from the install folder are stopped first with `installer\stop-vpnrouter.ps1` (path-scoped: it never stops
  a process or service only by name, so a second copy elsewhere is left alone) and the service is started again if it
  was running. The data-folder ACL is restricted as `install.ps1` does, and the registry entry that `install.ps1` wrote
  (`Uninstall\VPNRouter`) is removed when it points at this folder, so Apps & Features does not list the program twice.
- Uninstall: stops the app and the service it owns, runs `VPNRouter.CLI.exe cleanup` (I3; skip with `/NOCLEANUP`; the
  output goes to `/CLEANUPLOG=` or a temp file and a failure shows a short message, it never blocks the uninstall), removes
  the Defender exclusions if the task had added them, deletes the installed files and the updater's artefacts, and keeps
  `%ProgramData%\VPNRouter` unless the user answers yes (default no) or `/DELETEDATA` is given.
- Silent mode: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, `/DIR=`, `/TASKS=`; extra switches `/NOCLEANUP`,
  `/DELETEDATA`, `/CLEANUPARGS=--dry-run`, `/CLEANUPLOG=` and `/NOSYSTEMCHANGES` (scratch or staging installs: no ACL change,
  no registry migration, no shared Start Menu shortcut, no service or Defender change, no data deletion, the stop script is
  limited to the install folder). The switch exists because the Windows worker also hosts a real VPNRouter install whose
  shortcut, data folder and services a test must not touch.
- `tools/build-installer.ps1 -Version X -PackageZip <win.zip>` (or `-PackageDir`): finds ISCC.exe, compiles, writes the `.exe`
  and a `.sha256` sidecar. Not signed, not uploaded; the release asset contract (16 files) and `build.ps1` are unchanged.
- `.gitignore` re-includes `tools/build-installer.ps1` (`tools/*` is ignored).
- `WindowsInstallerContractTests` pin the promises above as text checks (they do not compile the script).

## Verification

- CI: the contract tests; `grep` and compile jobs unaffected.
- Windows worker (private toolchain folder; load preflight numbers recorded in the outcome): official Inno Setup 6
  installed user-locally into `C:\android-build\tools\innosetup`, the script compiled with ISCC, then a scratch install and
  uninstall into a temporary folder with `/NOSYSTEMCHANGES`, the real `VPNRouter.CLI.exe cleanup --dry-run` from the
  I3 build as the payload, services, Defender, autostart, Program Files and the real data folder untouched.
- Not verified: a real install into Program Files, the service task, the Defender task, upgrade over a live install,
  compatibility with an actual in-app update, SmartScreen behaviour on the unsigned `.exe` (I4 and I6 need a clean VM
  and the owner's explicit permission because they touch a real installation).

## Outcome

To be filled in after the green CI run and the worker compile and scratch install.
