# D-22: the emblem badge is not cut off

## Why

Owner screenshot of r25 (Zapret page, dark theme): the small round badge at six o'clock under the emblem is cut flat at the bottom. `StatusEmblem` was exactly as tall as its ring (136 px) while the badge hangs 6 px below it, and the control's own bounds cut it
(a zoom into the real Windows screenshot confirms a flat bottom edge; the headless Linux render did not show it).

## What

`StatusEmblem` is 144 px tall; the ring grid stays 136 px and sits at the top, so the badge lies inside the control.

## Not covered

The home screen emblem (inline in `SimplePage`, no control around it, not cut); the Android ring badge (checked on the emulator, whole).

## Verification

UI MCP render zoomed on the badge; page suites on windows-worker; a real Windows screenshot of the next candidate zoomed at the badge.

## Rollback

Revert the PR.

## Outcome

Pending.
