# H-45: split the Android simple page builder into sections

## Why

`AndroidApp.BuildSimplePageView` in `VPNRouter.Android/AndroidApp.axaml.cs` was 780 lines, the longest function of the
repository: header with the kebab menu, status card, config row, server input, tunnel mode, connect buttons,
advanced card, scroll layout and the touch-scrolling handlers in one body.

## What

The body is now a short assembly that calls, in the original order, `BuildSimpleHeaderRow`,
`BuildSimpleStatusCard`, `BuildConfigRowButton`, `BuildServerInputSection`, `BuildTunnelModeSection`,
`BuildConnectButtons`, `BuildAdvancedCardButton` and `AttachTouchScrolling`; the form card, menu feedback, update
banner, scroll wrapper and overlays stay in the assembly. Each new method holds the statements of its section
unchanged; the corner radii that sections use are passed as parameters, and the control a section builds for the
assembly is returned. Fields are assigned in the same order as before.

A multiset comparison of the trimmed lines of the file before and after shows nothing removed; the only additions are
the eight method headers, their braces, the eight calls and the eight `return` lines.

## Verification

Android compile check in CI (the project compiles the shared Core sources too). No device or emulator run: emulator
work is not allowed for now, so the screen itself was not looked at after the change.

## Outcome

Merged in #379 after green exact-head CI.
