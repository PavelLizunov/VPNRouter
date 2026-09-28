# APP — Applications routing (Include / Exclude / Full Tunnel)

Contract for the per-app routing policy. Source theory:
`plans/user-interaction-boundaries-and-edge-case-verification-framework-2026-06-02.md`.
Audit basis: `plans/applications-page-audit-2026-06-01.md`.

**User intent:** choose which applications go through the VPN — route only
selected apps (Include), route everything except selected apps (Exclude), or
route everything (Full Tunnel).

**Platform parity (B7):** `adapter` — same modes/semantics; desktop uses
process basenames + Explorer shell verbs (v2.38), Android uses package names /
its own app picker.

**Why this is high-risk:** APP is the dominant *leak-from-intent* surface — the
gap between what the UI shows selected and what the generated sing-box config
actually routes. The v2.39 `#147` fix was exactly this class (custom-JSON mode
ignored the policy: Exclude inverted, Full Tunnel could leak direct).

## State model

```
mode:    APP.mode.include | exclude | full
runtime: APP.runtime.vpn-stopped | vpn-running-clean | vpn-running-pending | applying
```

The effective routing is computed identically in both config paths:
- **Generated mode:** `ConfigGenerator.BuildRoute` / `BuildDns`
  (`isExcludeMode`, `isFullTunnel`, app-list = `RoutingAppsExclude` |
  `RoutingAppsInclude`-or-legacy).
- **Custom-JSON mode:** `CustomConfigInjector.Inject` now mirrors that exactly
  (#147 + the dns.final follow-up).

Resulting config polarity (the single source of truth both paths obey):

| Mode | per-app route | route.final | per-app DNS | dns.final |
|---|---|---|---|---|
| Include + split | listed → proxy | direct | listed → remote | local |
| Exclude + split | listed → direct | proxy tag | listed → local | remote |
| Full tunnel | (none) | proxy tag | (none) | remote |

## Action catalog

| Action ID | Intent |
|---|---|
| `APP.MODE.SET` | choose Include / Exclude / Full |
| `APP.PRESET.TOGGLE` | enable/disable a prepared app category |
| `APP.CUSTOM.ADD / REMOVE` | add/remove one process |
| `APP.CATEGORY.ADD / REMOVE` | add/remove a custom category + its routing effects |
| `APP.APPLY` | make pending routing changes active |
| `APP.SHELL.ADD / REMOVE` | mutate the routing list via the Explorer context menu (desktop, v2.38) |

## Invariants (feature-specific, on top of G1–G10)

- A visibly-removed app has **no hidden routing rule** (the r6 `89fb833` scrub).
- Include and Exclude lists stay **independent**; switching mode never
  silently reinterprets the other list.
- sing-box `process_name` **casing preserved** (no `ToLowerInvariant`).
- Manual input, executable picker and shell verb share **one normalization**
  (basename; `.lnk`→target via `ShortcutResolver`).
- Presets retain scanner patterns + child-process expansion (runtime-expanded).
- If VPN is running, the UI **truthfully** distinguishes persisted-but-pending
  from active-runtime routing (B2, G3).
- Full Tunnel: app selection is **ignored** and the UI says so (verified live in
  v2.39-r7 MCP pass: "Full-tunnel mode is active. App selection is ignored").

## Implementation state vs contract

| Contract point | State |
|---|---|
| Generated + custom paths agree on polarity (G1) | done — #147 mirrored ConfigGenerator; 31 tests + sing-box check across modes |
| Exclude not inverted; Full Tunnel no direct leak (G1) | done — #147 |
| DNS follows traffic (no DNS leak in full/exclude) | done — #147 dns.final follow-up |
| Removed app fully scrubbed (no hidden rule) | done (Windows) — `89fb833`; **macOS/Linux `TryRemoveProcessName` is `.exe`-gated → no-op**, and the inactive list isn't scrubbed (active-list scrub still works) — see B2 gap |
| Shell verbs refuse in Exclude mode (no leak) | done — v2.38 r6 |
| Apply during `V.starting`/`stopping` blocked/deferred (B2) | **GAP — verify**: APP-010 not pinned. Mirror the FC #5 decision (block lifecycle-affecting Apply while starting/stopping). |
| Persisted-vs-runtime truthfulness when VPN running (B2, G3) | partial — need explicit "pending vs active" status audit |

## Seed scenarios

| ID | Action | Boundary tuple | Expected | Layer |
|---|---|---|---|---|
| `APP-001` | Remove app | `R.include + V.running` | hidden proxy route removed after Apply | L1 |
| `APP-002` | Remove app | `R.exclude + V.running` | hidden direct-exception removed after Apply | L1 (done) |
| `APP-003` | Remove category | `R.exclude + selected children` | all child effects scrubbed | L1 |
| `APP-004` | Add manual | `I.valid=Discord.exe` | basename preserved | L1 |
| `APP-005` | Add manual | `I.valid=full path` | normalized to basename | L1 |
| `APP-006` | Add manual | `I.blank / malformed / folder` | rejected, actionable feedback | L1/L3 |
| `APP-007` | Toggle preset | `scan-pattern child launches later` | runtime-expanded process in generated config | L1 |
| `APP-008` | Mode switch | `include + exclude selections` | each mode restores its own list | L1 |
| `APP-009` | Shell remove | `case variant` | entry scrubbed without lowercasing stored name | L1 |
| `APP-010` | Apply | `V.starting` | blocked or deferred explicitly (B2) | L3 — **gap, P2** |
| `APP-011` | Custom-JSON | `exclude/full per mode` | polarity matches generated (G1) | L1 (done #147) + L2 sing-box check |

## Phase 2 candidates (queued, post-v2.39-cut)

- `APP-010`: gate Apply while VPN is starting/stopping (B2) — mirrors FC #5.
- macOS/Linux `TryRemoveProcessName` parity (scrub works cross-platform + covers
  the inactive list) — surfaced by the v2.39 adversarial review (LOW, deferred).
- Persisted-vs-runtime "pending" status audit when VPN running (B2/G3).
