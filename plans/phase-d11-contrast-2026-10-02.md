# D-11: 4.5:1 on filled accent and success buttons and on muted text (light theme)

## Why

The UI exploration server's contrast lint (M-1) reported, on every page of the light theme: white text on the accent fill `#0EA5E9` is 2.8:1 (all primary
buttons: Add, Add Server(s), Enable DPI bypass, Start & open Telegram, Next, Close), white on the success fill `#16A34A` is 3.3:1 ("Find working configs"),
and the muted text `#667287` on the panel fill `#EBEEF3` is 4.2:1 (counters, About rows, the VPN tab label, hints).

## What

`Tokens.axaml`, light theme only (the dark theme already uses dark text on the light accent): `AccentSolid` `#0276B4` (4.9:1 with white), hover `#0369A1`,
`SuccessSolid` `#15803D`, `TextMuted` `#5B6A7F` (4.7:1 on the panel fill). The lint no longer compares text height with a size that includes the margin
(false positives for "text-clipped-height").

## Verification

`PageScreenshotTests`, `PageScreenshotDesignTests`, `HeadlessGuiTests`, `UiProbeHeadlessTests`, `PictogramText*` on `windows-worker`; the two Windows
baselines that the new colour changes (`page-dpi-bypass.png`, `page-tools.png`) were re-rendered on the same worker and replaced.

## Not covered

Small touch targets (info level) and the dark theme are unchanged; Android has its own palette.
