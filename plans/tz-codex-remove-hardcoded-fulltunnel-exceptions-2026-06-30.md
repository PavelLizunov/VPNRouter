# ТЗ (Codex): убрать захардкоженные «гипотезные» исключения из генератора full-tunnel конфига

## Контекст (самодостаточно — у Codex нет памяти сессии)
В `ConfigGenerator.cs` (генератор sing-box-конфига для generated-режима) есть
захардкоженные спец-кейсы, которые роутят/резолвят конкретные игры/домены МИМО
туннеля — противоречат идее full-tunnel и основаны на гипотезах «этой игре так
лучше». Аудит 2026-06-30 выделил 4 штуки; этот ТЗ про удаление двух и замену
костыля настоящим фиксом. Два других НЕ трогать (см. ниже).

Версия ядра: AmneziaWG полностью работает с v2.45.0-r6 (Windows). AWG-ядро и
`tools/build-singbox-lx.ps1` в этом ТЗ НЕ касаться.

---

## Задача 1 — УДАЛИТЬ ПОЛНОСТЬЮ: Roblox-direct (`RouteGamesDirect`)
**Что это:** в full-tunnel хардкодом роутит игровые процессы Roblox НАПРЯМУЮ (мимо
прокси):
- `RealtimeUdpGameProcessNames` = `RobloxPlayerBeta.exe`, `RobloxPlayerLauncher.exe`
  — `VPNRouter.Core/Services/ConfigGenerator.cs:19-23`
- ветка `if (routeGamesDirect && isFullTunnel) { ... Outbound="direct" }` —
  `ConfigGenerator.cs:1903-1911`; параметр `routeGamesDirect = true` (footgun-дефолт)
  в сигнатуре `BuildRoute` — `ConfigGenerator.cs:1848`; передаётся из
  `settings.App.RouteGamesDirect` — `ConfigGenerator.cs:144`
- флаг `AppConfig.RouteGamesDirect` (default `false`) — `Models/AppConfig.cs:267`

**Почему удаляем:** route-direct НЕ чинит игровой UDP (на RU-сети TSPU морозит
foreign IP и tcp, и udp), фикс реалтайм-игр — UDP-native транспорт (Hysteria2/AWG),
а не direct. Это Roblox-only хардкод, противоречит full-tunnel, де-факто уже
reverted (toggle default-off), но мёртвая логика + список + UI остались.

**Удалить целиком и зачистить хвосты:**
- константу `RealtimeUdpGameProcessNames`, ветку в `BuildRoute`, параметр
  `routeGamesDirect`, чтение `settings.App.RouteGamesDirect`;
- поле `AppConfig.RouteGamesDirect` + любые упоминания в `SettingsMigrator`,
  `AppSettingsSane`, дефолтах;
- UI-тумблер (найти в `MainWindowViewModel*` / Settings-странице + `Localization/
  Strings.cs` + Android, если есть) — убрать тумблер и его binding/label;
- тесты `ConfigGeneratorRealtimeGamesDirectTests` (и любые ассерты на games-direct
  rule в других тестах) — удалить/поправить.

---

## Задача 2 — ЗАМЕНИТЬ КОСТЫЛЬ НАСТОЯЩИМ ФИКСОМ: DNS-bootstrap, потом убрать game-DNS-off-proxy
**Костыль (то, что в итоге убрать):**
- `RealtimeGameDnsSuffixes` = `roblox.com, rbxcdn.com, steamserver.net,
  steampowered.com, steamstatic.com, dota2.com` — `ConfigGenerator.cs:27-35`
- ветка `if (settings.App.ResolveGameDnsOffProxy) { dns.Rules.Add(... DomainSuffix=
  RealtimeGameDnsSuffixes, Server="local-dns") }` — `ConfigGenerator.cs:1055-1063`
- флаг `AppConfig.ResolveGameDnsOffProxy` (default `false`) — `Models/AppConfig.cs:239`

