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
- Known limit: the autostart task writes `HKCU\...\Run`, i.e. for the account that runs Setup elevated. On a normal PC that
  is the signed-in user; if a standard user types another admin account's password at the UAC prompt it is that admin's
  hive (ISCC warns about it, `UsedUserAreasWarning=no` acknowledges it). The in-app autostart switch fixes it per user.
- Not verified: a real install into Program Files, the service task, the Defender task, upgrade over a live install,
  compatibility with an actual in-app update, SmartScreen behaviour on the unsigned `.exe` (I4 and I6 need a clean VM
  and the owner's explicit permission because they touch a real installation).

## Outcome

CI: `test` (with the contract tests), `grep`, `go-test-windows` and `characterization-windows` were green on head
`02a5baf9` before the rebase onto newer `main`; the final head is listed in the pull request checks (the commits after
it only add a compile directive, this brief, or rebase).

Windows worker (`tester@100.115.182.0`, the machine that also hosts a real VPNRouter install), 2026-10-01, everything from
exact SHAs fetched shallowly from GitHub (installer files `aa689cce`, CLI payload built from the I3 head `db4ac21f`):

- Load preflight (read-only): 13.3 GB of 16.0 GB RAM free, CPU 0 to 2 %, C: 17.8 GB free, no `dotnet`, `VBCSCompiler`,
  `MSBuild`, `java` or `ISCC` process.
- Tooling: official Inno Setup 6.7.3 (`innosetup-6.7.3.exe` from the `jrsoftware/issrc` GitHub release that
  jrsoftware.org/isdl.php links to), SHA256 `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`, equal to
  the digest GitHub publishes for that asset; Authenticode `Valid`, signer `CN=Pyrsys B.V.` (the publisher the download
  page names), issuer Sectigo. Installed user-locally (`/CURRENTUSER /VERYSILENT /DIR=C:\android-build\tools\innosetup`,
  33 MB, kept as task-owned tooling); `Languages\Russian.isl` present; compiler engine "Inno Setup 6.7.3".
- Compile: `tools\build-installer.ps1 -Version 2.50.0 -PackageDir <scratch payload>` exit 0 in 43 s, `VPNRouter-Setup-v2.50.0.exe`
  31.5 MB, `.sha256` sidecar written, file version 2.50.0.0, `NotSigned`. A second compile without `/Q` first printed one
  warning (HKCU Run value under `PrivilegesRequired=admin`), acknowledged with `UsedUserAreasWarning=no`; the final head
  compiles with no warnings.
  The payload was the real CLI from the I3 head plus stand-ins (a copy of `ping.exe` as `app\VPNRouter.App.exe` so that a
  process can run from the install folder, a text file as `app\VPNRouter.GUI.exe`, `Start VPN.cmd`, `README.txt`); it is
  not a release build.
- Scratch cycle in `C:\android-build\i2\inst`, all with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOSYSTEMCHANGES
  /TASKS=`:
  1. Install (`/LANG=russian`): exit 0, 273 files (`app\`, `installer\stop-vpnrouter.ps1`, `Start VPN.cmd`, `README.txt`,
     `unins000.exe/.dat`), Uninstall key `{BAAAEA6C-...}_is1` with DisplayName VPNRouter, DisplayVersion 2.50.0,
     InstallLocation the scratch folder; no `HKCU\...\Run` value; shared Start Menu shortcut untouched.
  2. Upgrade over it (`/LANG=english`) while a stand-in app ran from the install folder and a second stand-in ran from
     another folder: exit 0; the stand-in in the install folder was stopped, the one in the other folder and the real
     `VPNRouter.App.exe` (PID 9572) kept running; `app.bak\old.txt`, `.update-backup.lock` and a user file stayed; still
     exactly one Uninstall key.
  3. Uninstall (`/CLEANUPARGS=--dry-run /CLEANUPLOG=...`, `ProgramData` redirected to a scratch folder): exit 0; the
     cleanup step ran the real CLI and its log shows the dry-run report (8 "not present" lines, "Dry run: 0 item(s) would
     be removed, nothing was changed."); a stand-in running from the install folder was stopped; the Uninstall key was
     removed; `app\`, `app.bak`, `.update-backup.lock`, `installer\` and `unins000.*` were deleted; only the user file stayed
     (Inno logged "Failed to delete directory (145)" for the non-empty folder, as intended).
  4. Read-only snapshots before and after the whole cycle (12 items, see the I3 brief, including all files under
     `Program Files\VPNRouter`, the shared shortcut, Defender exclusions, services, firewall rules, Run keys, Uninstall
     keys, data-folder ACL and entries, DNS servers): identical.
- Cleanup: no process left running from the scratch folder, `C:\android-build\i2` (420 MB) and every helper script
  deleted, the scratch Uninstall key absent, disk 17.8 GB free again. Kept: `C:\android-build\tools\innosetup`.
- Not verified: a real install into Program Files; the service, Defender and autostart tasks (and therefore their removal);
  the data-folder ACL step, the registry migration of the old script install and the shared Start Menu shortcut (all
  skipped by `/NOSYSTEMCHANGES`); an upgrade over a live install; compatibility with a real in-app update from the
  updater zip; that the Russian wizard text shows (silent mode only; the `.isl` compiled); the interactive wizard;
  SmartScreen and antivirus behaviour of the unsigned `.exe`; a real `cleanup` removing something (I3 covers it with fakes).
  These need a clean VM and the owner's permission (I4, I6).
