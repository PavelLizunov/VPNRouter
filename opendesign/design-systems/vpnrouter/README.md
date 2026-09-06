# VPNRouter Design System

**Arctic / Glacier system** — a friendly power-user tool design for [VPNRouter](https://github.com/PavelLizunov/VPNRouter) (Virtual Penguin Network).

## What this is

A rebuild of VPNRouter's visual language around a deliberate system instead of scattered hex values. Built for density (11px base, 4px grid), warmed up with a cool Arctic blue accent and a friendly penguin mascot.

## Structure

```
tokens.css                  — semantic tokens (light + dark)
assets/penguin.svg          — mascot
assets/logo-lockup.svg      — wordmark + icon
system/
  colors.html               — palette + semantic tokens + states
  typography.html           — scale, weights, usage rules
  spacing.html              — 4px grid, radii, shadows
  components.html           — buttons, badges, inputs, tabs, banners, lists
  brand.html                — mark, principles, voice
UIKit.html                  — 520×640 main window in the new system
```

## Principles

1. **Dense, not crammed** — 11px base. Small type is a feature.
2. **Friendly surface, serious core** — round corners + mascot on top of sing-box/TUN/ETW.
3. **Status is the UI** — pill badges and dots say what's running at a glance.
4. **Arctic, not corporate-blue** — cool palette distinctive among VPN apps.

## Tokens (always use semantic, never raw hex)

- **Surfaces:** `--surface-app`, `--surface-sunken`, `--surface-base`, `--surface-raised`
- **Text:** `--text-primary`, `--text-secondary`, `--text-muted`, `--text-accent`
- **Borders:** `--border-subtle`, `--border-default`, `--border-strong`, `--border-accent`
- **Accent:** `--accent-bg-subtle`, `--accent-solid`, `--accent-fg`, `--accent-on-solid`
- **States:** `--success-*`, `--warning-*`, `--danger-*`, `--info-*` (each has bg / border / fg / solid)

## Kept from original

- Product name: **Virtual Penguin Network**
- Penguin mascot
- Split-tunnel core concept
- Header badge strip: VPN / ZAPRET / TG PROXY
- 520×640 main window dimensions
- Six-tab primary nav: Manual · Subscribe · Network · Apps · Tools · Free

## Variations

`UIKit.html` exposes two variants via Tweaks:
- **Technical** — default; monospaced meta, tight rows, subtle badges
- **Friendly** — softer shadows, rotated logo, wider rows, tinted active tab


## Clickable nav badges

The three small badges in the mini-header (**VPN · Zapret · TG**) are not just state
indicators — they are also shortcuts that navigate to the corresponding surface in
Advanced mode (Servers, Zapret, Telegram proxy). In the HTML mocks they fire
console events with `data-route`; in the Avalonia app, bind them to the same
router that the Advanced-settings card uses.
