# D-3: the desktop app crashed on the Applications tab (render-pass invalidation)

## Why

The owner sent a diagnostics bundle of r14 and said the app crashed once after the update when they clicked around. The bundle
shows the run (PID 21044) logging "OnSelectedTabIndexChanged tab=2", then "tab=3" at 22:17:02, then nothing; the next
start warns "Previous run did not shut down cleanly". Tab 3 is the Applications tab.

## Reproduction

On WINBRAT (r14 deployed), the app switched to advanced mode with `ui_mode: advanced`, then UI automation selected
the Settings tab and the Applications tab: the process died. The .NET Runtime event:
`System.InvalidOperationException: Visual was invalidated during the render pass` from
`TextBlock.RenderCore -> MeasureEmbeddedControls -> Layoutable.Measure -> InvalidateMeasure`. It is the defect
PAGESCREENSHOT-RENDER-INVALIDATION (six `PageScreenshotTests`, failing since 2026-09-29), which the ledger had filed as
tests-only (P3) and "not seen in production".

## Cause

Read in the Avalonia 12.0.3 source: `InlineUIContainer.BuildTextRun` measures its child with the block width.
`TextBlock.OnMeasureInvalidated` drops the text layout and `RenderCore` rebuilds it lazily; a text block that is rendered
before its first layout pass still has a NaN constraint, i.e. width 0. A fresh inline `Pictogram` that is measured for
the first time there changes its DesiredSize (0 to the icon size), `ChildDesiredSizeChanged` asks the parent to
invalidate itself in the middle of the render pass and the compositor throws. Earlier experiments (measure at creation
with the icon size, fixed-size `MeasureCore`, settling layout in the test helper) failed because the render-time measure uses width 0.

## What

`PictogramText.Update` measures every new inline icon at zero width (`Measure(0, infinity)`), so the render-time measure with
width 0 finds the DesiredSize unchanged; the real layout pass measures it normally. A unit test pins it.
`ApplicationsPage` is back in the design captures; the ledger line is resolved and raised to P1 (a crash).

## Verification

On `windows-worker` (private SDK, nothing else running): the six `PageScreenshotTests` that failed before pass, together with the
pictogram, visual-diff and design capture tests (83 passed, 0 failed). The release build still has to be run through the UI-automation
reproduction: see the release brief.

## Outcome

Pending (released with the next candidate).
