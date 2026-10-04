# D-20: one segmented control on every page (follow-up of D-19)

## Why

After D-19 the Zapret/Telegram/Tools pages use the segmented pill, while the Servers and Public pages still had small chips and the Applications routing-mode strip had square segments. One look for the same control across the app (the owner's "look at everything once more").

## What

Servers (Servers / Custom Config), Public (Search / Saved) and Tools (done in D-19) use `ListBox.hk-segs` with an even-width panel; the Applications mode strip uses `hk-seg-track` and `hk-seg` radio buttons (its own square styles are removed; the `apps-mode-seg` class stays as the test hook).

## Not covered

The Settings left menu and the Applications category list (they are menus, not tab strips); Android.

## Verification

Page/probe/apps suites on windows-worker at the exact SHA (the real-click segment test of F-5 must still pass), UI MCP renders of Servers, Public and Applications, the audit matrix for those surfaces, CI.

## Rollback

Revert the PR.

## Outcome

Pending.
