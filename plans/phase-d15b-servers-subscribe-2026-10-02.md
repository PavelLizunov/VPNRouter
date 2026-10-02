# D-15b: Servers and Subscribe as designed lists

## Why

The owner sees no visible change since r14; Servers and Subscribe looked the most basic (a table header over a white
box, rows of small monospace text, a bare form). Part of the D-15 redesign (a: header and tabs, c: controls, d: motion).

## What a person sees (UI MCP renders, 360 and 520 px, light and dark, English and Russian; worker captures with servers and a subscription)

| Area | Before (r18) | After |
|---|---|---|
| List frame | separate "Server IP Ping Port" text row above a plain box | one card: rounded, hairline border, a tinted header band ("Server", "Ping") inside the card |
| Server row | 11 px monospace name, host and IP in columns, coloured ping text | 12 px semibold name (trimmed with a tooltip; long names no longer run into the next column), protocol and use-case chips, `host:port` in muted monospace, a ping pill coloured by result (green ok, amber slow, red failed, grey not tested), 30 px icon buttons (retest, delete with a red hover) |
| Active / selected row | pale tint | active row on the accent tint with an accent radio mark and accent name; the selected row has an accent outline; hover tint |
| Empty Servers | one grey sentence | accent icon disc, "No servers yet", one sentence with the accepted link types |
| Empty Subscribe | "No servers / Add a subscription below" | accent icon disc, "No subscriptions yet", what a subscription is and what to do |
| Add server form | box and two plain buttons | a form card: full-width link box, "Remove" in the soft danger role on the left, "+ Add Server(s)" solid on the right |
| Subscriptions | grey rows "url · 0s · —" | cards: name, a "N servers" chip, url, last refresh (hidden when never), refresh and delete icons |
| Add subscription form | Name, URL, Add squeezed in one row | a form card "Add subscription": Name, then URL with "+ Add" beside it |
| Test buttons | 10 px | 11 px semibold; "Deep verify" tonal next to the solid "Test all" (Subscribe) |

View-model additions: `ServerViewModel.IsPingGood/IsPingSlow/IsPingBad/Endpoint`, `SubscriptionViewModel.ServerCountText/HasLastRefreshed`,
labels `L_SrvEmptyTitle/Body`, `L_SubsEmptyTitle/Body`, `L_AddSubscriptionTitle`. Shared list styles live in
`Styles/Lists.axaml` (desktop only). Control names (`ServerList`, `SubList`), bindings and commands unchanged.

## Verification

- UI MCP built at the branch SHA: renders read; `audit.py --tabs 1 --widths 360,520,1000`: 468 cells, 0 warnings.
- Windows worker, exact SHA: design captures (incl. servers and subscription content states), `HeadlessGuiTests`,
  `VisualDiffTests`, `MainWindowViewModelTests`, `ServerViewModel*`.
- PR CI.

## Outcome

Windows worker (preflight CPU 3 %, 12.7 GB free RAM, 11.0 GB free disk, no dotnet or java running) at `e353b744`:
119 of 119, no baseline changed. UI audit at the same commit: 468 cells, 0 warnings. Not seen: hover with a real pointer.
