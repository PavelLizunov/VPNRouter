# ACTION PLAN: Dota/SDR + realtime-игры через VPN — что делаем, в каком порядке, кто

Мастер-план, сводящий два ресёрча:
- `sdr-research-realtime-games-nat-2026-07-02.md` (Fable, 4-агентный + чтение точного форка) — SDR, вилка H0/H1/H2, протокол верификации, план фиксов.
- `mtu-fragmentation-robustness-2026-07-02.md` (source-read sing-tun v0.8.10 + wireguard-go форка) — MSS/фрагментация/PMTUD/MTU.

**Главный принцип (оба ресёрча согласны): сначала ЗАМЕР, потом код.** Полного
root-cause нет и не может быть без одного свежего замера — единственный
задокументированный провал снят на отравленном r6-конфиге (мёртвый DNS + MTU-инверсия);
диага провала на r9+ НЕ существует; «падает на всех 3 транспортах» — устно, не в логах.

**Не приписывать успех r10.** `endpoint_independent_nat` — мёртвая опция (парсится
`option/tun.go:62`, не потребляется `protocol/tun/inbound.go:378-389`, выпилена из
sing-box 1.11; system-стек full-cone by construction). Если тестер скажет «на r10
заработало» — эффект от r8 DNS/MTU-фиксов, впервые опробованных вместе, а НЕ от EIN.

---

## ФАЗА 0 — Исправить запись (сейчас, ~0 риска, замер не нужен)

- [ ] **Переписать комментарий r10** `ConfigGenerator.cs:1127-1149`: убрать ложный
  механизм («SDR получает cross-relay reply»), поставить правду — «EIN no-op на
  system-стеке (парсится, не потребляется, sing-box 1.11+); оставлен на случай
  gVisor; SDR full-cone НЕ требует (per-relay сокеты, same-address reply); см.
  sdr-research-…». Значение `true` оставить (безвредно).
- [ ] **Поправить release-notes r10 на GitHub**: сейчас «full-cone UDP NAT (Dota/SDR
  fix)» — ложь. Заменить на честное «carries r8/r9 fixes; SDR RCA refuted, см. план;
  no functional Dota fix».
- [ ] **Закоммитить** оба research-дока + этот план (Fable оставил uncommitted).
  Память `sdr-fullcone-nat` уже переписана.
- [ ] **Не cut'ать r10 в stable** как «Dota fix».

---

## ФАЗА 1 — ДИАГНОСТИКА (ГЕЙТ; до любого адресного кода). Схлопывает H0/H1/H2.

Владельцы: тестер (шаг 0,3) + владелец VPS (шаг 1,2).

- [ ] **Шаг 0 — Dota `-console`, 5 мин, тестер.** Запустить Dota с `-console` (или
  `+con_logfile console.log`), открыть выбор региона, снять строки `[SteamNetSockets]`:
  - `SDR RelayNetworkStatus: avail=? config=? anyrelay=?`
  - «Ping measurement completed in Ns. Relays: **X valid**…»
  - «Destroying relay … initial_ping_timeout».
  Классификация: `config=Failed` → H0/DNS-класс (смотреть `dns:` в singbox.log в тот
  момент). `config=OK, anyrelay=Failed, Relays: 0 valid` → чистый UDP-класс (H1/H2) → шаг 1.
  **ОБЯЗАТЕЛЬНО прогнать трижды — AWG, VLESS, Hysteria2.** Впервые получим лог-
  доказательство транспорт-(не)зависимости. Параллельно на AWG-прогоне:
  `Select-String wsasendmsg C:\ProgramData\VPNRouter\logs\singbox*.log` — сыпет в
  минуту теста ⇒ **H1-AWG подтверждён на месте**. Если Hy2 ВНЕЗАПНО работает ⇒
  root cause per-transport (H1) ⇒ фикс = «game-UDP через Hy2» (Фаза 2D) — самый дешёвый.
