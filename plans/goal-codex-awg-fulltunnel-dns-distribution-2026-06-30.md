# GOAL (Codex): AmneziaWG / full-tunnel корректность — DNS, игровой UDP, leak-protection, дистрибуция

## Триггер
AmneziaWG заработал на Windows (v2.45.0-r6), но живые диаги вскрыли связанный
кластер проблем: общий **DNS-bootstrap loopback** (ломает резолв ВСЕХ имён через
туннель, не только игр), захардкоженные **«games-direct / game-DNS-off-proxy»
костыли**, противоречащие full-tunnel, провал **Dota game-UDP** (DNS + WSAENOBUFS),
непроверенное поведение **leak-protection под AWG**, и дыра **дистрибуции** (stable
без Android APK → 404 + конфликт подписи при апдейте). Эта goal сводит открытые ТЗ
и находки в один план. **AWG-ядро (v2.45.0-r6) и `tools/build-singbox-lx.ps1` НЕ
трогать ни в одной фазе.**

## Уже сделано в этой сессии (НЕ закоммичено, ждёт gate/push)
- **Split double-start guard**: idempotent `StartAsync`/`StartWithJson` + VM
  in-flight guard + смена сервера через `ApplyAsync(forceRestart:true)`. См.
  `plans/tz-codex-split-tunnel-crashloop-2026-06-29.md`. Root cause: два публичных
  Start поверх живого туннеля → `FATAL configure tun interface: ... already exists`.
- **RouteGamesDirect удалён** (хардкод Roblox-direct в full-tunnel) + compat-тест на
  игнор старого YAML-ключа. См. `plans/tz-codex-remove-hardcoded-fulltunnel-exceptions-2026-06-30.md` Задача 1.
- **DNS-bootstrap фикс**: `dns.servers[vpn-dns]` получил `domain_resolver="local-dns"`
  (detour="proxy" сохранён) — имя DoH-сервера резолвится без self-loop. **Gate HELD**:
  костыль game-DNS-off-proxy НЕ удалён, т.к. нет live-подтверждения «0 loopback».

## Definition of done (acceptance)
1. **DNS-bootstrap live-verified**: 0 `DNS query loopback` на РЕАЛЬНОМ туннеле, и на
   VLESS, и на AWG (резолв hostname через vpn-dns проходит). После этого —
   game-DNS-off-proxy костыль удалён чисто (флаг `ResolveGameDnsOffProxy`,
   `RealtimeGameDnsSuffixes`, ветка, UI-тумблер, `GameDnsOffProxyTests`).
2. **Leak-protection под AWG проаудичен** (StrictDns, ForceIpv4Only, FlushDnsOnStart,
   DnsLeakLockdown, MTU 1337-vs-AWG-1280): матрица применяется/работает/by-design/баг +
   фиксы реальных багов с тестами.
3. **Game UDP / Dota**: WSAENOBUFS root-caused или задокументирован (P2); Dota
   перепроверена ПОСЛЕ DNS-фикса.
4. **Full-tunnel hygiene**: подтверждено, что НИ один хардкод route/DNS не уводит
   трафик мимо туннеля сверх осознанных `BypassRussianTraffic` + QUIC-reject.
5. **Дистрибуция**: Android APK build+sign вшит в cut-stable (stable перестают 404);
   подтверждена консистентность keystore (путь апдейта существующих юзеров).
6. Везде: `dotnet build -c Release` 0 errors; тесты зелёные (с учётом известного
   dev-box baseline: ~42 локальных фейла = ProgramData-perms / non-admin TUN-lock /
   visual-diff, НЕ регрессии); 0 регрессий AWG-ядра. Gate-before-delete: по каждой
   фазе показать план/диф и дождаться подтверждения перед массовым удалением UI/флагов.

## Фазы

