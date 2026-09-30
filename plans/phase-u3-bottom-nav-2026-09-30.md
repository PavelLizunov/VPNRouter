# U3: bottom navigation for the Advanced screens, settings sections as chips

## Why

The look audit (2026-09-30, see the U1 brief) found that the Advanced screens follow a desktop layout on a phone: a top
strip of five text-only tabs, a master-detail Settings page whose left menu takes about a third of the width and squeezes
the content, and two rows of navigation on the Servers page. The owner left the choice of the variant to the assistant
(the report offered: A bottom bar, B one tab row with a single scrolling Settings list, C leave); this step is a
contained form of A.

## What

- The five sections (Servers, Subscribe, Settings, Apps, Public) move from the top tab strip to a bottom bar, above the
  system gesture area and below the existing "Not connected / Start VPN" row. Each item has a 24 dp stroke icon drawn for
  this app (servers, feed, sliders, grid, globe; no third-party icon set) and a label; the active item has a tonal pill.
  Same handlers (`SelectAdvancedTab`) and content builders; the language refresh updates the label inside the item.
- The Settings page loses its 140 dp left menu: its six sections (Routing, Rules, Leak Protection, Content, Updates,
  Autostart) become a horizontally scrolling row of chips above the content, which now has the full width.
- A second bottom-bar variant with a "More" list was not needed: five items fit.

## Verification

- CI: Android compile and the existing suite.
- Emulator (Android 14, Linux worker): Servers, Subscribe, Settings, Apps and Public screens at the default font scale and
  Servers at scale 1.3 (labels still fit); the chip row scrolls horizontally ("Updates" is cut at the edge, as intended).
- Not checked: the tablet layout; two-handed reach on very tall phones.

## Outcome

Merged after green exact-head CI.
