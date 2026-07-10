# Smart Connect + diag follow-ups (2026-06-26)

## Триггер

Два пользовательских диага в один вечер:
- `VPNRouter-diagnostics-20260626-222927` (подписка `~ninitux`, v2.44.1) —
  Roblox, «часто теряется соединение».
- `VPNRouter-diagnostics-20260626-224714` (подписка `~main-brat`, v2.44.3-r2) —
  выбрана «Iceland», а трафик идёт через Германию.

## Что показал разбор (факты из логов)

1. **222927** — на стабильном сервере (Germany VLESS) обрывов нет. Обрывы
   случались, когда активным становился **мёртвый сервер**: `naive[proxy]:
   ... 213.155.15.93:443: i/o timeout`, `hysteria2[proxy-udp]: timeout: no
   recent network activity`. Это вызывало HealthMonitor restart-storm
   (`TUN ready after 13s/17s/21s, attempt 4/5/6`). Roblox-UDP сам по себе
   проксируется нормально, когда сервер жив.
2. **AutoFailover не сработал** в окне обрывов — он триггерится на Clash API
   503/504 (dead config), а dial i/o-timeout до недоступного прокси проходит
   мимо него → вместо свапа на живой сервер шёл restart-loop того же мёртвого.
3. **224714** — `auto_select_best_server: true` → `proxy` в current.json =
   `type: urltest` пул из 3 VLESS (Germany/Iceland/Netherlands). sing-box
   выбирает быстрейший = Germany `104.194.156.93`. Пользователь выбрал
   «Iceland» (`93.95.226.167`), но реально идёт через Германию. **Утечки нет**
   (DNS = AdGuard через прокси, IPv6 выключен). Проблема — UI показывает
   **имя ручного выбора + IP реально выбранного urltest-узла** → рассинхрон.
4. **Корневая проблема, общая для обоих**: Connect (и Reconnect) поднимают
   активный/первый сервер **без проверки, что он живой**. В simple-mode
   `SmpToggleConnectAsync → ToggleConnectionAsync` нет pre-connect health-probe.

## Цели (objectives)

### G1 — Smart Connect: simple-mode всегда стартует заведомо живой сервер  [HIGH]

**Проблема**: Connect садится на активный/первый сервер без проверки → можно
сесть на мёртвый (i/o timeout) → «часто теряется», зелёный фейковый «Protected».

**Цель**: при нажатии Connect в simple-mode приложение само подбирает
**рабочий и быстрый** сервер из пула, прежде чем поднять туннель.

**Acceptance**:
- Подписка, где часть серверов мертва → Connect садится на живой сервер за
  ≤ ~5 c, никогда на мёртвый (если хоть один жив).
- Все мертвы → честное сообщение («Все серверы недоступны — проверь подписку/
  интернет»), без фейкового «Protected».
- На рабочем пуле поведение и скорость Connect не деградируют заметно.

### G2 — Auto-select: статус показывает фактически выбранный сервер  [MED]

**Проблема** (224714): при `auto_select_best_server=true` шапка = имя ручного
выбора + IP реально выбранного urltest-узла. Видно «Iceland · <German IP>».

**Цель**: когда активен urltest, показывать **реально выбранный** узел
(опрос Clash API `/proxies` → `now` у `proxy`), либо метку
«Авто: <server> (быстрейший)». Имя и IP в статусе всегда про один сервер.

**Acceptance**: в auto-режиме `SmpActiveServerLine` / статус-карточка и
шапка Advanced показывают узел, через который реально идёт трафик; ручной
выбор при включённом авто помечен как игнорируемый («авто-выбор включён»).

### G3 — Subscription-refresh не рвёт туннель, если активный сервер не менялся  [MED]

**Проблема**: `SubRefresh: Servers changed, rebuilding pool and reconnecting`
делает полный reconnect (обрыв), даже если изменился НЕ активный сервер.

**Цель**: на refresh реконнектить только если поменялись параметры
**активного** сервера (host/port/uuid/protocol). Иначе — обновить пул в
памяти без обрыва (или hot-reload через Clash API без рестарта TUN).

