# D-4: updates and the installer fail on the locked split-tunnel driver file

## Why

The owner's r14 update did not apply and the Inno installer stopped with "DeleteFile: failure; code 5, access denied" on
`C:\Program Files\VPNRouter\app\driver\mullvad-split-tunnel.sys` (retry / skip / cancel prompt). The diagnostics bundle shows the
in-app updater at 22:16: `xcopy exit=4`, `.update-failed` marker written, the old version relaunched; and
`windows-services.txt`: the kernel driver service `mullvad-split-tunnel` ("Mullvad Split Tunnel (VPNRouter)", image path inside
the install folder) is RUNNING.

## Cause

True Split loads that driver from `app\driver`. A loaded kernel driver locks its `.sys` file. When the app is killed (by the updater, or
by the installer's stop script) the driver stays loaded, so neither `xcopy` nor Inno Setup can replace the file. Earlier updates
worked only while the driver happened to be unloaded.

## What

- The update helper script (`UpdateChecker.Apply.cs`) stops `mullvad-split-tunnel` before the copy when its image path is inside the
  install folder.
- `packaging/windows/installer/stop-vpnrouter.ps1` does the same after it stopped the processes (a Mullvad client or another copy keeps its own driver:
  only a driver whose file is in the install folder is touched); the driver starts on demand afterwards.
- The installer's main `[Files]` entry gets `restartreplace`: a file that still stays locked is replaced at the next restart instead of
  stopping the setup with a prompt.
- A contract test pins the three pieces.

## Not covered

A run on a machine with True Split active (the worker's driver is not loaded); the fix follows the diagnostics and the Windows
service rules and is covered by the contract test only. The owner's confirmation with the next update is the real check.

## Outcome

Pending (released with the next candidate).