### Phase 0 — приземлить in-flight работу
Получить live DNS-подтверждение (Phase 1.1), затем закоммитить + запушить в ОБА
remote (`origin`=github, `forgejo`). Pre-push hook проверяет CI предыдущего коммита.
После пуша — дождаться зелёного Linux `dotnet test` CI на новом коммите (rule #15).

### Phase 1 — DNS-корректность (keystone)
1.1. **Live-verify bootstrap**: на реальном туннеле (dev-бокс/windows-brat через
   WinRM напрямую — `Import-Clixml .testpc-cred-192.168.0.106.xml` →
   `New-PSSession 192.168.0.106`, НЕ через `testvm-control` — Proxmox-API отвалился)
   прогнать конфиг с фиксом, резолвить hostname через socks (`curl --socks5-hostname
   ... https://www.google.com/generate_204`), проверить `DNS query loopback` = 0 в
   `singbox.log`. Сделать для AWG И VLESS. Если loopback остался — фикс неполный,
   чинить, костыль НЕ удалять.
1.2. После 1.1 PASS — **удалить game-DNS-off-proxy костыль** (см. tz-...-remove-
   hardcoded-fulltunnel-exceptions Задача 2): `RealtimeGameDnsSuffixes`, ветку
   `ResolveGameDnsOffProxy`, `AppConfig.ResolveGameDnsOffProxy`, UI-тумблер +
   локализацию, `GameDnsOffProxyTests`.
1.3. **Leak-protection AWG-аудит** — выполнить `plans/tz-codex-awg-dns-leakprotection-
   audit-2026-06-28.md` (матрица по 6 пунктам + фиксы явных багов). MTU: разобраться
   с TUN 1337 vs AWG endpoint 1280 (фрагментация/overhead).

### Phase 2 — игровой UDP / Dota
2.1. **WSAENOBUFS** (`failed to send data packets ... wsasendmsg: ... buffer space /
   queue full` на AWG endpoint под игровой UDP-нагрузкой) — root-cause (socket
   send-buffer в wireguard-go bind / sing-box endpoint). Если безопасного app-level
   фикса нет — задокументировать P2 в OPEN-DEFECTS (уже занесено) + предложить
   направление (увеличить SO_SNDBUF/batch, если выводимо из кода). НЕ патчить AWG-ядро
   наугад.
2.2. **Dota re-test**: после DNS-фикса (Phase 1) перепроверить connect-to-match (на
   стороне пользователя — у Codex нет игрового клиента; описать что тестеру проверить:
   заходит ли в матч; если заходит, но лагает — это WSAENOBUFS, отдельно). См.
   `plans/tz-codex-dota-awg-game-udp-2026-06-29.md`.

### Phase 3 — full-tunnel hygiene sweep
Подтвердить (grep + чтение `ConfigGenerator.cs` route/DNS-генерации), что после
удаления RouteGamesDirect + game-DNS-off-proxy НЕ осталось других захардкоженных
carve-out'ов, уводящих трафик/DNS мимо туннеля, КРОМЕ осознанных
`BypassRussianTraffic` (geosite-ru/geoip-ru → direct) и `BlockQuicOnTcpProxy`
(QUIC-reject, корректно пропускается для AWG). Эти два — НЕ трогать. Зафиксировать
краткий список «легитимные исключения full-tunnel» в комментарии/доке.

### Phase 4 — дистрибуция (Android)
4.1. **Android в cut-stable**: stable стабильно идут без APK (404 на сайте). Вшить
   шаг build-UNSIGNED-локально → upload → CI-sign (`sign-android.yml`) в `cut-stable`
   skill/процесс, чтобы каждый stable нёс `VPNRouter-vX.Y.Z-android.apk`. Учесть
   global.json: репо запинен на .NET 8, Android-сборка требует .NET 10 (`~/.dotnet10`)
   — global.json временно отодвинуть на время Android-публиша (restore в finally).
   (v2.44.1 APK уже собран+подписан+приложен вручную в этой сессии — нужен процесс,
   а не разовый фикс.)
4.2. **Keystore-консистентность**: подтвердить, что CI-секрет keystore — ТОТ ЖЕ, что
   подписывал прошлые официальные релизы (иначе существующие юзеры со старым
   официальным APK упрутся в «package conflicts» при апдейте). Если ключ менялся —
   это P0-находка (существующие юзеры не смогут обновиться без uninstall), описать
   отдельно, НЕ менять ключ.

## Не трогать / границы
- AWG-ядро (v2.45.0-r6), `tools/build-singbox-lx.ps1`, 3 build-time патча.
- `BypassRussianTraffic`, `BlockQuicOnTcpProxy` — load-bearing, не удалять.
- Stable cut — только по явной команде владельца (не autonomous).
- Gate-before-delete: показать план/диф, дождаться подтверждения перед удалением
  UI/флагов и перед каждым push.

## References
`plans/tz-codex-split-tunnel-crashloop-2026-06-29.md`,
`plans/tz-codex-remove-hardcoded-fulltunnel-exceptions-2026-06-30.md`,
`plans/tz-codex-awg-dns-leakprotection-audit-2026-06-28.md`,
`plans/tz-codex-dota-awg-game-udp-2026-06-29.md`,
`plans/OPEN-DEFECTS.md`.
