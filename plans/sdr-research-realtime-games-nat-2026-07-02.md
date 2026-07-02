# Research: Steam Datagram Relay через single-exit VPN из РФ — root cause, план фиксов, протокол верификации

**Дата**: 2026-07-02. **Исполнитель**: Claude Fable 5 (4 параллельных research-агента:
SDR-протокол / sing-box internals / exit-NAT / prior art + локальный лог- и код-анализ).
**Бриф**: `plans/goal-fable-realtime-games-sdr-nat-2026-07-01.md`.

Условные пометки: **[ФАКТ]** — подтверждено первичным источником или нашим логом
(цитата/ссылка приложена); **[ВЫВОД]** — инференс из фактов; **[ГИПОТЕЗА]** — требует
live-проверки.

---

## TL;DR (одним абзацем)

SDR **не требует full-cone NAT** ни на клиенте, ни на exit'е: Valve по умолчанию
пингует **каждый relay с отдельного локального сокета**, ответ приходит **с того же
IP:port**, куда ушёл запрос — это переживает любой consumer-NAT, включая symmetric.
Фикс r10 (`endpoint_independent_nat=true`) — **no-op** (опция парсится и никуда не
передаётся — мертва в sing-box с 1.11; TUN-слой sing-box и так full cone по
построению) и решает несуществующую проблему — red herring. Единственные
задокументированные провалы Dota сняты (а) на конфиге r6-эры с **мёртвым vpn-dns**
и MTU-инверсией, (б) все — на AWG: «падает одинаково на трёх транспортах» лог-базы
не имеет. Самый подтверждённый механизм: **SDR-burst (330+ проб/мин) душит
единственный физический AWG-сокет — WSAENOBUFS начинается в ту же минуту, что
burst, и wireguard-go дропает пакеты без ретрая** (H1); альтернативы — exit-VPS
anti-abuse (H2) и хвост DNS-каскада (H0). Отдельно: **TUN MTU 1280 (r8)
гарантированно сломает подключение к матчу** даже после оживления пингов — SDR шлёт
до 1300 байт payload без PMTUD (лечится MTU ≥1332, целевое 1420). Дальше — точный
протокол верификации (одна строка в консоли Dota классифицирует стадию провала за
минуту; tcpdump-вилка на exit'е) и приоритизированный план фиксов по слоям.

---

## 1. Как реально работает SDR (packet-level) — [ФАКТ]

Все пункты подтверждены первичными источниками: официальные docs
(partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay), строки из
боевого бинаря Dota (SteamDatabase/GameTracking-Dota2
`steamnetworkingsockets_strings.txt`), публичные хедеры GameNetworkingSockets
(`steamnetworkingtypes.h`), protobuf (SteamDatabase/Protobufs
`steamdatagram_messages_sdr.proto`), живой API-эндпоинт.

1. **Сначала HTTPS, не UDP**: клиент скачивает "network config" (~26 KB JSON) с
   `https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=570` через
   ISteamHTTP Steam-клиента. В нём — 50+ PoP'ов с конкретными relay-IP и
   port_range (`[27015,27060]`/`[27015,27140]`), сертификаты, матрица
   `typical_pings` PoP↔PoP. Обновляется ~раз в час.
2. **Пинг — application-level UDP** (`RouterPingRequest`=msg 1 /
   `RouterPingReply`=msg 2, protobuf), к relay-IP из конфига на порты
   **UDP 27015–27140**. ICMP не участвует. STUN (3478/4379/4380) — только для
   Steamworks P2P/voice/Remote Play, НЕ для region ping Dota.
3. **По сокету на relay** (ключевой факт): «By default, we open up a new UDP
   socket (on a different local port) for each relay. This is slightly less
   optimal, but it works around some routers that don't implement NAT properly»
   (`k_ESteamNetworkingConfig_SDRClient_SingleSocket`, steamnetworkingtypes.h).
   Наш лог это воспроизводит 1-в-1: **330 packet-сессий = 330 уникальных
   src-портов, каждый ровно к одному relay** (diag 20260701-122336, burst
   11:31).
4. **Ответ приходит с того же IP:port**. Демультиплексирование по source-адресу,
   ответы валидируются challenge'ом против per-request состояния
   («Ignoring unsolicited/spoofed/late packet from %s»), незнакомые relay
   отбрасываются по whitelist из конфига. Механизма «relay B отвечает за relay A»
   не существует ни в одном публичном артефакте. Cross-relay reply — [ВЫВОД,
   высокая уверенность] — не существует; каждая NAT-требовательная часть keyed
   на пингуемый адрес.
5. **Пинг гейтится Steam-сертификатом** (нужен логин в Steam) и network
   config'ом: «No signed cert? We cannot probe relays without a cert»;
   «Pending ping measurement until network config is obtained».
6. **Не все PoP пингуются напрямую**: `SDRClient_LimitPingProbesToNearestN` —
   дальние регионы оцениваются через `typical_pings`-матрицу от ближайших.
   [ВЫВОД] «ОШИБКА на ВСЕХ регионах» = провал пинг-таблицы целиком (0 valid
   relays), а не N независимых таймаутов.
7. Результат уходит в матчмейкинг как `CMsgClientPingData` с
   `region_ping_failed_bitmask` — при пустой пинг-таблице матчмейкинг мёртв.

### Требование SDR к NAT — [ФАКТ+ВЫВОД]

Раз (3)+(4): каждому relay — свой сокет, ответ same-address, — то для region ping
достаточно **любого** NAT, который пропускает обычный UDP request/reply
(port-restricted и symmetric включительно). Full-cone НЕ нужен. Подтверждение
калибровкой: CS2/TF2 matchmaking работает через AirVPN WireGuard (не full-cone;
чинился MTU-фиксом); ProtonVPN по умолчанию отдаёт symmetric-подобный «Strict
NAT» и это работает для mainstream-матчмейкинга; RU/UA-вспышка «Задержка: не
удалось вычислить» (2020) массово ЧИНИЛАСЬ включением обычного consumer-VPN.
Публичных свидетельств, что Valve-релеи отвергают datacenter/VPN-IP, не найдено
(искали прицельно). Требование «public IP, принимающий unsolicited-трафик» в
доках Valve относится ТОЛЬКО к dedicated-серверам, не к клиентам.

---

## 2. Критический разбор r10 full-cone фикса — red herring

### 2.1 На system-стеке опция — no-op — [ФАКТ, исходники]

Проверено ДВАЖДЫ независимо: (а) локальные исходники sing-tun v0.8.11 из Go
module cache; (б) research-агент склонировал ТОЧНО отгружаемый форк
`Leadaxe/sing-box-lx @ c7a2592e` (v1.13.13-lx-awg, наш build-скрипт) со всеми
его pinned-зависимостями (sing v0.8.10, sing-tun v0.8.10):

- В lx-форке `option/tun.go:62` — **единственное** вхождение
  `EndpointIndependentNat` во всём дереве: опция парсится из JSON и никуда
  не передаётся. Конструкция `tun.StackOptions{...}`
  (`protocol/tun/inbound.go:378-389`) поля EIN **не имеет** — как и
  `StackOptions` в sing-tun v0.8.10/v0.8.11.
- Опция мертва в mainline с sing-box 1.11: коммит sing-tun `9bcc1ec`
  (2024-10-23) удалил её потребление; исторически она включала
  per-4-tuple-flow режим ТОЛЬКО на gVisor-пути. System-стек с самого первого
  коммита (2022-09-06) — source-keyed udpnat.
- `stack_system.go:164`: `udpnat.New(handler, prepare, timeout, false)` —
  system-стек использует `sing/common/udpnat2`, а тот ключует NAT-таблицу
  **только по source**: `cache freelru.Cache[netip.AddrPort, *natConn]`,
  lookup по `source.AddrPort()` (`service.go:17,54`), **и не имеет никакой
  inbound-фильтрации** (любой ответ, дошедший до outbound-сокета сессии,
  доставляется приложению). То есть TUN-слой sing-box — full cone (EIM+EIF)
  по построению, всегда, на обоих стеках (gVisor-путь через тот же udpnat2,
  `stack_gvisor_udp.go:41`).
- Документация sing-box (tun) отстала, но в главном честна:
  «…other stacks are endpoint-independent NAT by default».
- Community-ceiling: живые замеры дают sing-box TUN максимум
  PortRestrictedCone (sing-box#261) — ограничение вносят внешние слои, и
  этого для SDR достаточно (см. §1).

### 2.2 Проблемы, которую r10 решает, не существует — [ФАКТ]

Гипотеза r10 («SDR получает ответ с ДРУГОГО адреса, чем слал, и sing-box с
keyed-by-(src,dst) дропает cross-relay reply») опровергнута с двух сторон:
(а) SDR не шлёт cross-relay ответов (§1.4); (б) сессии и не ключуются по
(src,dst) на system-стеке (§2.1). Плюс лог: пинги идут per-socket — «тот же
сокет, другой адрес» в ping-фазе не возникает в принципе.

### 2.3 Что с этим делать

`EndpointIndependentNat=true` безвреден (и станет полезен, если когда-нибудь
переключимся на gVisor-стек) — **оставить значение, но переписать комментарий**
в `ConfigGenerator.cs:1135-1149`: текущий утверждает ложный механизм и однажды
кого-нибудь снова уведёт в full-cone кроличью нору. Если тестер доложит «на r10
заработало» — НЕ засчитывать это r10: см. §5 (эффект почти наверняка от
r8-DNS/MTU-фиксов, впервые опробованных вместе с r10).

---

## 3. Что показал наш собственный диаг (r6-эра, единственный залогированный провал) — [ФАКТ]

Диаг `VPNRouter-diagnostics-20260701-122336.zip` (v2.45.0-r7 binary; лог-окно
01:38–12:23, Dota-тест 11:30–11:38 шёл на прежней сессии sing-box со **старым
конфигом r6-эры** — рестарт с r7-конфигом виден только в 12:20):

1. **vpn-dns был мёртв весь тест**: тысячи `DNS query loopback in
   transport[vpn-dns]`; `api.steampowered.com` не резолвился через sing-box
   (11:28:47–11:30:48), `p2p-*.discovery.steamserver.net` — 100 фейлов
   (11:28:59–11:33:02). После рестарта в 12:20 — резолвы ожили.
2. **Параллельно системный DNS утекал мимо туннеля** на RU ISP-резолверы
   (находка B handoff'а, скриншот browserleaks: 195.2.238.4/… «Trytek LLC»,
   `dns_leak_lockdown: false`) — поэтому у приложений «какой-то DNS» был, и
   network config Dota, судя по всему, получила (пинг-burst пошёл по
   конкретным relay-IP).
3. **SDR ping burst состоялся и ушёл в туннель**: ~538 packet-сессий/мин в
   пике; 330 уникальных src-портов → 61+ уникальных relay-адресов Valve
   (155.133.x / 162.254.x / 146.66.x / 185.25.x, порты 27016–27127), все
   через `endpoint/wireguard[proxy]`, отправка без ошибок в этом окне.
   Steam CM TCP (27019–27025) — работал. Ответы на пинги в INFO-логе не
   видны by design (sing-box не логирует UDP-ответы) — «не видно» ≠ «не было».
4. **MTU-инверсия**: TUN mtu=1337 > AWG endpoint mtu=1280 (r8-кэп ещё не
   стоял).
5. **WSAENOBUFS — реален и коррелирует с burst'ом до минуты** (диаг AWG.zip,
   28.06, r6): первый SDR-burst 22:47 (356 сессий/мин) — и **в ту же минуту
   22:47 первые 5 ошибок** `endpoint/wireguard[proxy]: … failed to send data
   packets: write udp4 …->93.95.226.167:51822: wsasendmsg: …` (WSAENOBUFS);
   дальше 70 шт в 23:33 и 18 в 23:42 под продолжающейся game-UDP нагрузкой.
   Физический WG-сокет дропал исходящие data-пакеты локально; wireguard-go
   на send-error просто дропает батч БЕЗ ретрая (`device/send.go:639`), а на
   detour-пути ClientBind send-error вообще закрывает транспортный сокет
   (`client_bind.go:184-187`). В диаге 122336 таких ошибок нет (но там и
   burst меньше).
6. **Все залогированные SDR-бёрсты — только AWG.** Ни один диаг (29.06,
   30.06, 01.07) не содержит relay-сессий через hysteria2 или vless —
   «падает одинаково на трёх транспортах» существует только как устный
   отчёт; лог-доказательства транспорт-независимости НЕТ.
7. **Важно для методологии**: диага с провалом на r9 (после DNS/MTU-фиксов)
   НЕ СУЩЕСТВУЕТ — только устный отчёт тестера. Вся доказательная база
   «DNS уже починен, а SDR всё равно падает» держится на непроверенном
   утверждении.

---

## 4. Слой NAT: полная карта (клиент → exit → relay) — [ФАКТ]

RFC 4787-термины: EIM/EIF = endpoint-independent mapping/filtering;
full cone = EIM+EIF; port-restricted = EIM+APDF; symmetric = APDM+APDF.

| Слой | Что там сейчас | NAT-семантика | Ломает ли SDR ping (same-address reply)? |
|---|---|---|---|
| Windows-приложение (Dota/Steam) | per-relay сокеты | — | — |
| sing-box TUN, stack=system | udpnat2 keyed-by-source | EIM всегда; filtering определяется downstream | НЕТ |
| Outbound VLESS+xudp (клиент→exit по TCP) | 1 app-сокет = 1 xudp-сессия = 1 свежий TCP/Reality dial; адреса per-packet (Keep-фреймы); GlobalID sing-box не пишет (теряется только reattach после реконнекта) | full cone внутри живой сессии (exit: один unconnected сокет) | НЕТ (но см. §6 H1 про burst из ~330 dials) |
| Outbound AWG endpoint (netstack wireguard-go) | gVisor netstack форка | сокет-на-flow внутри netstack | НЕТ; но физический send-сокет — единая точка отказа (WSAENOBUFS) |
| Outbound Hysteria2 (QUIC) | sessionID per client-socket | сервер: порт-на-сессию, unconnected socket, «SHOULD assign a unique UDP port to each Session ID» | НЕТ |
| Exit: kernel WG + MASQUERADE (AWG) | netfilter conntrack | **EIM+APDF (port-restricted)**; unsolicited-от-нового-адреса → INPUT VPS (дроп); при коллизии портов деградирует к symmetric | НЕТ для same-address reply (conntrack ESTABLISHED пропустит); ДА для будущих P2P/unsolicited сценариев |
| Exit: xray VLESS-сервер | `ListenPacket` unconnected socket per session, форвардит от любого источника («cone behavior» — намеренный default) | full-cone-capable | НЕТ |
| Exit: hysteria2-сервер | `net.ListenUDP` per session, без source-фильтра | full cone by design | НЕТ |
| Host-firewall VPS (ufw/cloud) | stateful: ESTABLISHED пройдёт, NEW inbound UDP — дроп | режет только unsolicited | НЕТ для ping reply; ДА для unsolicited |
| Valve relay | отвечает с того же IP:port, валидирует challenge | — | — |

**[ВЫВОД] Ни один слой текущего стека не дропает same-address ping reply.**
Значит либо пробы не доходят до релеев, либо ответы гибнут ДО exit'а
(anti-abuse хостера / сам relay молчит), либо наблюдавшийся провал был
DNS-каскадом r6-эры. Это в точности вилка §6.

Полезные конкретики exit-слоя (пригодятся в Phase 2):
- conntrack UDP timeout: 30 s (unreplied) / 120 s (stream) — маппинг живёт
  недолго, игра должна keep-alive'ить.
- MASQUERADE сохраняет source-port «where possible» (man iptables) — на
  одно-клиентском VPS обычно EIM.
- Рецепты full-cone для WG-exit (по простоте): (a) статический DNAT
  UDP-диапазона на wg-клиента: `nft add rule inet nat prerouting iif eth0 udp
  dport 27000-27100 dnat ip to 10.8.0.2`; (b) einat-ebpf (EIM+EIF на TC-хуках,
  kernel ≥5.15, без out-of-tree kmod); (c) FULLCONENAT/nft-fullcone kmod
  (хрупко на автообновляемом ядре).

---

## 5. Почему «Задержка: ОШИБКА» на ВСЕХ регионах сразу — стадии и их сигнатуры — [ФАКТ]

Валве-стек печатает в консоль Dota (`-console` или `con_logfile`) строку,
которая **однозначно классифицирует стадию** провала:

```
[SteamNetSockets] SDR RelayNetworkStatus: avail=<...> config=<...> anyrelay=<...>
```

| Стадия | Сигнатура | Наш кейс |
|---|---|---|
| (a) network config fetch провален | `config=Failed`, «SDR network config fetch attempt #N failed… SDR functionality will not be available!», код 3004 «Don't have network config» | маловероятно СЕЙЧАС (Steam HTTPS работает), но на r6 с мёртвым DNS — возможно |
| (b) нет Steam-сертификата (логин) | «No signed cert? We cannot probe relays without a cert», avail=Waiting | нет (Steam залогинен) |
| (c) UDP-пробы гибнут (туда или обратно) | `config=OK anyrelay=Failed`; per-relay «Destroying relay '…' because initial_ping_timeout»; итог «Ping measurement completed in %.1fs. Relays: **0 valid**, …» | **главный кандидат** — отличить «туда» от «обратно» может только tcpdump на exit |
| (d) ICMP-блок | не существует — SDR не использует ICMP | — |
| (e) source-port consistency check | не существует (per-relay сокеты — у всех игроков порты разные) | — |

Известные real-world причины (c) БЕЗ VPN: router/ISP firewall режет outbound
UDP high ports; ISP-маршрутизация к релеям сломана (RU/UA 2020 — чинилось
VPN'ом). Открытый issue ValveSoftware/Dota2-Gameplay#32293 «SDR Protocol
Conflicts with VPNs in Restricted Regions (Russia)» (май 2026) — без ответа
Valve, workaround'а в треде нет.

---

## 6. Root-cause: вилка из трёх гипотез (и как её разрешить)

Констатация: полного root-cause для «SDR падает на здоровом туннеле» у нас
**нет и не может быть без одного свежего замера** — потому что единственный
задокументированный провал снят на отравленном (DNS+MTU) конфиге. Вилка:

### H0 — «на r9+ уже работает / провал был DNS-каскадом» — [ГИПОТЕЗА]
На r6: vpn-dns мёртв; Steam-инфраструктура частично на утёкшем RU-DNS;
p2p-discovery не резолвился. Не исключено, что часть SDR-пайплайна (fetch
config / cert / переранжирование) стояла именно из-за DNS, и после r8/r9 всё
уже живо, а r10-репорт тестера это покажет. **Опасность**: успех припишут
r10-фиксу (EIN) — не засчитывать без A/B (см. §7 шаг 0).

### H1 — burst UDP-проб гибнет на клиентском data-path — [ГИПОТЕЗА, приоритет; для AWG — с прямым лог-свидетельством]
~330 новых UDP-flows за секунды — самый жёсткий паттерн, который наш стек
вообще видит. Механизм на каждом транспорте СВОЙ (sing-box: 1 app-сокет =
1 сессия = 1 outbound-«соединение», но цена соединения разная):

- **AWG** — ВСЕ 330 flows мультиплексируются в ОДИН физический UDP-сокет
  (StdNetBind, по syscall'у на датаграмму, SO_SNDBUF 7MB). Под burst'ом он
  давал WSAENOBUFS, и wireguard-go **дропает батч без ретрая**
  (`device/send.go:639`). Прямая корреляция в логе 28.06: первые ENOBUFS —
  в ту же минуту, что первый SDR-burst (§3.5). Исходящие пробы гибли на
  клиенте — релеи их не видели, все регионы = ОШИБКА. **Это самый
  подтверждённый механизм из всех.**
- **VLESS+xudp** — каждый app-сокет = СВЕЖЕЕ TCP+Reality соединение к exit'у
  (`protocol/vless/outbound.go:186-215`). SDR-burst = ~330 одновременных
  TCP/TLS-хендшейков к одному IP:443 через RU-DPI — медленно (probe ждёт в
  64-пакетном pre-dial буфере), заметно для TSPU (сигнатура SYN-флуда),
  упирается в лимиты exit'а. Пробы могут не успевать до
  `initial_ping_timeout`.
- **Hysteria2** — самый лёгкий путь (sessionID поверх ОДНОГО живого QUIC,
  без новых хендшейков). Если Dota реально падает и на Hy2 — это аргумент в
  пользу H2 (exit/relay-side), а не клиентского burst'а. Но именно на Hy2
  документированного теста НЕТ (§3.6). Смежный prior art:
  apernet/hysteria#860 + sing-box#968 — «клиент sing-box (win-tun) + сервер
  hysteria2 official → работает».

Мелочь, но упомянем: session-таблица TUN — LRU на 1024; наш burst + фон её
не переполняет (окно выселения ≈ сотни секунд при нашем рейте) — НЕ
подозреваемый. NatTypeTester шлёт 1 пакет — весь этот класс burst-багов он
не видит (урок Xray #5888: «зелёный FullCone» ничего не гарантирует).

### H2 — пробы/ответы гибнут на exit-VPS или у хостера — [ГИПОТЕЗА]
Все три транспорта — один VPS (93.95.226.167, 1984 ehf, Iceland). ЕСЛИ
транспорт-независимость подтвердится (сейчас она устная, §3.6),
«транспорт-независимость» на деле может быть **exit-IP-независимостью**.
Варианты: anti-abuse/anti-DDoS хостера давит исходящий UDP-веер (330
пакетов к 60+ разным /16 на высокие порты за секунды — сигнатура сканера);
ufw/cloud-firewall (для proxy-экзитов не мешает same-address reply, но мог
бы мешать при отсутствии conntrack-записи); Valve-relay игнорирует
конкретный IP (prior art не подтверждает, но для данного privacy-хостера не
исключено). Разрешается только tcpdump'ом (§7).

### Отдельно и НЕЗАВИСИМО от вилки: MTU — [ФАКТ]
SDR шлёт UDP-payload **до 1300 байт** (`k_cbSteamNetworkingSocketsMaxUDPMsgLen
= 1300`), **без PMTUD и без адаптации**: «We currently don't have a
configurable MTU, and don't do MTU discovery» (Fletcher Dunn,
GameNetworkingSockets#22). 1300 + 28 = **1328 байт IP-пакета**. Наш r8-кэп
TUN MTU = 1280 на AWG **категорически не пропустит** такие пакеты, SNS вниз
не подстроится. Пинг-пробы мелкие и пройдут, поэтому «Задержка» может даже
показать числа — а вот подключение к матчу/геймплей на AWG умрёт. Известный
полевой кейс: CS2/TF2 matchmaking через WireGuard был мёртв ровно до MTU-фикса.
Hy2 не страдает (у протокола своя UDP-фрагментация поверх QUIC), VLESS/xudp
не страдает (stream). **Фиксить обязательно, иначе любой успех пингов упрётся
в следующий же этап.**

---

## 7. Протокол верификации (по шагам, без гаданий)

**Шаг 0 — классификация за 5 минут, без кода (тестер).**
Запустить Dota с `-console` (или `+con_logfile console.log`), открыть выбор
региона, снять строки `[SteamNetSockets]`:
- `SDR RelayNetworkStatus: avail=? config=? anyrelay=?`
- «Ping measurement completed in Ns. Relays: X valid, …»
- любые «Destroying relay … because initial_ping_timeout»
`config=Failed` → H0/DNS-класс (и смотреть singbox.log `dns:` в тот момент).
`config=OK, anyrelay=Failed, Relays: 0 valid` → чистый UDP-класс (H1/H2) →
шаг 1. Заодно фиксируем версию клиента (r10) и транспорт.

**Обязательно прогнать трижды — AWG, VLESS, Hysteria2** (по 2 минуты) — с
console-логом «одинаковость на трёх транспортах» впервые станет доказуемой
(сейчас она устная). Параллельно на AWG-прогоне: `grep wsasendmsg
C:\ProgramData\VPNRouter\logs\singbox.log` в минуту теста — если сыпет,
H1-AWG подтверждён на месте. Если Hy2-прогон ВНЕЗАПНО работает — root cause
per-transport (H1), и full-tunnel-fallback «game-UDP через Hy2» становится
самым дешёвым продуктовым фиксом.

**Шаг 1 — где гибнут пакеты: tcpdump на exit (5 минут, root на VPS).**
```bash
tcpdump -ni eth0 'udp portrange 27015-27200' -c 200   # пробы уходят? ответы приходят?
tcpdump -ni wg0  'udp portrange 27015-27200' -c 200   # (для AWG) ответы возвращаются в туннель?
conntrack -L -p udp | grep <wg-client-ip>              # маппинги; [UNREPLIED] = ответов нет
```
Матрица чтения:
- пробы НЕ появляются на eth0 → гибнут на клиенте/в туннеле → H1 (для AWG
  первым делом смотреть WSAENOBUFS в singbox.log в момент burst).
- пробы уходят, ответов на eth0 НЕТ → relay/хостер молчит → H2-внешняя:
  повторить пробу вручную С САМОГО VPS (см. шаг 2) и/или сменить exit-IP.
- ответы на eth0 ЕСТЬ, на wg0 НЕТ → conntrack/firewall exit'а — H2-локальная.
- ответы доходят до wg0, а Dota всё равно ERROR → возврат в клиентский стек
  (H1-обратный путь): pcap на Windows (`pktmon` по TUN) + Clash API
  `/connections` (download=0 на relay-сессиях = ответы не доставлены).

**Шаг 2 — изоляция relay-reachability с exit'а (вручную, без Steam).**
С VPS: `nping --udp -p 27015 155.133.248.85 --data-length 40 -c 3` (или
python-скрипт с первым байтом 0x01 = RouterPingRequest). ВАЖНО: формат
запроса закрыт (protobuf-описания request'а нет в паблике), relay имеет
право молча игнорировать мусор → тишина здесь НЕ доказывает блокировку
(false-negative), а вот ЛЮБОЙ ответ — доказывает reachability. Надёжная
дискриминация H2-внешней — только шаг 1 (tcpdump в момент реального
Dota-теста). Плюс `stunclient --mode full stunserver2025.stunprotocol.org`
с VPS (baseline EIM+EIF на публичном IP) и с клиента через туннель
(композитный NAT-тип всего пути; ожидаем port-restricted — этого достаточно).

**Шаг 3 — контролируемый A/B для закрытия H0.**
На здоровом r10-клиенте: (а) Dota-тест при vpn-dns живом (грепнуть
`dns: exchanged` на api.steampowered.com в момент теста — 0 loopback); (б)
тот же тест с принудительно затемнённым DNS-leak (dns_leak_lockdown=true) —
если (а) работает, а (б) нет, значит SDR-путь зависит от утечки — отдельный
баг. Плюс негативный контроль: `SDRClient_ForceRelayCluster fra` (консоль:
`sdr SDRClient_ForceRelayCluster fra`) — сузить burst до одного кластера и
посмотреть, оживёт ли одиночный пинг (если да — H1-burst усиливается).

**Критерий «починено»**: `Relays: N valid, N ≥ 20` на обоих транспортах + вход
в матч и 10 минут игры без «connection lost» (это уже MTU-гейт, §6).

---

## 8. Приоритизированный план фиксов

### Phase 1 — клиент (VPNRouter), можно делать сразу

1. **MTU (обязательно, независимо от вилки)**: поднять пропускную способность
   пути для 1328-байтных IP-пакетов на AWG. `AwgEndpointMtu` 1280 был
   pos-hoc страховкой r8 (реальный фикс DoH-блэкхола — plain-UDP DNS);
   WG-стандарт при 1500-WAN — 1420, AWG transport-оверхед равен WG (junk —
   только в handshake). Целевое: endpoint mtu 1420, TUN mtu = min(user,1420);
   fallback при живых дропах — 1360, но НЕ ниже 1332. Проверка: `ping -f -l
   1372 <внешний>` через туннель (1372+28=1400) + Dota-матч.
2. **Комментарий r10** (`ConfigGenerator.cs:1135`): переписать на честный
   («no-op на system stack; оставлен для gVisor; SDR full-cone НЕ требует —
   см. этот документ»). Значение `true` оставить.
3. **DNS-leak (находка B) — закрыть как P1**: она не только приватность — она
   отравляет всю диагностику игровых кейсов (H0). Минимум: включить
   `dns_leak_lockdown` по умолчанию в full-tunnel; правильнее — победить
   Windows smart multi-homed resolution (NRPT/блок 53/DoH-редирект на
   физическом NIC) отдельным треком.
4. **WSAENOBUFS на AWG-сокете — поднять P2→P1** (корреляция burst→ENOBUFS
   до минуты уже есть, §3.5; ждём только подтверждения шага 0/1, что это
   решающий, а не сопутствующий фактор): ретрай-с-бэкоффом на wsasendmsg
   ENOBUFS в lx-форке (wireguard-go bind `conn/bind_std.go:473` — сейчас
   drop-no-retry) и/или увеличение SO_SNDBUF (сейчас 7MB,
   `conn/controlfns_windows.go`). Трогает lx-core — по памяти
   awg-windows-lx-patches (не ломать 3 build-патча).
5. **Продуктовый fallback, если H1 подтвердится per-transport**: в
   full-tunnel с AWG-профилем маршрутизировать game-UDP (или всё UDP) через
   Hy2-сиблинга, как уже делается для наивных VLESS-серверов (`proxy-udp`
   route) — Hy2-путь несёт burst дёшево (sessionID поверх одного QUIC).
6. **Не делать**: gVisor-стек «для NAT», route-исключения для Steam (TSPU
   душит direct), никакого нового full-cone на клиенте.

### Phase 2 — exit-VPS (по результатам шага 1; руками владельца VPS)

6. Если ответы гибнут на exit (H2-локальная): для AWG-экзита — DNAT-диапазон
   `udp dport 27015-27200 → wg-client` (см. §4; заодно даёт полноценный
   full-cone этому диапазону) ИЛИ einat-ebpf целиком; проверить
   ufw/cloud-firewall (`ufw allow proto udp to any port 27015:27200` для
   proxy-экзитов не нужен, для WG-экзита не нужен тоже — но проверить, что
   FORWARD не режет).
7. Если релеи молчат конкретно этому IP (H2-внешняя): сменить exit-IP/хостера
   для игрового профиля (самый дешёвый тест — второй VPS у другого
   провайдера); full-cone не поможет.

### Phase 3 — игра/Steam (диагностические рычаги, не фиксы)

8. `sdr SDRClient_ForceRelayCluster <code>` (изоляция кластера),
   `net_connections_stats` (CS2), `SDR_NETWORK_CONFIG` env (подсунуть локальный
   конфиг с одним PoP), `steamdatagram_client_single_socket 1` (сжать burst до
   одного сокета — ещё один H1-тест).

---

## 9. Таксономия «класс игры × слой × фикс»

| Класс netcode | Пример | Паттерн UDP | Что его убивает в туннеле | Решающий слой/фикс | Наш статус |
|---|---|---|---|---|---|
| Single-server realtime (RakNet) | Roblox | 1 сокет ↔ 1 сервер, долгоживущий | TCP-HoL (VLESS-over-TCP), DNS-стойла, MTU | UDP-native транспорт (AWG/Hy2) + живой DNS + MTU ≤ endpoint | РАБОТАЕТ (r8) |
| Relay-mesh, client-initiated (SDR) | Dota 2, CS2 | N сокетов → N релеев, burst из десятков проб; затем 1 сокет ↔ выбранный relay, пакеты до 1328 IP-байт | (1) burst-деградация клиентского data-path; (2) exit-side anti-abuse/firewall; (3) MTU<1332; (4) DNS для config-fetch. НЕ NAT-тип | tcpdump-вилка §7; MTU ≥1332; DNS-гигиена. Full-cone НЕ нужен | СЛОМАН — вилка H0/H1/H2 |
| P2P-direct (ICE/STUN) | Steam P2P-титулы, voice, Remote Play | STUN reflexive + hole-punch; unsolicited inbound | port-restricted/symmetric exit NAT (нужен EIM+EIF) | full-cone на exit'е: DNAT-range / einat-ebpf / FULLCONENAT; клиентский TUN уже EIM | не заявлен пользователем; фиксы Phase 2.6 дают его «бесплатно» |
| Anycast/QUIC-игры и лаунчеры | часть мобильных/UDP-443 | 1 сокет ↔ anycast VIP | MTU (QUIC 1200+), rate-limit DPI на UDP-443 | UDP-native транспорт, MTU | попутно ок |

Главная строка: **у SDR решающий слой — не NAT-тип, а (а) выживание burst'а
проб, (б) reachability exit-IP↔relay, (в) MTU для 1328-байтных пакетов.**

---

## 10. Честная оценка fixability

| Что | Кто чинит | Оценка |
|---|---|---|
| MTU 1280→1420 на AWG-пути | клиент (VPNRouter) | тривиально, обязателен, риск low (проверить на живом AWG-сервере) |
| Комментарий r10 + не-регрессия | клиент | тривиально |
| DNS-leak lockdown default / NRPT | клиент | средняя сложность, известный трек (находка B) |
| WSAENOBUFS retry/SO_SNDBUF | клиент (lx-core) | средняя; только после подтверждения H1 |
| tcpdump-вилка + ручной SDR-probe с VPS | владелец VPS (5 минут) | обязательный следующий шаг, стоимость ~0 |
| Full-cone exit (DNAT/einat) | владелец VPS | простой рецепт готов; для SDR-пингов НЕ нужен, но закрывает будущий P2P-класс |
| Anti-abuse хостера / relay игнорирует IP | смена exit-IP | вне контроля кода; единственный тест — второй VPS |
| RU TSPU (фундаментально) | — | обходится только «игры в туннеле» — уже наш дизайн; SDR внутри туннеля TSPU не видит |
| Steam/Valve-сторона (issue 32293 без ответа) | — | не ждать; у нас есть все рычаги диагностики локально |

**Bottom line**: r10 не вреден, но не фикс. Следующее действие — не код, а
шаг 0+1 протокола (§7): консоль Dota + tcpdump на exit'е. После них вилка
H0/H1/H2 схлопывается в единственный root-cause, и фикс из §8 применяется
адресно, а не наугад.

---

## Приложение А. sing-box internals — verified file:line (агент клонировал точный отгружаемый форк)

База: `Leadaxe/sing-box-lx @ c7a2592e` (v1.13.13-lx-awg) + pinned deps
(sing v0.8.10, sing-tun v0.8.10, sing-quic v0.6.1, sing-vmess@3aed155,
wireguard-go-awg2-lx@0c0c10b) + Xray-core@main + apernet/hysteria@main.

1. **EIN — dead option**: `option/tun.go:62` — единственное вхождение;
   `protocol/tun/inbound.go:378-389` не передаёт; удалена из mainline
   sing-tun коммитом `9bcc1ec` (2024-10-23, = sing-box 1.11+); system-стек
   source-keyed с первого коммита (2022-09-06).
2. **udpnat2**: keyed by `source.AddrPort()` (`udpnat2/service.go:18,55`),
   destination — per-packet (`service.go:82`), inbound-фильтра нет; LRU 1024
   (`service.go:29-33`); pre-dial буфер 64 пакета/сессию, silent drop
   (`service.go:90-95`).
3. **Роутинг — один раз на app-сокет** (`route/route.go:195`,
   `route/conn.go:144`): один `ListenPacket` на сессию, destinations
   per-packet. Разрезать live-сокет правилом по порту НЕЛЬЗЯ (первый пакет
   решает всё) — «один сокет = один exit» гарантировано структурно. Footgun
   `udp_connect: true` (connected socket, ломает multi-dst) — мы НЕ ставим.
4. **Idle-timeout по протоколу** (`route/conn.go:240-254`,
   `constant/timeout.go:25-40`): dst-порт 3478 → «STUN» → 10 s; 443 → QUIC
   30 s; 53 → DNS 10 s; наш sniff-rule всегда включён. SDR-порты 27xxx → 5m
   default. (Наш же issue sing-box#4193 — про этот механизм.)
5. **VLESS+xudp**: каждый app-сокет = свежий TCP/Reality dial
   (`protocol/vless/outbound.go:186-215`); xudp Keep-фреймы несут адрес
   per-packet; **sing-box НЕ пишет XUDP GlobalID** → Xray-сервер пропускает
   XUDPManager (reattach-after-reconnect теряем), но внутри живой сессии на
   exit'е — один unconnected OS-сокет через freedom, full cone
   (`proxy/freedom/freedom.go:551-660`, discussions#252).
6. **AWG endpoint**: `ListenPacket` = `gonet.DialUDP(unconnected)` на
   netstack (`transport/wireguard/device_stack.go:145-168`) — EIM+EIF внутри
   туннеля; физический сокет один на весь шифротекст, StdNetBind, syscall на
   датаграмму, SO_SNDBUF/RCVBUF 7MB (`conn/controlfns_windows.go:18-19`);
   send-error → drop batch, no retry (`device/send.go:639`); detour
   ClientBind: send-error закрывает транспорт (`client_bind.go:184-187`).
7. **Hysteria2**: клиент — fresh sessionID per app-сокет поверх одного QUIC
   (`sing-quic/hysteria2/client.go:305-327`), UDP-фрагментация есть
   (`packet.go`); официальный сервер — `net.ListenUDP` per session, без
   source-фильтра → full cone by design (`extras/outbounds/ob_direct.go`).
8. `udp_disable_domain_unmapping` — только про domain-адресованные ответы,
   для IP-адресованных SDR-релеев нерелевантно.

## Приложение Б. Источники (ключевые)

- Valve SDR docs: partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay
- Живой relay-конфиг: api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=570
- GameNetworkingSockets: steamnetworkingtypes.h (SDRClient_SingleSocket,
  LimitPingProbesToNearestN, MinPingsBeforePingAccurate), issue #22 (MTU 1300,
  no PMTUD), #174 (initial_ping_timeout), #216 (P2P/ICE); socketthread.cpp
  (CSharedSocket source-demux)
- Строки бинаря Dota: SteamDatabase/GameTracking-Dota2
  steamnetworkingsockets_strings.txt; протобуфы: SteamDatabase/Protobufs
  steamdatagram_messages_sdr.proto; odota/core (CMsgClientPingData)
- csgo-osx-linux#3239 (3004 Don't have network config; RelayNetworkStatus)
- RFC 4787; net/netfilter/nf_nat_core.c (get_unique_tuple «no port alteration
  where possible»); kernel nf_conntrack-sysctl (30/120 s)
- Chion82/netfilter-full-cone-nat, fullcone-nat-nftables, EHfive/einat-ebpf
- Xray: discussions#252 (XUDP full cone), issues #161, #5509/#5526/#5888
  (TUN fullcone саги, one-socket-many-destinations), #5833/#5858 (WG outbound)
- hysteria: PROTOCOL.md (port per sessionID), core/server/udp.go; issue #860;
  sing-box#968, #261, #1492, #4193 (наш; STUN-sniff 10s trim)
- Prior art: steamcommunity [SOLVED] WireGuard MTU thread; RU-тред «Задержка:
  не удалось вычислить» (2020, чинилось VPN); Mullvad blog (порт-форвардинг
  удалён) + help (split-tunnel Steam); ProtonVPN Moderate NAT; UU加速器 KB
  (strict/moderate NAT в PC-режимах); Netch README; net4people/bbs#347, #181
- ValveSoftware/Dota2-Gameplay#32293 (SDR vs VPN в РФ, open, no answer)
- Наши артефакты: diag 20260701-122336 (burst-лог), codex-diag-20260630
  (WSAENOBUFS), plans/handoff-xhttp-dns-leak-dota-2026-07-01.md (находки B/C),
  sing-tun@v0.8.11 + sing@v0.8.11 исходники (udpnat2 keyed-by-source,
  StackOptions без EIN)
