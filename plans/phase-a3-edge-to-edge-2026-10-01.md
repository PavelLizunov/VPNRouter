# A-3: safe area on Android 15 and 16 (huge gap at the top, tab bar under the gesture bar)

## Why

The owner tested r11 on a Pixel: a very large empty space above the header (about 130 dp), and once the whole
screen collapsed into a narrow strip. Reproduced on an Android 15 (API 35, edge-to-edge is enforced for apps
targeting 35 or higher) emulator, not on the API 34 one.

## Cause (measured with new numbers in the test hook state dump, `layout` block)

- `AndroidApp` applied `insetsMgr.SafeAreaPadding` once at attach time and then only on the insets event. At attach
  time the top level has render scaling 1.0, so the value is in pixels (top 128, bottom 63) instead of dp
  (48.8 and 24). No event followed to correct it, so the main scroller got 134 dp of top padding (a rotation fixed
  it, because that fires the event again). On API 34 the same bug gave 69 dp instead of 30 dp and went unnoticed.
- The status footer of the Advanced shell carried the bottom inset, but since U3 the tab strip is docked lowest, so
  its labels sat under the gesture bar.
- The main column was `HorizontalAlignment=Center` without a width, so it shrank to the natural width of its content.

## What

- Read the safe area again on insets, scaling and client size changes; ignore the activity's own inset source once
  Avalonia's is active (it reported zeros on API 35).
- The main column stretches to its maximum width (the layout system centers it).
- The bottom inset goes to the tab strip of the Advanced shell.
- Test hook: `layout` block in `TEST_DUMP_STATE` (activity and Avalonia safe area, applied value, scroller padding,
  bounds, scaling, density).

## Verification

Android 15 emulator (test-hook build): before, `appliedSafeArea` 0,128,0,63 and `scrollerPadding` 0,134,0,79; after,
0,48.8,0,24 and 0,54.8,0,40, wrapper full width 379.4 dp, header directly under the status bar. Screenshots in the
scratchpad. The API 36 emulator images crash surfaceflinger headless (not usable here).

## Outcome

Pending.
