# D-21: a natural way back to the home screen; the home screen fits its window

## Why

Owner feedback on r24: (1) "on part of the screenshots a button does not fit, its bottom is cut": the UI probe confirms it, at the default 760 px window the last home row ("Advanced settings") is cut off by 24 px and the content ends about 90 px below the
window; (2) the outlined "Simple" pill "stands out from the interface, has no icon, looks unnatural".

## What

1. The way back is a house icon plus the word "Home" ("Главная") on a quiet accent tint, no outline, same radius as the other header controls, on desktop (header) and on Android (Advanced header). New Lucide `house` icon (pinned tag 1.49.0, generated into `UiIcons`), Pictogram id `home`. The word says where it goes ("Home", not the internal "Simple").
2. The home screen is tighter (page margin 24 -> 16, space above the state word, the button, the card and the quiet list reduced by 4-6 px each) and the default window is 820 px tall (`MainWindow.PreferredHeight`; `FitToScreen` still shortens it to the work area), so all rows fit on a normal monitor.

## Not covered

On a small screen (the test machine is 1024x768, the window is shortened to about 720 px) the lowest rows still scroll; that is by design.

## Verification

UI MCP: cut-off lint on the home screen at 520x820 (EN/RU, light/dark, configured/connected) must show no cut-off; the audit matrix for `simple` and `window-advanced`; page and window suites on windows-worker at the exact SHA; icon checks (`check-icons.py`); a real Windows screenshot and an emulator screenshot of the next candidate.

## Rollback

Revert the PR.

## Outcome

Merged as #507 and shipped in r25 (see the r25 brief for the checks).
