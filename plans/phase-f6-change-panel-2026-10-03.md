# F-6 and F-8: "Change" opens a panel inside the card; the setup check is on the home screen (tester report on r20, items 4 and 8)

## Why

Tester screenshot 4: "Change" on the home screen opened a "VPN config" form above the Disconnect button, pushing the emblem and the button down; inside there was only a text field with the subscription URL, no way to pick a server. Item 8: the
"VPN setup wizard" (4 steps: routing, MTU, safe settings) is reachable only from the "..." menu, so nobody finds it.

## What

1. "Change" now opens a panel inside the connection card (below the config row): the servers of the active subscription as a list (the selected one is the one Connect uses; picking another one while connected switches the tunnel as before),
   the rule in one line ("the server you picked last if it answers, otherwise the fastest working one"), and the field to replace the link or subscription. The emblem and the button no longer move. The first-run editor (no config yet) stays above the button.
2. A "Check my setup" row in the quiet list under Autostart opens the same wizard as the menu item.

## Not covered

Showing the setup check automatically at the first run or after a failed connect (the row is the discoverable entry; auto-open needs an owner decision on interrupting the first connect).

## Verification

`MainWindowViewModelChangePanelTests`, existing home-state suites, page screenshot baselines re-rendered on the Windows worker (the quiet list has a third row), UI MCP renders of the home screen with the panel open (EN/RU, light/dark, 360-520 px), CI.

## Rollback

Revert the PR.

## Outcome

Pending.
