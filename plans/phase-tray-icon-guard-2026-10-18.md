# Tray icon guard (2026-10-18)

## Why

Owner report on Windows: the window "did not go to the tray, it just disappeared, but the app did not close". Closing the window only hides it (`App.axaml.cs`, `Closing` -> `Hide()`), and the window then comes back only from the tray icon or a second launch. If the tray icon is not in the notification area, the app is still running with nothing to click.

Two causes fit, both without logs today:

- The icon is not registered. Avalonia 12.1.3 `TrayIconImpl.UpdateIcon` sets `_iconAdded = true` even when `Shell_NotifyIcon(NIM_ADD)` fails (a busy or not yet ready shell at autostart). Later `NIM_MODIFY` calls then fail silently. Avalonia re-adds only on a `TaskbarCreated` broadcast, which a started-but-not-ready tray does not send.
- The icon is registered but hidden: Windows 11 puts new icons behind the "^" overflow arrow.

The app never logs which case happened.

## What

Windows only. Linux and macOS keep the current behavior.

1. On close, ask the shell whether the icon exists (`NIM_MODIFY` with no flags, on Avalonia's message window and the icon id).
2. If it is missing, re-add it with the public API (`IsVisible` false then true) and check again.
3. If it is still missing, minimize the window to the taskbar instead of hiding it, so the window cannot be lost.
4. The first time the window goes to the tray, show a one-time balloon: the app keeps running, and the icon may be under the "^" arrow. A marker file in the data dir records that the hint was shown.
5. A background check 10 s after start and then every 2 minutes re-adds a missing icon. If the icon cannot be added while the window is hidden, it shows the window minimized on the taskbar (covers autostart with `--minimized`).
6. Log every decision with a `[Tray]` prefix.

## How

- New `VPNRouter.App/Services/TrayGuard.cs`:
  - Finds the message window by enumerating this process's top-level windows for the class prefix `AvaloniaMessageWindow `.
  - Uses `Shell_NotifyIconW` for the existence check and the balloon.
  - Contains pure decision helpers for tests.
- `App.axaml.cs`: the `Closing` handler and the timer use it.
- Core strings RU/EN for the balloon.

## Risk

The probe and the balloon touch only our own icon id on our own message window. If the window or id is not found, the result is "unknown" and today's hide behavior stays. Each re-add makes the icon blink once.

## Rollback

Revert the PR. No settings schema change; the marker file `tray-hint-shown` is ignored by older builds.

## Gates

1. Release build and full test suite in CI, zero failures.
2. Unit tests for the decision helpers and the marker.
3. Live WINBRAT check: close the window, then confirm the `[Tray]` log line, the balloon once, and a click on the icon bringing the window back.
4. Live WINBRAT check: remove the icon behind the app's back, then confirm the guard re-adds it or minimizes to the taskbar.
5. Independent review by a second model (pi), with every finding verified against the code.
6. Outcome recorded in this brief.

## Outcome

### Root cause (found during the live check)

There was no tray icon at all. Avalonia 12.1.3 changed `TrayIcon`: the constructor no longer creates the native icon, and `Attach()` creates it only for an icon in `TrayIcon.Icons` on the Application. 12.1.2 and earlier created it in the constructor.

`App.axaml.cs` only constructed the icon. The 12.1.3 bump came in #324 (2026-09-28) and first shipped in v2.50.0, so v2.50.0 through v2.50.4-r1 have no tray icon on any desktop. Closing the window hid it, and only a second launch could bring it back.

Fix: `TrayIcon.SetIcons(this, new TrayIcons { _trayIcon })`, guarded by a source contract test.

### Guard

The guard from the brief stays, as a safety net for a shell that is not ready or is gone:

- `TrayGuard` checks the icon (in-process `NIM_MODIFY`) and re-adds it.
- If the icon is still missing, the window is minimized to the taskbar instead of hidden.
- The 10 s / 2 min watch does the same for a window that is already hidden.
- The one-time balloon tells the user where the icon is.

Files: `VPNRouter.App/App.axaml.cs`, `VPNRouter.App/Services/TrayGuard.cs` (new), `VPNRouter.Core/Localization/Strings.cs`, `VPNRouter.App/Localization/Strings.cs`, `VPNRouter.Tests/TrayGuardTests.cs` (new).

### Verification

Live on WINBRAT (Windows 10 1809), with App, Core and Service all from 9407182d:

1. Close with the shell up: the window hides, the log says `icon in the notification area: True`, and the Russian balloon shows once. The penguin is in the tray and in the overflow.
2. A second close hides again, with no second hint.
3. Explorer killed (`AutoRestartShell=0`), then close: the window is minimized to the taskbar (`re-added: False` logged). After explorer restarts, Avalonia re-adds the icon and the next close hides it to the tray again.
4. Explorer killed while the window is hidden: the watch shows the window minimized within one tick, in under a minute.
5. A left click on the penguin restores the window. A right click opens the menu ("Настройки...", "Запустить VPN", "Выход").

The first live run with the guard alone showed the probe always false, because the icon never existed. That run led to the root cause.

Text shortened afterwards: Windows 10 cut the balloon after four lines.

CI and the second-model review: see the PR.

### Surprises

- Deploying App and Core without a matching `VPNRouter.Service.dll` makes the next start reinstall the published build (`InstallHealthCheck` -> `SelfRepair`). The first test run silently tested v2.50.4-r1.
- The shell answers tray queries only for the calling process's own icons.

### Follow-ups

- Not verified: Linux and macOS tray after the registration fix (same Avalonia path, expected to come back), and Windows 11 overflow behavior.

### Rollback

Revert the PR. Older builds ignore the `tray-hint-shown` marker.
