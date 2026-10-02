# F-1: the window and the tunnel agree (tester report on r20, items 3 and 9)

## Why

The tester's diagnostics (VPNRouter-diagnostics-20261003-011238.zip, r20) show the same chain twice (01:03:31 and 01:11:02): a routing change (All traffic / Selected apps) restarts sing-box for 5-9 s, the 2-second runtime poll sees no
sing-box process, flips the window to "Not connected", and nothing flips it back (`OnEngineStatus` ignores "Connected" while the flag is false, and `SyncConnectedWithVpnRuntime` returns for a running engine). The next press of Connect
runs `SmpToggleConnectAsync` -> `ToggleConnectionAsync`, which stops a running engine (`IsConnected || _engine.IsRunning`): the tester sees the connection "reset" and "Start does not work". Item 3 adds that the same press ran the
server pre-flight through the live tunnel (F-2).

## What

1. `ConnectionSyncPolicy` (pure): the runtime poll changes nothing during Connecting / Applying / Reconnecting, adopts a running in-process engine behind a "Not connected" window, and keeps the old grace and service rules.
2. `ToggleConnectionAsync` and the Simple Connect button: a Connect press while the engine runs shows Connected (adopt) instead of stopping; presses during Applying are ignored.
3. While a routing change is applied the home screen shows "Applying settings..." and the waiting button instead of Disconnect.

## Not covered

Server choice (F-2), firewall rule speed (F-3), config mode (F-4), apps-mode segment look (F-5). The window/engine race on the Windows service path is unchanged.

## Verification

`ConnectionSyncPolicyTests` (plain xUnit, decision matrix incl. the incident rows), existing MainWindowViewModel suites on windows-worker at the exact SHA, CI on the PR; after release a live run on WINBRAT that switches the mode 20 times
and presses Connect after each switch.

## Rollback

Revert the PR; behavior returns to r20.

## Outcome

Pending.
