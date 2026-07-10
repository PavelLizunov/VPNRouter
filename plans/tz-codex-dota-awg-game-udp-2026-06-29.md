# ТЗ (Codex): Dota 2 не подключается к матчу через VPN (AWG / game UDP)

## Симптом (от пользователя)
С AmneziaWG не работает Dota 2 — не подключает к матчу. Диаг:
`C:\Project\logs\AWG.zip` (VPNRouter v2.45.0-r6, Windows 11, Connected=True).

## Важная находка (проверить в первую очередь!)
В диаге `AWG.zip` файл `current.redacted.json` — это НЕ AmneziaWG, а
**VLESS (proxy) + Hysteria2 (proxy-udp)**:
- TCP → `proxy` (VLESS+Reality, 93.95.226.167:443),
- **UDP → `proxy-udp`** (Hysteria2, 93.95.226.167:8444, salamander obfs),
  через route-правило `{ "network":"udp", "action":"route", "outbound":"proxy-udp" }`.
- `route.final: "proxy"`, full-tunnel.

То есть на момент снятия диага пользователь был на подписочном VLESS+Hy2-сервере,
а не на AWG. Поэтому ПЕРВЫЙ шаг: определить, на каком транспорте реально падал Dota:
- поискать AWG-сессию (`endpoint/wireguard[proxy]`) в `singbox-old-tail.log` (~4.9 МБ)
  и в более ранних `vpnrouter2026*.log`;
- если AWG-сессии в диаге нет — значит проблема воспроизводится и/или на VLESS+Hy2,
  и «AWG» в названии — ярлык, а не транспорт. Зафиксировать это явно.

## Контекст (важно)
Это класс «realtime game UDP через туннель», уже разбирался для Roblox (Error 277):
см. memory `roblox-277-rca` + `plans/goal-roblox-dns-singbox-1.13.14-2026-06-27.md`.
Суть: игровой realtime-UDP плохо переживает VLESS-over-TCP (HoL-блокировка) →
лечится UDP-native транспортом (Hysteria2). Здесь UDP уже идёт через Hy2 (`proxy-udp`)
— значит для Dota нужно понять, ПОЧЕМУ именно matchmaking/connect-to-match не
проходит, несмотря на UDP-native путь.

## Задача
Найти причину, почему Dota 2 не подключается к матчу, и предложить/реализовать фикс.
Проверить гипотезы:
1. **Какой транспорт нёс игровой UDP** в момент сбоя (AWG full-tunnel vs Hy2
   `proxy-udp`), и доходил ли UDP до игровых серверов Valve (в логах — outbound к
   IP/портам Dota; Source 2 использует UDP 27015-27068 + matchmaking).
2. **MTU/фрагментация**: TUN mtu=1337; для AWG endpoint mtu=1280 + junk-пакеты
   (overhead). Hy2 — QUIC поверх UDP. Не режется ли крупный игровой/handshake-пакет?
3. **Маршрутизация Dota direct vs proxy**: не попадают ли игровые/Steam-IP в
   geo-RU/`route_games_direct`-ветку (см. предупреждение в roblox-277-rca:
   `route_games_direct` Codex когда-то добавлял → REVERT; проверить, не активна ли
   подобная логика, отправляющая игровой UDP direct, где RU TSPU его морозит).
4. **DNS/Steam**: резолв matchmaking-доменов Valve (через `vpn-dns`/proxy) —
   не таймаутит ли (был класс DNS-loopback при мёртвом туннеле).
5. **Hysteria2 Brutal-калибровка** (`hysteria_up_mbps`/`down`): не задушен ли UDP
   неверным bandwidth-ceiling (см. `VlessServerEntry.HysteriaUpMbps` коммент).

## Где смотреть
Диаг `AWG.zip`: `current.redacted.json`, `singbox-tail.log` (24 КБ),
`singbox-old-tail.log` (4.9 МБ), `vpnrouter2026*.log`. Код:
`ConfigGenerator.cs` (UDP-route на `proxy-udp`, AWG-endpoint, `route_games_direct`
если есть, geo-RU rules), `BuildRoute`, Hy2-outbound, DNS-блок.

## Deliverable
1. Чёткий ответ: на каком транспорте падал Dota + где именно рвётся
   connect-to-match (с `file:line`/лог-доказательством).
2. Root cause + предложение фикса (route/MTU/DNS/Hy2-калибровка). Реализовать
   только если фикс ясный и безопасный, с тестами; спорное — как находка.
3. Не ломать: AWG v2.45.0-r6, Roblox-фикс (UDP→Hy2), full-tunnel.
4. Если нужна live-проверка с реальным Dota — это на стороне пользователя
   (у Codex нет игрового клиента); описать, что именно пользователю проверить.

## Не трогать
AWG-ядро (v2.45.0-r6) + `tools/build-singbox-lx.ps1`.