**Acceptance**: провайдер меняет неактивный сервер → обрыва нет; меняет
активный → reconnect как сейчас. На диагах: число «Servers changed →
reconnect» падает до случаев реального изменения активного узла.

### G4 — Failover по недоступности сервера (i/o timeout), а не только Clash-dead  [HIGH]

**Проблема** (222927): сервер, который i/o-timeout’ит (naive/HY2 недоступен),
не триггерит AutoFailover (он смотрит Clash 503/504) → restart-storm того же
мёртвого вместо свапа.

**Цель**: повторные dial-таймауты / «no recent network activity» / N неудачных
рестартов подряд на одном сервере → трактовать как dead-config → AutoFailover
свапает на живой (по результатам G1-probe), а не рестартит мёртвый 5 раз.

**Acceptance**: активный сервер стал недоступен → свап на живой за ≤ ~N c,
без серии `attempt 4/5/6` с 13–21 c восстановления.

### G6 — Split-режим затрагивает direct-приложения (валидация 2026-06-26)  [MED]

**Провалидировано по коду** (ответ на вопрос user'а «может ли VPN в split влиять
на приложения, которые должны идти полностью direct?») — **ДА, влияет**:

1. **DNS-хайджек всех приложений** (`ConfigGenerator.cs:1637`
   `protocol=dns → hijack-dns`): DNS direct-приложений уходит на
   `dns.final=local-dns` = Cloudflare DoH (`:918`, `:937`), а не на системный/
   ISP/локальный резолвер. → ломает резолв **LAN/интранет-имён** (`nas.local`,
   корпоративный split-DNS, mDNS) у direct-приложений. By-design (анти-ISP-leak),
   но с реальным сайд-эффектом.
2. **TUN захватывает всё** (`:1016` `AutoRoute=true`, без `inet4_route_address`):
   direct-трафик идёт через TUN→sing-box→direct outbound → MTU 1280 + sniff 300ms
   + userspace-оверхед и для direct.
3. **Обрыв при цикле TUN** (reconnect/restart/switch/crash) рвёт и direct-коннекты.
4. **Blackhole** при зависшем sing-box с orphaned-TUN до тех пор пока HealthMonitor
   не снесёт TUN.

**Цель G6**: дать direct-приложениям корректный DNS — split-DNS-правило: приватные/
LAN-домены (`*.local`, `.lan`, RFC1918-PTR, заданные суффиксы) резолвить через
системный/локальный резолвер (`dns-direct` тип `local`/getaddrinfo), а не Cloudflare
DoH. Опционально — флаг «не трогать DNS direct-приложений». Механизмы 2-4 inherent
(не баг) — их смягчают G1/G4 (меньше лишних реконнектов = меньше обрывов direct).

**Acceptance**: в split-режиме direct-приложение резолвит LAN-имя (`nas.local` →
LAN IP) и ходит к нему; публичные домены по-прежнему через DoH (без ISP-leak).

### G5 — Глушить IPv6-DNS при IPv4-only туннеле  [LOW]

**Проблема**: при выключенном IPv6 в TUN клиенты пытаются IPv6-DoH
(`[2001:4860:4860::8888]:443`) → `address not valid in its context`, лишние
задержки резолва.

**Цель**: при IPv4-only TUN ставить DNS strategy `ipv4_only`/`prefer_ipv4`
(и/или sinkhole AAAA), чтобы IPv6-запросы не уходили впустую.

**Acceptance**: в логе нет IPv6 dial-fail на DNS; резолв не висит на их
таймауте.

## Прайор-арт: как это сделано у похожих клиентов (2026-06-26)

Свериться попросил user перед реализацией. Вывод: **наш подход — мейнстрим**,
но с 4 правками.

**Два слоя у всех зрелых клиентов:**

1. **Engine-слой (Clash/mihomo, sing-box) — непрерывный health-check ВНУТРИ
   туннеля через group-outbound:**
   - Clash `url-test`: периодически HTTP-HEAD-тестит все ноды, выбирает
     минимальный пинг; `tolerance` чтоб не флапать (tolerance=0 → дёрганье
     на шумной сети).
   - Clash `fallback`: идёт по списку **по порядку**, держится первой живой,
     переключается только когда текущая умерла. «primary, пока он жив» =
     стабильность. Это ближе к тому, что хочет наш user.
   - sing-box `urltest`: fastest-wins, `interval` 3m, `tolerance` 50ms,
     `idle_timeout` 30m (в простое delay-тест **ставится на паузу** → выбор
     может протухнуть). Нативного `fallback` у sing-box НЕТ (открытый feature
     request) → наш `AutoFailover` и есть fallback-слой поверх urltest.

