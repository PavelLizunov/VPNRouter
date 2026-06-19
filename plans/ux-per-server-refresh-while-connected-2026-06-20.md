# UX — per-server "refresh" (⟳) buttons are dead/misleading while VPN connected

Status: **backlog** (user remark 2026-06-20, screenshot). Small UX fix, not started.

## Триггер

User (Brat NiniTux), Подписка tab, connected `[full] → Latvia HY2`: "кнопки обновить
возле конфигов бессмысленны так как они рвут соединение когда vpn включен". The
per-row ⟳ icons (one per server, right-hand column) look active but shouldn't be
used while connected.

## Симптом

Each subscription server row shows a ⟳ button. While the VPN is connected it is
still clickable but does nothing useful — and on older builds the user reports it
"tears" the connection. Either way it's a misleading control: enabled, but a no-op
(or harmful) in the connected state.

## Root cause (code state)

- Button: `VPNRouter.App/Views/Pages/SubscribePage.axaml:175-180` — `Content="⟳"`,
  `Command="…TestServerCommand"`, `CommandParameter="{Binding}"`,
  `IsEnabled="{Binding !IsTesting}"` (disabled only during an in-flight test).
- Command: `MainWindowViewModel.ServerTesting.cs:88` `TestServerAsync` runs a
  per-server TCP/TLS (protocol-aware) probe via `TcpTlsProbe.ProbeServerAsync`.
- **A guard already exists** (`ServerTesting.cs:97`, v2.38.2 "surito Bug A"):
  `if (IsConnected) return;` — skips the probe while connected so it doesn't route
  through the live TUN and overwrite cached latency with tunnel RTT.

So on **v2.38.2+** the ⟳ is a silent no-op while connected (the "бессмысленны"
the user sees). A real connection *tear* would only occur on a **pre-2.38.2** build
(probe routed through the TUN). The bug now is UX: the control stays **enabled and
clickable** even though it can't do anything useful while connected — there's no
visual signal that it's inert.

Same class applies to the batch buttons that also early-return when connected:
"Проверить все" (`TestAllServersCommand`) and "Глубокая проверка"
(`TestServerCollectionAsync`), and the equivalent ⟳ on `ServersPage.axaml:243`.

## Fix strategy

1. Gate the per-server ⟳ `IsEnabled` on **`!IsTesting && !IsConnected`** (not just
   `!IsTesting`) — `SubscribePage.axaml:179` + `ServersPage.axaml` equivalent.
   Consider the same for the batch "Проверить все" / "Глубокая проверка" buttons.
2. Add a tooltip for the disabled-while-connected state (RU/EN via `Strings`):
   "Отключите VPN, чтобы проверить сервер" / "Disconnect to test a server".
   (Keep the existing `L_TipTestTcpTls` tooltip for the enabled state.)
3. Bind to whatever the existing connected flag is (`IsConnected` / `IsRunning` —
   the one `TestServerAsync` already checks) so the disable state and the guard
   can't desync.
4. **Verify on the user's actual version** whether a real tear occurs (if they're
   pre-2.38.2 the disable fixes it; if 2.38.2+ it only removes the dead control).
   Ask the user for their version, or confirm via the next diag bundle.

Alternative (heavier): hide the buttons entirely while connected. Disable+tooltip is
preferred — the control stays discoverable, just clearly inert.

## Acceptance

- [ ] While VPN connected: per-server ⟳ is visibly disabled (greyed), tooltip
  explains why; clicking does nothing.
- [ ] While disconnected: ⟳ works exactly as today (runs the probe).
- [ ] Same for batch test buttons if they early-return when connected.
- [ ] No regression to the v2.38.2 cached-latency guard.

## Оценка

~0.5 day. Risk: LOW (XAML `IsEnabled` binding + one localized tooltip string;
no engine/lifecycle change). Quick-win candidate.

## Связь

- `VPNRouter.App/CLAUDE.md` rule D1 (localize new mini-labels), rule C1 (control
  behavior parity).
- Independent of the server-health backlog
  (`server-health-failover-backlog-2026-06-19.md`).
