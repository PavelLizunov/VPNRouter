# U1: a state hero and a filled primary button on the main screen

## Why

The owner asked for the app's look to be checked, fixed and redrawn in a more modern form (2026-09-30). An audit on an
Android 14 emulator (Pixel 6 size 1080x2400 and a 720x1600 budget size, light and dark, Russian and English, large
system font) found for the main screen: the state was a small card with a 10 dp dot and 10 dp subtitle, the primary action
(Connect) was an outlined low-emphasis button below a long form, the form with the config field and routing options was
always expanded, small captions were 9-11 dp, and the system font scale was ignored (the app's text did not change when the
system text size did, while the system clock did).

## What

- `Controls/StatusCard.cs`: same properties (`IsOn`, `IsWarn`, `IsOff`, `Title`, `Subtitle`), new look: a 112 dp ring
  with a power glyph whose colour follows the state (grey, amber with a slow opacity pulse while connecting, green),
  centred 22 dp title and 13 dp subtitle, rounded card. The pulse is a composited opacity animation, only while
  connecting, cancelled when the state changes or the control leaves the visual tree.
- `AndroidApp.axaml.cs`: the three connect buttons are 52 dp tall, 16 dp bold, rounded 14; Connect is the filled accent
  button, Connecting a quiet disabled state, Disconnect an outlined button. They now sit directly under the hero, above the
  config summary row. The form with the config field, routing and autostart is collapsed when a subscription or link is
  already saved (expanded on first run). The health and error lines under the hero are centred and 12 dp.
- `UiScale` (Android) and `UiTypeScale` (Core, unit-tested): the shared type scale, `source size + 2` times the system font
  scale (clamped to 1.0 to 1.4), read once when the UI is built. Only the new sites use it in this step; U2 applies it to
  the rest of the UI.

No change to the connection logic, the state updates or the handlers.

## Verification

- CI: Android compile, the new `UiTypeScaleTests`, the existing suite.
- Emulator (Linux worker, Android 14 x86_64): screenshots of the main screen in light and dark, English and Russian, at
  1080x2400 and 720x1600, after a connect attempt (error line under the hero). Not verified on screen: the green "connected"
  state and the amber "connecting" pulse, because the tunnel cannot start on an x86_64 emulator (libbox is arm64-only);
  they use the same code path as the grey state with different brush keys.

## Outcome

Merged after green exact-head CI. Follow-ups: U2 applies the type scale and the system font scale to all text; U3
simplifies the Advanced screens (tabs, settings master-detail, touch targets, button roles).
