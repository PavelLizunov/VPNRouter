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

Pending.
