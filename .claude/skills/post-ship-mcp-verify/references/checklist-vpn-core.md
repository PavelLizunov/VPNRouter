# VPN core checklist

Use when release notes mention sing-box / TUN / subscriptions / VLESS /
VpnEngine / HealthMonitor.

## Setup

1. Window already launched (Phase 2).
2. Ensure a real subscription URL is configured (test subscription from
   `.claude_handoff.md` section "Test subscription URLs").

## Verify Subscribe tab

3. Click "Расширенные настройки" if in Simple mode.
4. Click "Подписка" tab.
5. Screenshot.
6. Expected:
   - Subscription URL field populated.
   - Server list below shows >0 entries after fetch.
   - Each server row shows Server / IP / Ping / Port columns.

## Connect via subscription

7. Select a server row (single-click highlights).
8. Click "Запустить VPN" / "Start VPN" at bottom.
9. Wait 10 seconds.
10. Status near footer should transition:
    - "Запуск..." → "Подключено [subscription] → <name> (<ip>)".
11. Screenshot.

## Verify connection (smoke)

12. Open browser, navigate to `https://ipleak.net` or similar.
13. The reported IP should be the VPN server's IP, not the user's real
    IP.

## Test Apply (hot-reload)

14. Click "Расширенные настройки" → "Сеть" → toggle "Только IPv4" (or
    any setting).
15. Click "↻ Применить" button.
16. Status should briefly show "Применение..." then return to
    "Подключено" within 1-2 seconds (hot-reload via Clash API).

## Stop

17. Click "Остановить VPN" / "Stop VPN".
18. Status returns to "Не подключено".

## Per-feature log checks

| Looking for | Pattern |
|---|---|
| Engine start | `[VpnEngine] Connected` |
| sing-box spawn | `[SingBoxManager] sing-box started (PID NNNN)` |
| TUN ready | `[VpnEngine] TUN ready after Nms` |
| Subscription fetched | `[SubscriptionFetcher] Fetched N servers` |
| VLESS resolved | `[VlessServersResolver] Aggregated N server(s)` |
| Health check OK | `[HealthMonitor] sing-box healthy` |
| Hot-reload | `[SingBoxManager] Hot-reload via Clash API succeeded` |
| Stop clean | `[VpnEngine] Stopped` |

## Expected log noise

- `[VpnEngine] Full-tunnel mode — ignoring ActiveProfile` — expected
  when routing_mode=full.
- `[HealthMonitor] Restarting in 5000ms (attempt 1/5)` — expected on
  transient network blip.
- `[SettingsLoader] config_mode=subscribe, subs=N` — expected on every
  start.

## Critical failure patterns (real errors)

| Pattern | Meaning | Action |
|---|---|---|
| `Cannot create a file when that file already exists` | TUN orphan loop (alicemoren1991 class — fixed in v2.35.0) | Verify version; if pre-v2.35.0 user, upgrade |
| `start inbound/tun: configure tun interface` repeating | TUN reconfig crash | Check TunAdapterDiagnostics + Restart path |
| `proxy outbound missing` | Silent VLESS leak (v2.28.1 class) | Reload subscription / check ConfigGenerator |
| `Reality handshake failed: flow mismatch` | Wrong vless.flow / xtls-rprx-vision missing | Check current.json outbound |

## Pass criteria summary

- Subscription syncs and shows servers.
- VPN connects within 30 seconds.
- Browser IP changes to VPN server's IP.
- Apply (hot-reload) works without disconnect.
- Stop cleanly returns to "Не подключено".
- Log shows expected lifecycle.

## Screenshots to attach

- `tmp-rN-subscribe-tab.png` — server list populated.
- `tmp-rN-connecting.png` — "Запуск..." state.
- `tmp-rN-connected.png` — "Подключено [...]" state with server name.
- `tmp-rN-ipleak.png` — browser showing VPN IP.
- `tmp-rN-stopped.png` — clean stop state.
