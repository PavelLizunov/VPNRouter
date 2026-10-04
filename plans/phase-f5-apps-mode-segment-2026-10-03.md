# F-5: the apps-mode selector always shows the selected mode (tester report on r20, item 9)

## Why

Tester screenshot 9 (Applications, `routing_apps_mode: exclude` in the diagnostics config): neither "Only selected" nor "All except selected" is highlighted. The two segments were `ToggleButton`s bound two-way to a pair of booleans whose
setters ignore `false` (`IsRoutingAppsModeInclude/Exclude`). Pressing the selected segment toggled it off in the control while the model kept the mode, so the strip showed no selection. A headless test with a real click failed on the old
markup (red) before the change.

## What

Radio buttons in one group instead of toggle buttons (the same pattern the home screen already uses for Selected apps / All traffic); same look, template copied from the old toggle style.

## Not covered

Segment look in the dark theme is unchanged (subtle accent text on a dark pill); the apps page layout in a narrow window is part of F-7.

## Verification

`ApplicationsPageModeSegmentTests` (real mouse click through the headless window: pressing the selected segment keeps it checked; pressing the other switches the mode), PageScreenshot and apps-mode suites on windows-worker at the exact SHA, CI.

## Rollback

Revert the PR.

## Outcome

Pending.
