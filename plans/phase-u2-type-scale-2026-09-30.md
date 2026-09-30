# U2: one type scale and the system font scale for all text

## Why

The U1 audit found that the Android UI ignored the system font size (set to 1.3 and relaunched: the system clock grew,
the app's text did not) and that 149 of 208 font sizes in the code were 9 to 11 dp, below the Android minimums for body
text (12 to 14 sp).

## What

- 199 `FontSize` and 7 `LineHeight` numeric literals in the Android UI sources (14 files) now go through `UiScale.Fs` and
  `UiScale.Lh` (added in U1): source size + 2, times the system font scale clamped to 1.0 to 1.4. A scripted rewrite of
  numeric literals only (`FontSize = 11` becomes `FontSize = UiScale.Fs(11)`); no other code changed, no layout edit.
- Two places where a title clipped at 1.3 now wrap: the settings section buttons (they showed "Leak Protectio") and the
  title of the checkbox cards (the "Russian traffic via real IP" card).
- The scale is read when the UI is built, so a change of the system font size applies after the app is restarted.

## Verification

- CI: Android compile and the existing suite (the scale math has unit tests since U1).
- Emulator (Android 14, Linux worker), dark theme, English: main screen, Servers, Subscribe, Settings, Apps, Public and the
  menu at the default scale: larger text everywhere, no clipped or overlapping text. Same screens at system font scale 1.3:
  text grows everywhere; the header title ellipsizes ("Virtual Penguin Netwo..."), two titles clipped before the wrap fix
  (recorded above), tab labels are tight but readable.
- Not checked: scales above 1.4 (clamped), Russian at 1.3, tablets.

## Outcome

Merged after green exact-head CI.
