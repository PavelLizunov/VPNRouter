# ТЗ (Codex): аудит DNS / leak-protection настроек при активном AmneziaWG-сервере

## Контекст (самодостаточный — у Codex нет памяти этой сессии)

В v2.45.0 в VPNRouter добавлена поддержка **AmneziaWG** (`awg://`), полностью
заработавшая на Windows в **v2.45.0-r6** (verified на тест-VM windows-brat: HTTPS
через туннель выходит через сервер, loc=DE). Детали ядра — memory
`awg-windows-lx-patches.md` + `VPNRouter.Core/CLAUDE.md` секция «sing-box-lx core».

Ключевое отличие AWG от VLESS на уровне sing-box-конфига:
- VLESS/Hy2/TUIC = **outbound** с тегом `proxy`.
- AmneziaWG = **`endpoints[]`** wireguard-блок с тегом `proxy` (sing-box 1.11+),
  полный L3-туннель, `allowed_ips: 0.0.0.0/0`, `route.final: "proxy"` (full-tunnel).

Пример сгенерированного AWG-конфига (DNS-часть, из реального диага):
```json
"dns": {
  "servers": [
    { "tag": "vpn-dns",   "type": "https", "server": "dns.adguard-dns.com", "detour": "proxy" },
    { "tag": "local-dns", "type": "https", "server": "1.1.1.1",             "detour": "dns-direct" }
  ],
  "rules": [ { "action": "reject", "rule_set": ["vpnrouter-adblock"] } ],
  "final": "vpn-dns",
  "strategy": "ipv4_only"
},
"inbounds": [ { "type": "tun", "address": ["172.19.0.1/30"], "mtu": 1337, ... } ],
"endpoints": [ { "type": "wireguard", "tag": "proxy", "system": false, "mtu": 1280, "address": ["10.66.0.23/32"], ... } ]
```

UI-страница «Настройки → Защита от утечек» содержит тумблеры (см. `AppSettings.App`):
`StrictMode`, `ForceIpv4Only` («Только IPv4»), `FlushDnsOnStart` («Очищать DNS кэш»),
`StrictDns` («Строгий DNS — весь DNS через VPN»), `DnsLeakLockdown` («Блокировать DNS
вне VPN»), и `Tun.Mtu` («MTU TUN-интерфейса», у пользователя 1337).

## Задача

Провести **аудит**: для каждой DNS / leak-protection настройки определить, что
именно происходит, когда **активный сервер — AmneziaWG** (а не VLESS). Цель —
понять, какие настройки (a) применяются и работают, (b) применяются, но
**избыточны by-design** при full-tunnel AWG, (c) **не применяются / ведут себя
неверно** (баг). Это **read-only аудит + матрица + предложения**; чинить только
то, что явный баг и явно безопасно (с тестами). Спорное — выносить как находку,
решение за владельцем.

## Конкретно проверить (по каждой настройке — где смотреть + что ответить)

1. **Строгий DNS (`StrictDns`)** — `весь DNS через VPN`.
   - Где: `ConfigGenerator.cs` (генерация `dns.final` / `dns.servers[].detour`),
     `HealthMonitor.cs` `ReconcileStrictDnsFailover` (failover, который флипает
     strict-DNS при недоступности proxy через Clash API).
   - Вопросы: с AWG `dns.final=vpn-dns`, `detour=proxy` — DoH-запрос идёт IP-пакетами
     через wireguard-endpoint. Резолвится ли вообще hostname DoH-сервера
     (`dns.adguard-dns.com`) — есть ли bootstrap-резолвер, или это даёт
     `DNS query loopback in transport[vpn-dns]` (наблюдалось в диаге, КОГДА туннель
     был мёртв — нужно перепроверить на ЖИВОМ AWG-туннеле)? Работает ли
     StrictDns-failover-мониторинг для AWG-endpoint так же, как для VLESS-outbound
     (Clash API health-проба по тегу `proxy`)?

2. **Только IPv4 (`ForceIpv4Only`)** — IPv6-leak protection.
   - Где: `ConfigGenerator.cs` (`dns.strategy=ipv4_only`, IPv6-обработка TUN),
     `BuildAmneziaWgEndpoint`.
   - Вопросы: AWG endpoint `address` = только IPv4 (`10.66.0.23/32`). Что с IPv6 в
     туннеле — есть ли IPv6 AllowedIPs/route, может ли IPv6-трафик утечь мимо
     AWG-туннеля (full-tunnel должен забрать всё, но проверить route + TUN IPv6)?