2. **GUI-слой (Hiddify, v2rayN, NekoBox) — явный ping/latency-тест + авто-пик:**
   - **Hiddify «Auto»**: пингует конфиги, сортирует по результату, **коннектит
     к ноде с минимальным пингом**. Это буквально наш G1.
   - **v2rayN / NekoBox**: «Real delay» / URL-test по всей подписке (молния /
     hold подписки → Latency), мёртвые получают delay = −1.

**Прямой аналог нашего бага** — v2rayN #6633: «Multi-Server Lowest Latency
использует конфиги с delay = −1» (авто-выбор тянул мёртвые ноды). Фикс там —
**исключать −1 из пула**. Ровно наш случай (224714 urltest включал все ноды
протокола без проверки живости).

### Что это меняет в плане (4 правки)

- **R1 — G1 не новьё, а паттерн Hiddify «Auto».** Делаем уверенно, но
  переиспользуем готовый real-delay (точнее голого TCP): голый TCP-handshake
  ≠ «прокси работает». Лучше лёгкий real-delay (как Clash URL-test через каждый
  прокси) — у нас уже есть `VlessDeepVerifier`.
- **R2 — исключать мёртвые из пула (урок v2rayN −1).** В urltest-пул и в
  авто-выбор кладём ТОЛЬКО прошедшие probe ноды. Сейчас (224714) пул = все
  ноды протокола.
- **R3 — fallback-стабильность важнее fastest-wins.** User хочет «работает без
  проблем», а не «гонимся за −50 мс». sing-box `fallback` нативного нет →
  эмулируем: **высокий `tolerance`** в urltest (не флапать) + `AutoFailover`
  как fallback-слой (свап только когда нода реально умерла). Это и решает
  флап-риск, о котором предупреждает Clash (tolerance=0).
- **R4 — staleness из-за `idle_timeout`.** sing-box ставит delay-тест на паузу
  в простое (30m) → мёртвая нода не детектится, пока нет трафика. Снизить
  `idle_timeout` / держать лёгкий keep-alive probe (часть G4).

## Simple-mode Connect flow («Smart Connect»)

Принцип: **дешёвая проверка ДО коннекта** (выбрать вероятно-живой сервер) +
**авторитетная проверка ПОСЛЕ коннекта** (urltest + AutoFailover ловят
false-positive дешёвого пробинга). В simple-mode это поведение по умолчанию —
без тумблеров и ручного выбора.

```
[Connect нажат]
  |
  v
1. Собрать пул кандидатов
   - subscribe: все серверы активной подписки (после fetch, если нужно)
   - вставленная vless:// ссылка: один сервер -> пропустить probe, сразу connect
   - dedup по host:port:uuid:protocol
  |
  v
2. Быстрый liveness-probe (параллельно, дедлайн ~3-4 c)
   - переиспользовать TCP+TLS probe (FreeConfigTester / ServerTesting):
     открыть TLS до host:port, без полного подъёма sing-box
   - замер latency; помечаем pass/fail
   - UI: статус «Подбираем рабочий сервер…», CTA = Connecting
  |
  v
3. Ранжирование выживших
   - только passed; сорт по latency (asc)
   - tie-break: предпочесть протокол, дружелюбный к UDP-играм
     (Hysteria2/TUIC) если профиль = игровой/full — опционально (G1.1)
  |
  +-- нет ни одного passed ---> честная ошибка, НЕ коннектить,
  |                              «Все серверы недоступны…» (G1 acceptance)
  v
4. Построить конфиг и подключиться к победителю
   - если auto-select on: urltest-пул ТОЛЬКО из passed-серверов
     (sing-box сам держит быстрейший + переключается при деградации)
   - если off: одиночный outbound победителя
   - поднять туннель
  |
  v
5. Post-connect verify (уже есть)
   - текущий post-start probe / AutoFailover подтверждает, что туннель
     реально несёт трафик; если победитель оказался мёртв вопреки TCP-probe
     -> AutoFailover свапает на следующий по рангу (G4)
  |
  v
6. Готово: статус показывает РЕАЛЬНО выбранный узел (G2)
```