**Настоящий баг (root cause, из диага AWG-сессии):** DNS-сервер `vpn-dns` задан DoH
ПО ИМЕНИ (`dns.adguard-dns.com`, `detour: proxy`), а резолвить это имя некому, кроме
самого vpn-dns → бесконечная петля. В логе `singbox-old-tail.log`:
**`DNS query loopback in transport[vpn-dns]` — 7739 раз** за сессию; `dns.adguard-dns.com`
не резолвится НИКОГДА → падает ЛЮБОЙ hostname: `p2p-sto1.discovery.steamserver.net`,
`api.steampowered.com`, И `links.duckduckgo.com` (т.е. это ОБЩИЙ DNS-баг, а не игровой).
Поэтому game-DNS-off-proxy — пластырь только на 6 доменов, общий браузинг по именам
через туннель остаётся сломан.

**Что сделать (по порядку!):**
1. Найти, где генерится `dns.servers[vpn-dns]` + `route.default_domain_resolver`
   (`ConfigGenerator.cs`, DNS-блок; `Models/VPNConfig.cs` DnsSettings/SingBoxServer).
   Подтвердить точный механизм петли (что резолвит `dns.adguard-dns.com` и почему
   уходит в vpn-dns).
2. Починить bootstrap так, чтобы имя DoH-сервера резолвилось БЕЗ петли. Варианты
   (выбрать чистейший, проверить на доке sing-box 1.13):
   - задать у server[vpn-dns] свой `domain_resolver` = `local-dns` (direct), ИЛИ
   - выставить `route.default_domain_resolver`/bootstrap, который резолвит DoH-host
     через `local-dns`, ИЛИ
   - адресовать DoH-сервер ПО IP (AdGuard `94.140.14.14`/`94.140.15.15`), убрав
     зависимость от резолва имени.
3. **Проверить, что баг есть и на VLESS** (конфиг vpn-dns общий, не только AWG) —
   фикс должен лечить оба.
4. Только ПОСЛЕ того как общий резолв имён через туннель подтверждён рабочим —
   удалить костыль (Задача 2: `RealtimeGameDnsSuffixes`, ветку
   `ResolveGameDnsOffProxy`, флаг, UI-тумблер, тесты `GameDnsOffProxyTests`).
   Если bootstrap-фикс рискованный/неоднозначный — НЕ удаляй костыль в этом заходе,
   вынеси удаление отдельным шагом (главное — почини общий DNS).

---

## НЕ ТРОГАТЬ (не костыли — load-bearing)
- **`BypassRussianTraffic`** (geosite-ru/geoip-ru → direct, `ConfigGenerator.cs:197/208`,
  `AppConfig.cs:163`, default `true`) — это суть продукта (RU-трафик намеренно
  direct, туннель только для заблокированного). Осознанный toggle, НЕ удалять.
- **`BlockQuicOnTcpProxy`** (QUIC reject на TCP-only proxy, `ConfigGenerator.cs:1926-1932`)
  — принципиальная transport-логика, для AWG/UDP-native корректно пропускается.
  НЕ трогать.
- AWG-ядро (v2.45.0-r6) + `tools/build-singbox-lx.ps1`.
- `CustomConfigInjector` / free-configs — другой путь, вне этого ТЗ.

## Verification
- `dotnet build VPNRouter.sln -c Release` → 0 errors; `dotnet test` зелёный
  (с учётом известного dev-box baseline: ProgramData-perms / non-admin TUN-lock /
  visual-diff — это среда, не регрессии).
- Регенерить конфиг и проверить: (a) НЕТ games-direct route-rule; (b) при дефолтных
  настройках hostname резолвится через туннель без `DNS query loopback` (по логу
  sing-box на живом соединении — dev-бокс/windows-brat, `tools/testvm-control.ps1`);
  (c) удалённые флаги не оставили мёртвых полей/упавших тестов/осиротевшего UI.
- Сначала покажи мне по каждой задаче: точный механизм петли (Задача 2.1) + план +
  список файлов к правке, дождись подтверждения перед массовым удалением UI/флагов.
