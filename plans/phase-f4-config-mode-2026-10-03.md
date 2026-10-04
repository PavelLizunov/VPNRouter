# F-4: the home card shows the server the tunnel really uses (tester report on r20, item 2)

## Why

Tester screenshots: Advanced shows `Sweden VLESS` selected and connected, the home card says `130.94.0.171 - Server link` and the hero `via 130.94.0.171 . 155.4.244.204`. The log shows `config_mode` flipped from `subscribe` to `generated`
in config.yaml between 01:01:44 and 01:03:31 (the routing change saved from the home screen), while sing-box never contacted 130.94.0.171 (a forgotten single link, `vless.servers=1`, deleted by hand at 01:09:57). The card names
`Servers.First` in "generated" mode. `ApplyConfigModeToSettings` derives the mode from `IsVlessMode`/`IsSubscribeMode`, which are also the view state of the Servers/Subscribe tabs (selecting the Servers tab sets IsVlessMode).

## What

1. On the home screen `SaveSettings` no longer recomputes `ConfigMode` from the tab flags; the home screen changes the mode only where it sets it itself (`TryApplyVless`, `TryApplySubscriptionUrl`).
2. While connected, the card and hero name the server whose address the engine reports (`ActiveServerAddress`) when it belongs to the subscription, and the kind line says Subscription, whatever the stored mode is.

## Not covered

The Advanced tabs still switch `IsVlessMode` by viewing (needed for their panels); which exact click flipped the mode in the tester's session is not proven, the home-screen guard and the address match make both the file and the card right regardless.

## Verification

`MainWindowViewModelConfigModeTests` (routing change from the home screen keeps `subscribe`; card names the subscription), `ActiveServerMatcherTests`, MainWindowViewModel suites on windows-worker at the exact SHA, CI.

## Rollback

Revert the PR.

## Outcome

Pending.