### Переиспользуем существующее

- `FreeConfigTester` (TCP+TLS fast probe) / `MainWindowViewModel.ServerTesting`
  — шаг 2.
- `VlessDeepVerifier` — опциональный «глубокий» вариант шага 2 для платных
  подписок (реальный HTTP round-trip), если TCP-probe мало.
- sing-box `urltest` (feature A) — шаг 4 как «живой» авто-выбор среди выживших.
- `AutoFailover` (feature B) — шаг 5 safety-net + G4.

### Trade-off / заметки

- Pre-connect probe добавляет ~2-4 c к Connect — приемлемо за надёжность;
  показываем «Подбираем рабочий сервер…». Можно кешировать результат probe на
  короткое окно (например 60 c), чтобы повторный Connect был мгновенным.
- Simple-mode: smart-connect = поведение по умолчанию (без тумблера).
  В Advanced — опционально (тумблер «Подбирать рабочий сервер при подключении»),
  чтобы не ломать ручной выбор конкретного узла.
- Ручной выбор конкретного сервера в simple-mode должен иметь приоритет над
  авто-подбором, если пользователь явно ткнул сервер (иначе повторяем путаницу
  224714).

## Оценка / порядок

1. **G1 + G4** вместе (один `-r`) — ядро: probe-before-connect + failover на
   недоступность. Наибольший эффект на «часто теряется». Risk: MED (трогаем
   connect path — обязательны unit + live-проверка на мёртвом сервере).
2. **G2** — отдельный `-r`, UI-only + Clash `/proxies` опрос. Risk: LOW.
3. **G3** — отдельный, требует аккуратной диф-логики «изменился ли активный».
   Risk: MED.
4. **G5** — мелкий, в составе любого `-r`. Risk: LOW.

## Implementation steps (2026-06-27, unit-test proof accepted)

Каждый increment = отдельный `-rN`, через 6-гейтовый lifecycle
(`phase-task-launcher`): бриф → ветка → код → build+tests+review(+MCP) → ship →
`post-ship-mcp-verify`. Порядок по impact; можно переставить.

### Increment 1 (rN) — G1 + G4: Smart Connect + failover-on-unreachable  [HIGH]

Цель: Connect (особенно simple-mode) садится на заведомо живой сервер; если
активный стал недоступен (i/o timeout, не только Clash-503) — свап на живой.

1. **Probe-сервис (Core).** Новый `ServerHealthProbe` (или расширить
   `MainWindowViewModel.ServerTesting`): real-delay проба кандидата через
   `VlessDeepVerifier` (точнее голого TCP — правка R1). Вход: список
   `VlessServerEntry`; выход: `{entry, alive, latencyMs}`. Параллельно, дедлайн
   ~3-4 c, кеш результата ~60 c (R-trade-off). Мёртвые (`alive=false`)
   ИСКЛЮЧАЮТСЯ (правка R2).
2. **Connect-flow (App).** В `MainWindowViewModel.SimpleMode.cs`
   `SmpToggleConnectAsync` (и в общий `ToggleConnectionAsync`): перед
   `ToggleConnectionAsync()` вызвать probe → выбрать лучший живой → выставить
   active. Нет живых → честная ошибка, НЕ коннектить (`SmpErrorText`/StatusText).
   UI: статус «Подбираем рабочий сервер…».
3. **Pool = только живые (Core).** `ConfigGenerator` / `VlessServersResolver`:
   при auto-select urltest-пул строить из probe-passed серверов, а не всех
   (правка R2). Высокий `tolerance` в urltest, чтобы не флапать (правка R3 —
   fallback-стабильность).
4. **G4 — failover-trigger по недоступности.** В `AutoFailoverEngine` /
   `HealthMonitor`: трактовать N подряд dial-таймаутов / «no recent network
   activity» / неудачных рестартов на одном сервере как dead-config → свап на
   живой (через probe), а не restart-loop. Снизить urltest `idle_timeout`
   (правка R4 — staleness).
