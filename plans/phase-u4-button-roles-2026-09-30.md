# U4: one role per action button and larger hit areas

## Why

The look audit (2026-09-30, see the U1 brief) found action buttons with unrelated fills: a green "Test all", a blue
"Deep verify", a green "Find working configs" and a cyan "Start VPN", so the colour said nothing about the importance of
the action, and green (the success colour) was used for a plain action. Row actions in the server and subscription lists
were about 20 to 24 dp, and the overlay close buttons 36 dp, below the 48 dp that Android recommends.

## What

- "Test all" and "Deep verify" (Servers and Subscribe footers) are tonal accent buttons (muted accent fill, accent text,
  minimum height 36 dp).
- "Find working configs" is the accent primary button (48 dp minimum height); green stays for the success state.
- Row action icons (edit, refresh, remove) in the subscription list have a 40 dp minimum hit area; the per-row test icon
  in the server list is 40 dp and its header column follows.
- The overlay close and refresh buttons (profiles, config share, log viewer) are 44 dp.

Styling only; no handler or logic change.

## Verification

- CI: Android compile and the existing suite.
- Emulator (Android 14, Linux worker, dark and light): the Servers, Subscribe and Public screens show the new roles, rows
  and footers still fit.
- Not checked: the log viewer and share overlays after the 44 dp change (same buttons, larger square).

## Outcome

Merged after green exact-head CI.
