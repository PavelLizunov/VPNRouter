# M-1c: hover and press steps for the UI probe

## Why

"Hover states were not checked with a real cursor" stayed on the not-verified list of every release since r16. A real cursor cannot be driven on the Windows worker (the interactive-session cursor moves, `WindowFromPoint` finds the app, but the captured frames
never differ), while the headless platform that the UI probe already uses delivers mouse moves to the real XAML. The probe only knew how to click.

## What

Steps of `ui_render`/`ui_matrix`/`ui_tree`/`ui_sweep` accept `hover:Label` (move the mouse onto the control and leave it there, so the `:pointerover` styles show in the capture) and `press:Label` (hold the left button down on it, `:pressed`). Plain labels still click.

## Not covered

Hover over several controls in one capture, drag, keyboard focus rings (a `focus:` step is the next candidate), the real Windows cursor.

## Verification

`UiProbeHeadlessTests.Steps_HoverAndPress_ChangeTheCaptureWithoutClicking` on windows-worker at the exact SHA, CI (`Build UI MCP` smoke), then renders of the header chips, the Connect button, Change and the setup row under hover.

## Rollback

Revert the PR.

## Outcome

Pending.