5. **Tests:** `ServerHealthProbeTests` (alive/dead/timeout/exclude-dead/ranking);
   `ConfigGenerator` — urltest-пул содержит только живых; `AutoFailover` —
   i/o-timeout триггерит свап (фикстура). + регрессия.
6. **Verify:** MCP на windows-brat: подписка с мёртвым сервером → Connect
   садится на живой (не на мёртвый); все мёртвы → честная ошибка.

### Increment 2 (rN) — G6: split-DNS для LAN/приватных доменов  [MED]

Цель: direct-приложение резолвит LAN-имя через системный резолвер; публичные —
по-прежнему через DoH (без ISP-leak). (Юнит-тесты `SplitTunnelDirectAppImpactTests`
уже пинят текущий gap; этот increment их намеренно перевернёт.)

1. **DNS-сервер (Core).** В `ConfigGenerator.BuildDns` добавить сервер
   `dns-system` типа `local` (getaddrinfo/системный резолвер), detour `dns-direct`.
2. **Split-DNS правило.** Добавить `DnsRule` ПЕРЕД `dns.final`: match по
   `domain_suffix` (`.local`, `.lan`, `.home.arpa`, `.internal` + user-настроенные
   суффиксы) → `action:route, server:dns-system`. Публичные домены не матчатся →
   идут на `dns.final=local-dns` (Cloudflare) как раньше.
3. **(Опц.) флаг** `App.ResolveLanViaSystemDns` (default on) в настройках +
   тумблер в Network page.
4. **Tests:** обновить `SplitTunnelDirectAppImpactTests` — теперь LAN-суффикс
   резолвится через `dns-system`, публичный — через `local-dns`; добавить
   `ConfigGenerator` тест на наличие split-DNS правила + порядок (до final).
5. **Verify:** на чистой сети (не windows-brat — у неё DNS ограничен) либо на
   реальной машине: LAN-имя резолвится при VPN-on в split.

### Increment 3 (rN) — G2: статус показывает реально выбранный сервер  [MED]

1. **Core/App.** При активном urltest опрашивать Clash API `/proxies` → `now`
   у группы `proxy` (есть `ClashSingBoxApi`); прокинуть в VM как
   `ActiveSelectedNode`.
2. **App.** `SmpActiveServerLine` / `SimpleStatusDescription` / шапка Advanced:
   когда auto-select on — показывать реально выбранный узел или «Авто: <node>
   (быстрейший)»; имя и IP всегда про один сервер.
3. **Tests:** VM-тест на формирование строки в auto-режиме (имя==IP узла).
4. **Verify:** MCP — auto-select on, выбран «Iceland» → статус показывает
   фактический (Germany), без рассинхрона.

### Increment 4 (rN) — G3: refresh без обрыва, если активный не менялся  [MED]

1. **Core.** В subscription-refresh пути (`SubscriptionResolver`/`SubRefresh`):
   диф не «любой сервер изменился», а «изменились параметры АКТИВНОГО»
   (host/port/uuid/protocol). Не изменился → обновить пул в памяти без reconnect
   (или hot-reload через Clash API без рестарта TUN).
2. **Tests:** диф-хелпер — active unchanged → no-reconnect; active changed →
   reconnect. Фикстуры из диагов.
3. **Verify:** на диаге/VM — «Servers changed» при неизменном активном не рвёт.

### G5 (fold-in, LOW) — IPv6-DNS

В любой increment: при IPv4-only TUN ставить `dns.strategy=ipv4_only`
(уже есть `ForceIpv4Only`) по умолчанию когда `Tun.Ipv6Enabled=false`; убедиться
что AAAA не уходят. Tests: `BuildDns` strategy. 

## Связь

- Feature A (auto-select urltest) — `VlessConfig.AutoSelectBestServer`.
- Feature B (AutoFailover) — `AutoFailoverEngine`, `VpnEngine.ExecuteProbeFailoverRestartAsync`.
- `MainWindowViewModel.SimpleMode.cs` (`SmpToggleConnectAsync`),
  `MainWindowViewModel.ServerTesting.cs`, `FreeConfigTester`, `VlessDeepVerifier`.
