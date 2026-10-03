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

Probe test green on windows-worker (32 probe tests). With the UI MCP built from this branch the home screen renders differ from rest under `hover:Connect` (lighter button), `press:Connect` (lighter and slightly smaller), `hover:Change`, `hover:Check my setup` and `hover:VPN`
(header chip, opacity change); the Zapret/TG chips cannot be hovered in the Linux probe because they are hidden there (Zapret is unavailable on Linux). A real-cursor attempt on WINBRAT (SetCursorPos and relative mouse_event from an interactive scheduled task,
PrintWindow captures) produced frames identical to the rest frame in all four cases although `WindowFromPoint` found the app: that route does not work, use the probe.

Audit of the merged r23 code with the same binary family (`audit.py --tabs 1 --widths 380,520,1000`, scenarios default/configured/connected/connecting/alert, 2340 cells): 0 broken cells, 0 sweep failures, 2 cells with one warning (home screen, Russian, 380 px,
first-run state at 900 px height: the last row "Расширенные настройки" is clipped by 10 px by the scroll area; the page scrolls, so it is the viewport edge and not a layout defect).