- [ ] **Шаг 1 — tcpdump на exit, 5 мин, root на VPS.** Во время реального Dota-теста:
  ```bash
  tcpdump -ni eth0 'udp portrange 27015-27200' -c 200   # пробы уходят наружу? ответы приходят?
  tcpdump -ni wg0  'udp portrange 27015-27200' -c 200   # (AWG) ответы возвращаются в туннель?
  conntrack -L -p udp | grep <wg-client-ip>             # [UNREPLIED] = ответов нет
  ```
  Матрица:
  - проб НЕТ на eth0 → гибнут на клиенте/в туннеле → **H1** (AWG: WSAENOBUFS в singbox.log).
  - пробы уходят, ответов на eth0 НЕТ → relay/хостер молчит → **H2-внешняя** (шаг 2 / смена IP).
  - ответы на eth0 ЕСТЬ, на wg0 НЕТ → conntrack/firewall exit'а → **H2-локальная** (Фаза 3).
  - ответы дошли до wg0, Dota всё равно ERROR → обратный путь в клиентском стеке →
    pcap на Windows (`pktmon` по TUN) + Clash API `/connections` (download=0 на
    relay-сессиях = не доставлено).
- [ ] **Шаг 2 — relay-reachability с VPS (вручную, без Steam).** `nping --udp -p 27015
  155.133.248.85 --data-length 40 -c 3`. ВАЖНО: формат запроса закрыт → тишина НЕ
  доказывает блок (false-negative); ЛЮБОЙ ответ доказывает reachability. Надёжная
  дискриминация H2-внешней — только tcpdump шага 1. Плюс `stunclient --mode full
  stunserver2025.stunprotocol.org` с VPS (baseline EIM+EIF) и с клиента через туннель
  (композитный NAT-тип; ожидаем port-restricted — этого SDR достаточно).
  Помнить: NatTypeTester шлёт 1 пакет — весь класс burst-багов не видит (Xray #5888).
- [ ] **Шаг 3 — A/B для H0 (тестер).** (а) Dota при живом vpn-dns (грепнуть `dns:
  exchanged api.steampowered.com`, 0 loopback); (б) тот же тест с `dns_leak_lockdown=true`.
  Если (а) ок, а (б) нет → SDR зависит от утечки (отдельный баг). Негативный контроль:
  консоль `sdr SDRClient_ForceRelayCluster fra` — сузить burst до одного кластера;
  оживёт одиночный пинг ⇒ H1-burst.

**Критерий «починено» (для всех фаз ниже):** `Relays: N valid, N ≥ 20` на ≥2
транспортах + вход в матч и **10 минут игры без «connection lost»** (последнее — уже
MTU-гейт).

---

## ФАЗА 2 — Клиентские фиксы (VPNRouter)

### 2A — MTU: откатить вредный 1280 → 1420 (ОБЯЗАТЕЛЬНО, независимо от вилки)
- [ ] `AwgEndpointMtu` 1280 → **1420** (WG-стандарт = `wireguard-go/device/tun.go:14
  DefaultMTU`); TUN = `min(user, 1420)`; endpoint ≥ TUN (без инверсии — та инверсия и
  дала DoH-затык). **Почему обязателен:** SDR шлёт до 1300B payload / **1328B IP**,
  БЕЗ PMTUD (`k_cbSteamNetworkingSocketsMaxUDPMsgLen=1300`, GameNetworkingSockets#22);
  наш 1280 их категорически режет, и SNS вниз не подстроится → вход в матч мёртв даже
  когда пинги оживут. (Известный полевой кейс: CS2/TF2 matchmaking через WG был мёртв
  ровно до MTU-фикса.)
- [ ] **Проверка (не сломать рабочее):** `ping -f -l 1372 <ext>` через туннель
  (1372+28=1400 проходит) + вход в Dota-матч + **браузинг и Roblox остаются ок** на 1420.
- [ ] Риск: low, но нужен ЖИВОЙ тест на AWG-сервере (если реальный underlay <1500 —
  1420 велик, см. 2B). Fallback при живых дропах: 1360, но **не ниже 1332**.

### 2B — Path-MTU робастность («не число, а архитектура» — из моего source-read)
- [ ] **Факты стека (подтверждены в исходниках, закладываться):** MSS-клампинга в
  sing-box НЕТ (`tcp.go` — только константы) → MSS ставит ОС от TUN MTU. System-стек
  **ДРОПАЕТ IP-фрагменты** обе стороны (`stack_system.go:571-575`). PMTUD-сигнализации
  НЕТ (`ICMPv4FragmentationNeeded` только константа `icmpv4.go:113`, не эмитится).
  ⇒ **фрагментация как fallback невозможна; НЕ закладываться на «снять DF / фрагментировать».**
- [ ] Единственный уязвимый класс — DF-UDP > MTU без PMTUD (= SDR-класс, игры капают
  ~1300). TCP иммунен (MSS=TUN−40 2-сторонне), QUIC/HTTP3/Hy2 иммунны (DPLPMTUD). ⇒
  между 1420 и 1500 в этой категории трафика нет; 1420 покрывает весь класс.
- [ ] **Настоящий разрыв — underlay < 1500** (PPPoE 1492 / мобилка / туннель-в-туннеле).
  Сделать: (a) probe эффективного path-MTU (ping-f с уменьшением при коннекте) →
  endpoint = pathMTU − ~80; либо (b) пользовательская MTU-настройка + гайд «если игры/
  крупные загрузки рвутся — снизь MTU». Проверить, ставит ли форк DF на ВНЕШНИЙ
  WG-пакет (если да — внешний PMTU-блэкхол при underlay<1500).

### 2C — WSAENOBUFS на AWG-сокете (P1, но ГЕЙТ на подтверждение H1 шагом 0/1)
- [ ] Механизм (лог 28.06, корреляция burst→ENOBUFS до минуты): ~330 flows/мин
  мультиплексируются в ОДИН физический AWG UDP-сокет (StdNetBind); под burst'ом
  WSAENOBUFS, и wireguard-go **дропает батч без ретрая** (`device/send.go:639`); на
  detour-пути ClientBind send-error вообще закрывает транспортный сокет
  (`client_bind.go:184-187`).
- [ ] Фикс: **ретрай-с-бэкоффом на wsasendmsg ENOBUFS** в lx-форке
  (`conn/bind_std.go:473` — сейчас drop-no-retry) и/или поднять **SO_SNDBUF** (сейчас
  7MB, `conn/controlfns_windows.go`). **Трогает lx-core → НЕ ломать 3 build-патча**
  (память `awg-windows-lx-patches`; тот же bind_std.go, что WSAEFAULT-патчи).
- [ ] Делать ТОЛЬКО после подтверждения, что ENOBUFS — решающий, а не сопутствующий.

### 2D — Продуктовый fallback: game-UDP через Hy2 (если H1 подтвердится per-transport)
- [ ] В full-tunnel с AWG-профилем маршрутизировать game-UDP (или всё UDP) через
  **Hy2-сиблинг**, как уже делается для наивных VLESS (`proxy-udp` route). **Два
  независимых довода сходятся:** (1) Hy2 несёт burst дёшево (sessionID поверх одного
  QUIC, без 330 хендшейков — против H1); (2) Hy2 **MTU-робастен** (DPLPMTUD +
  UDP-over-QUIC фрагментация с реассембли на exit) — SDR-1328 проедет при любом
  underlay, в отличие от AWG (сырой WG + фрагмент-дропающий стек). Prior art:
  apernet/hysteria#860, sing-box#968 (Hy2 official работает).

### 2E — DNS-leak lockdown default (P1, независимо, но важно)
- [ ] Находка B: системный DNS утекал мимо туннеля на RU-резолверы (browserleaks
  195.2.238.4 «Trytek LLC», `dns_leak_lockdown:false`). Это не только приватность —
  **отравляет всю диагностику игровых кейсов (H0)**. Минимум: `dns_leak_lockdown=true`
  по умолчанию в full-tunnel; правильно — победить Windows smart multi-homed
  resolution (NRPT / блок :53 / DoH-редирект на физ. NIC) отдельным треком.

**Что НЕ делать (клиент):** не credit'ить EIN/r10; НЕ переходить на gVisor-стек «ради
NAT»; НЕ делать route-исключения для Steam (TSPU душит direct); НЕ добавлять новый
full-cone на клиенте; НЕ закладываться на фрагментацию (стек её дропает).

---

## ФАЗА 3 — Exit-VPS (владелец VPS; ГЕЙТ на результат шага 1 tcpdump)

- [ ] **H2-локальная** (ответы на eth0 есть, на wg0 нет): для AWG-экзита DNAT-диапазон
  `nft add rule inet nat prerouting iif eth0 udp dport 27015-27200 dnat ip to <wg-client>`
  (заодно полноценный full-cone этому диапазону) ИЛИ **einat-ebpf** (EIM+EIF на TC-хуках,
  kernel ≥5.15, без out-of-tree kmod). Проверить, что FORWARD/ufw/cloud-fw не режут
  (conntrack UDP timeout 30s unreplied / 120s stream — игра должна keep-alive'ить).
- [ ] **H2-внешняя** (проб уходят, ответов на eth0 нет): anti-abuse хостера давит
  UDP-веер (330 пакетов к 60+ /16 за секунды = сигнатура сканера) ИЛИ relay игнорит
  этот IP. Самый дешёвый тест — **второй VPS у другого провайдера** для игрового
  профиля. Full-cone тут НЕ поможет.

---

## ФАЗА 4 — Steam/игра (диагностические рычаги, НЕ фиксы; тестер)
- [ ] `sdr SDRClient_ForceRelayCluster <code>` (изоляция кластера),
  `net_connections_stats` (CS2), `SDR_NETWORK_CONFIG` env (локальный конфиг с одним PoP),
  `steamdatagram_client_single_socket 1` (сжать burst до одного сокета — ещё один H1-тест).

---

## Таксономия «класс игры × слой × фикс» (контекст, не терять)

| Класс | Пример | Что убивает в туннеле | Решающий фикс | Статус |
|---|---|---|---|---|
| Single-server realtime (RakNet) | Roblox | TCP-HoL, DNS-стойла, MTU | UDP-native (AWG/Hy2)+живой DNS+MTU | РАБОТАЕТ (r8) |
| **Relay-mesh (SDR)** | **Dota/CS2** | burst / exit-anti-abuse / MTU<1332 / DNS. **НЕ NAT-тип** | вилка §7 + MTU 1420 + (H1)→Hy2/ENOBUFS + (H2)→exit | **СЛОМАН** |
| P2P-direct (ICE/STUN) | Steam P2P, voice, Remote Play | port-restricted/symmetric exit | full-cone на exit (Фаза 3 даёт «бесплатно») | не заявлен |
| Anycast/QUIC | UDP-443 | MTU, DPI на UDP-443 | UDP-native + MTU | попутно ок |

## Честная оценка fixability
| Что | Кто | Оценка |
|---|---|---|
| MTU 1280→1420 | клиент | тривиально, обязателен, low-risk (жив. тест) |
| Комментарий r10 + notes | клиент | тривиально |
| DNS-leak lockdown | клиент | средне, известный трек |
| WSAENOBUFS retry/SO_SNDBUF | клиент (lx-core) | средне; только после H1 |
| tcpdump-вилка + STUN | владелец VPS (~5 мин) | ОБЯЗАТЕЛЬНЫЙ следующий шаг, ~0 |
| Full-cone exit (DNAT/einat) | владелец VPS | рецепт готов; для SDR-пингов НЕ нужен, но закрывает P2P |
| Anti-abuse / relay игнорит IP | смена exit-IP | вне кода; тест = 2-й VPS |
| RU TSPU | — | только «игры в туннеле» (уже наш дизайн) |
| Steam/Valve (GNS#22293 без ответа) | — | не ждать; рычаги диагностики есть локально |

## Порядок исполнения (кратко)
1. **Фаза 0** (сейчас, я): комментарий + notes + коммит доков.
2. **Фаза 1 шаг 0** (тестер): Dota-консоль ×3 транспорта + wsasendmsg-греп. ← разблокирует всё.
3. **Фаза 2A+2B+2E** (я, можно параллельно с диагностикой — они независимы от вилки):
   MTU 1420 + path-MTU + DNS-lockdown → r11.
4. **Фаза 1 шаг 1** (VPS): tcpdump → определяет H1 vs H2.
5. **По результату:** H1 → Фаза 2C (ENOBUFS) + 2D (Hy2); H2-лок → Фаза 3 DNAT;
   H2-внеш → 2-й VPS; H0 → уже ок, только MTU.
6. **Acceptance:** Relays ≥20 valid на ≥2 транспортах + 10-мин матч без обрыва.
