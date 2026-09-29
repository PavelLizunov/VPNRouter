# H-4b: remove unreferenced Android methods

## Why

The scripted reference scan (no compiler) found Android methods whose names
appear nowhere else in tracked text. Deleting the first ten orphaned their
event handlers and helpers, so the scan was rerun to a fixed point.

## What

39 methods across 12 `VPNRouter.Android` files, 1,174 lines. Includes the unused
DPI-bypass, Telegram-tab, profile-overlay, custom-config and menu handler code
(`BuildDpiBypassTabContent`, `BuildTelegramTabContent`, `ApplyProfile`,
`BuildProfileCard`, `OnMenu*Clicked`, ...) and unreferenced `AndroidStorage`
accessors.

## Invariants and checks

- Each name occurs once in all tracked text (code, tests, AXAML, Java, scripts,
  workflows); no framework-bound attribute or interface implementation removed.
- Exact-head PR CI does not compile Android (it builds only on tags and
  `build-android.yml` rejects branch refs), and this machine has no .NET SDK.
  The Core and test builds in CI still pass, but Android compilation is NOT
  verified by this PR.

## Merge condition

Build `VPNRouter.Android` in Release with the command in
`VPNRouter.Android/AGENTS.md` on a worker with the SDK and Android workload
before merging, or accept the residual compile risk explicitly.

## Outcome

Pending.
