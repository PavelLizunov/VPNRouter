# D-9: the web installer could leave a half-deleted installation

## Why

The owner tested r16 on 2026-10-02: after the app closed during quick tab clicking, the Start Menu shortcut said the target had been removed and the
app had to be installed again. The diagnostics bundle has not arrived yet, so the trigger of the crash is still open (see below). What the code
review of the repair paths did show:

- `packaging/windows/install.ps1` (the `iwr | iex` installer, also run by `SelfRepair.Run` after three failed launches or an unhealthy install, and by the
  launcher stub's repair) downloaded and verified the archive, then **removed everything inside `C:\Program Files\VPNRouter`**, then extracted over it.
- It never stopped the split-tunnel kernel driver. The loaded `mullvad-split-tunnel` driver keeps `app\driver\mullvad-split-tunnel.sys` open (the same lock as
  D-4, which fixed only the in-app updater and the Inno installer). The wipe left that file behind, `Expand-Archive -Force` then failed on it, and the script exited
  after the wipe: `app\` mostly gone, shortcut dead.

## What

1. Stop the split-tunnel driver before touching the folder, only when its image path is inside the install folder (same rule as `stop-vpnrouter.ps1`).
2. Unpack the archive into the secured staging folder first and check for `app\VPNRouter.App.exe` there; a failed extraction or a wrong layout now exits
   without touching the existing installation.
3. Only then wipe and copy the staged payload in. If the copy fails (a file still in use) the staged payload is kept and named in the message.
4. Contract test `WindowsInstaller_StopsTheDriverAndChecksThePayloadBeforeWipingTheInstallation` pins the order.

## Not covered

- The trigger of the crash on quick tab clicking is unknown. A fast random navigation run (500 UI Automation selects, about 8 per second, across all
  tabs, sub-tabs and the Simple/Advanced toggle) on the deployed r16 build on `windows-worker` did not crash the app. The headless sweep of the new UI
  probe (150 random clicks, no delay) raised nothing either. The owner's diagnostics (`vpnrouter*.log`, `update.log`, Windows Application event log,
  `%TEMP%\vpnrouter-trampoline.log`) decide what is next.
- The Inno installer and the in-app updater were fixed in D-4 and are unchanged.
- The installer itself has no live test here (the worker's driver is not loaded); the parse test and the ordering contract run, the owner's next repair or
  `iwr | iex` run is the real check.

## Verification

`ReleaseToolingContractTests` on `windows-worker` at the exact SHA (includes the PowerShell parse test), CI on the PR.

## Outcome

Pending.
