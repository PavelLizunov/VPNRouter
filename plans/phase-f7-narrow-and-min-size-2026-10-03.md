# F-7: narrow window, minimum size, header buttons (tester report on r20, items 5, 6 and 7)

## Why

Tester screenshots 5-7: in a narrow window the server list shows three rows, the scrollbar overlaps the ping column and the bottom status line is cut; squeezed further the server list disappears completely (its `*` row has no minimum and the
Subscribe page has no scrolling); the header chips VPN / Zapret / TG are buttons (they open the VPN, Zapret and Telegram pages) but look like colour labels; the brand tile looks slightly raised.

## What

1. The window floor is 380 x 520 (was 360 x 360). Double click on the brand tile or the menu item "Reset window size" restores width 520 and the default height fitted to the screen.
2. Subscribe and Servers pages: the list keeps at least 170 px, the page scrolls when the window is too short (the content is at least the viewport tall, so a tall window still stretches the list as before).
3. The scrollbar gets its own 10 px gutter next to the ping column.
4. The header chips have a thin outline, hand cursor, hover and focus-ring changes; the brand tile loses its shadow; the title and the bottom status line get tooltips with the full text.

## Not covered

Shortening the connected status text itself (kept as is, many tests pin it); the Servers page custom-config tab; Android.

## Verification

`MainWindowSizeTests` (floor, reset size), the existing MainWindow/compact-tab/page suites on windows-worker at the exact SHA, UI MCP renders of Subscribe at 380/400/520 wide and 520/700 high (light and dark, EN and RU, connected, 13 servers), a real
Windows screenshot at the minimum size on WINBRAT, CI.

## Rollback

Revert the PR.

## Outcome

Pending.