3. **Очищать DNS кэш (`FlushDnsOnStart`)** — `ipconfig /flushdns`.
   - Где: lifecycle (`VpnEngine`/`SingBoxManager`/startup). Proxy-agnostic.
   - Ожидание: работает одинаково для AWG и VLESS. Подтвердить, что вызывается на
     AWG-пути тоже.

4. **Блокировать DNS вне VPN (`DnsLeakLockdown`)** — блок порта 53 на не-TUN
   интерфейсах через Windows Firewall (`FirewallManager.cs`).
   - Вопросы: правило файрвола вешается на TUN-интерфейс VPNRouter. С AWG TUN тот же
     (`VPNRouter-TUN`, 172.19.0.x) — wireguard работает userspace/gVisor (`system:false`),
     а не отдельный WG-адаптер, так что lockdown должен корректно пускать DNS только
     через TUN. Проверить, что не блокирует сам wireguard-handshake (UDP/51820 к
     серверу идёт мимо TUN, по физическому NIC — НЕ должен попасть под DNS-блок,
     т.к. это не порт 53, но убедиться).
   - Известный конфликт: `LeakProtection.CollectIncompatibleSettings` уже ворнит про
     `DnsLeakLockdown + BypassRussianTraffic`. Проверить, актуален ли ворн для AWG.

5. **MTU (`Tun.Mtu`, у пользователя 1337)** vs **AWG endpoint mtu 1280**.
   - Где: `ConfigGenerator` (TUN mtu из настройки; AWG endpoint mtu — откуда берётся
     1280, хардкод или из конфига?).
   - Вопрос: рассинхрон TUN=1337 / WG-endpoint=1280 — это разные слои (внешний TUN vs
     внутренний WG payload), но возможна фрагментация / падение throughput. AWG
     overhead (WireGuard ~60 байт + junk-пакеты) — корректен ли запас? Зафиксировать,
     корректно ли пользовательский MTU 1337 сочетается с AWG, или endpoint mtu надо
     выводить из TUN mtu минус overhead.

6. **Split-tunnel vs full-tunnel при AWG** (контекст для всего выше).
   - Где: `ConfigGenerator` `BuildRoute` / `BuildOutbounds` AWG-ветка.
   - Вопрос: уважает ли AWG режим split-tunnel («только выбранные приложения») или
     всегда форсит full-tunnel (`route.final=proxy`)? Если всегда full — часть
     leak-настроек становится избыточной (всё и так в туннеле) — это by-design, но
     надо явно зафиксировать и, возможно, отразить в UI (грейать неактуальные тумблеры
     при активном AWG-сервере).

## Deliverable

1. **Матрица** (markdown-таблица) по 6 пунктам: `настройка | применяется к AWG? |
   работает? | by-design-избыточна? | баг? | file:line доказательство`.
2. Для каждого **бага** — root cause + минимальный фикс + xUnit-тест (на AWG
   config-gen, по образцу `AmneziaWgEndpointTests.cs`). Не ломать VLESS/Hy2/TUIC и
   AWG-фикс v2.45.0-r6.
3. Для **by-design-избыточных** — предложение по UX (например, дизейблить/поясняющий
   тултип на тумблере при активном AWG), но НЕ реализовывать без подтверждения.
4. Если найден реальный DNS-loopback/резолв-баг на ЖИВОМ AWG-туннеле — это P1,
   описать отдельно (live-проверка резолва hostname через туннель — на dev-боксе или
   windows-brat, см. `tools/testvm-control.ps1`; у Codex может не быть VM-доступа —
   тогда ограничиться статическим анализом + предложить live-проверку).

## Не трогать / границы

- AWG-фикс ядра (v2.45.0-r6) и патчи `tools/build-singbox-lx.ps1` — НЕ трогать.
- `LeakProtection.ValidateAppSettings` AWG-escape (v2.45.0-r2) — НЕ ломать.
- Это аудит; код-фиксы только для явных багов с тестами. Архитектурные/UX-изменения —
  как предложения.

## Где искать (быстрый индекс)
`VPNRouter.Core/Services/ConfigGenerator.cs` (DNS-блок, `BuildAmneziaWgEndpoint`,
`BuildRoute`, `BuildOutbounds` AWG-ветка), `LeakProtection.cs`
(`ValidateConfig` / `CollectIncompatibleSettings`), `HealthMonitor.cs`
(`ReconcileStrictDnsFailover`, `GenerateConfigJson`), `FirewallManager.cs`
(`DnsLeakLockdown`), `VPNRouter.Core/Models/VPNConfig.cs` (`SingBoxEndpoint`,
`DnsSettings`), `Models/AppConfig.cs` (тумблеры). Тесты: `VPNRouter.Tests/
AmneziaWgEndpointTests.cs`, `ConfigGeneratorTests.cs`, `LeakProtectionTests.cs`.
