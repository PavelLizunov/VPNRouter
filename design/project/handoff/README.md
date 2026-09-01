# Simple Mode — Variant A · Calm

Handoff package for DSH agents. Implement Simple mode in the VPNRouter Avalonia app using this reference and the `merge-design-handoff` skill.

## Files

| Path | What it is |
|---|---|
| `SimpleMode.html` | Reference implementation — open in a browser, flip states, toggle dark mode. This is the source of truth for layout, markup structure, and state transitions. |
| `tokens.css` | **All** design tokens — colours, typography, spacing, radii, shadows, motion. Light + dark themes. Never hard-code values — reference these variables. |
| `assets/logo.jpg` | App mascot — pre-cropped, used in the mini-header. |

## Implementation notes

### Structure (top → bottom)
1. **Mini header** — 32 px logo, brand name, three tiny state badges (VPN / Zapret / TG), `⋯` menu (holds theme, language, about, check-for-updates).
2. **Status card** — indicator dot + one-word title + one-line description. Colour of the dot is the state.
3. **Config row** — shows the currently selected config and mode as `subscribe · split`. Tapping it opens a picker. Not a placeholder dropdown.
4. **Primary CTA** — Arctic accent when connected (Disconnect), muted outline when disconnected (Connect), grey with spinner when connecting (Cancel).
5. **Advanced settings card** — links into the full app (Servers, Subscriptions, Zapret, TG proxy, Free configs).

### States
Three: `on` (Connected), `warn` (Connecting…), `off` (Disconnected). Switch by
toggling a single class on the root of the status-card / CTA (see
`data-s="on|warn|off"` in the HTML). Badges in the mini-header update to reflect
which subsystems are active.

### Tokens — how to consume
- Accent / primary actions → `--accent-solid`, `--accent-solid-hover`, `--accent-on-solid`, `--accent-fg`, `--accent-bg-subtle`, `--accent-border`.
- Surfaces (low → high elevation): `--surface-app`, `--surface-sunken`, `--surface-raised`, `--surface-base`, `--surface-overlay`.
- Text: `--text-primary`, `--text-secondary`, `--text-muted`, `--text-inverse`.
- States: `--success-*`, `--warning-*`, `--danger-*`, `--info-*` (each has `-bg`, `-border`, `-fg`, `-solid`).
- Shadows: `--shadow-xs`, `--shadow-sm`, `--shadow-md`, `--shadow-lg`.
- Spacing: `--space-1` … `--space-10` (4 px grid, with `space-1` = 2 px).
- Radii: `--radius-xs|sm|md|lg|xl|pill`.
- Type sizes: `--fs-2xs` (9) … `--fs-4xl` (28) / `--fs-hero` (40). Base `--fs-sm` = 11 px.

Dark theme activates via `data-theme="dark"` on `<html>`. All tokens re-resolve
automatically — no per-component dark overrides needed.

### Don't
- Don't re-introduce a red destructive CTA for "Disconnect". It's a benign
  toggle, not a destructive confirm.
- Don't show 3 large status badges in the header — they duplicate the main
  status card. Use the tiny chip versions only.
- Don't leave "Change config or mode" as an empty placeholder. Show the current
  value inline.

### What to tell the model
> Reproduce `SimpleMode.html`'s window 1:1 in the Avalonia app as the Simple
> mode view. Wire the three states to the real VPN state. Keep the class names
> as hints; use the tokens from `tokens.css` via the theming system.
