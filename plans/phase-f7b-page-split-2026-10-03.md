# F-7b: the Subscribe page keeps its list and its controls in view (regression of F-7 in r22)

## Why

The real Windows screenshot of r22 (WINBRAT, 536x719 window) shows the Subscribe tab with the server list filling the whole page and "Test all", the subscriptions and the add form pushed below the fold. F-7 wrapped the page in a scroll viewer so the list
could not collapse, but a scroll viewer measures its content with unlimited height: the list grew to its full length (13 servers) instead of leaving room for the controls. r21 and earlier showed the controls without scrolling at this size.

## What

Subscribe: the list row takes about 60 % of the page (`3*`, at least 150 px), the controls below (test buttons, subscriptions, add form) sit in their own scrolling area (`2*`, at least 110 px). A short window never hides the list; a normal one shows
the list and the first controls without scrolling. Servers: back to the original layout with only the 170 px minimum for the list (no scroll viewer around it).

## Not covered

A shorter lower area at the default window height (the add-subscription form still needs a scroll of its own at 720 px).

## Verification

UI MCP renders of the Subscribe tab at 400x520, 520x760 and 520x1000 (13 servers), the page screenshot suites on windows-worker at the exact SHA, a real Windows screenshot of the next candidate.

## Rollback

Revert the PR.

## Outcome

Pending.
