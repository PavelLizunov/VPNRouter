# Server health / failover — reliability backlog (v2, post-review)

Status: **backlog / assessment only** (no code yet). Created 2026-06-19,
**revised after independent review** ([independent-review-server-health-mtu-2026-06-19.md](independent-review-server-health-mtu-2026-06-19.md)).
Key corrections from that review are folded in below; the review file is the audit trail.

## Триггер

DE-user (`VPNRouter-diagnostics-20260619-205004.zip`, v2.42.1): ChatGPT в браузере
не грузится в full-tunnel, работает в «Выбранных приложениях». YouTube починили ранее.

## Симптом

Full-tunnel («Весь трафик») → ChatGPT (Cloudflare) не открывается; selected-apps → ок.

## Root cause — **bounded** (не «definitely server»)

Подтверждено и перепроверено независимо:
- **Частые `EOF` на открытии relay через VLESS-outbound.** Счётчики верны:
  bundle 205004 — **1587** `using outbound/vless[proxy]: EOF` (медиана ~109 мс,
  1280/1587 < 200 мс); bundle 214717 — **1952** (медиана ~96 мс). Цели разнесены
  по провайдерам: 205004 — Roblox `128.116.*` (868, доминирует), Telegram, чуть
  Cloudflare; 214717 — **Anthropic `160.79.104.10` = api.anthropic.com/claude.ai
  (656)**, `ninitux.com 83.97.108.34` (546), Telegram (239), Cloudflare
  172.64/104.18 (ChatGPT, ~160).
- **Клиентский конфиг корректен** (перепроверено): DNS detour через proxy,
  route.final = direct(split)/proxy(full), QUIC-reject = process-scoped(split)/
  global(full). Нет route-инверсии, нет missing proxy-outbound. **Оба bundle — один
  узел `104.194.156.93`** (chachkamuti и main-brat = один IP).

**Localization — UNCERTAIN (важная правка ревью).** EOF-паттерн (~100 мс по
несвязанным целям) согласуется с «endpoint/путь отвергает создание relay», но НЕ
различает: server CPU/fd/conntrack/лимиты · провайдер-политика · ISP/DPI ·
path-specific сбой. Для точной локализации нужны серверные логи или одновременный
client+server pcap.

**RST/DPI-гипотеза — ОТОЗВАНА.** Прежний счёт «737 RST = сбросы Reality» неверен:
из 737 `forcibly closed` — **733 «connection upload closed»** (локальная TUN-сторона,
часто долгоживущие коннекты), 2 download, **только 4 ссылаются на внешний прокси
`104.194.156.93:443`**. Это не доказывает DPI/серверные сбросы. «Node/path capacity»
сейчас вероятнее DPI.

**Почему split ок, а full нет — plausible, не доказано.** App-лог: Roblox
детектится → переход в full → сразу volley VLESS-EOF. Версия: full гонит больше
коннектов (вкл. Roblox — много коротких параллельных) через одиночный TCP-only
VLESS → выше доля relay-open EOF → параллельные коннекты ChatGPT не проходят.
Корреляция, не доказанный механизм перегруза.

### Test fixtures (для регрессии телеметрии B0/C)
- `205004.zip` — split→full, 1587 EOF, **Roblox-фон доминирует** (не должно давать
  false-positive «нода плохая»).
- `214717.zip` — full, 1952 EOF, цель api.anthropic.com; **733/737 "forcibly closed"
  = локальные upload-closes** (классификатор НЕ должен их считать как сбои прокси —
  только 4 outer-proxy валидны).

## Конкурентный контекст (узко, после правки)

- `urltest` (sing-box) — выбор по латентности (probe `generate_204`, interval 3m,
  tolerance 50ms; VPNRouter эмитит 150ms). Ловит мёртвую ноду (если падает сам
  probe), но **может пропустить ноду, что отдаёт лёгкий 204, но сыпет под нагрузкой**.
- v2rayN/v2rayNG **имеют** policy-groups, `leastPing`, `leastLoad`, balancing,
  observatory-пробы, fallback. Чего НЕ найдено — **passive-контроллера, который
  считает error-rate реального трафика и штрафует/переключает узел**. Nekoray —
  auto-test/switch остался feature request (#417).
- **Наш дифференциатор (узко):** passive runtime failure-rate detection из реального
  трафика + node penalty/cooldown/failover. Именно это ниша.

---

## Backlog (переоценено, с учётом существующей инфры)

> Что УЖЕ есть в коде (не строить заново): `ConfigGenerator.AddOutboundGroup`
> (urltest-обёртка при N серверах, generate_204, 3m, tolerance 150, без
> interrupt_exist_connections); `AutoFailoverEngine` (выбор др. сервера, attempts,
> persist, restart-delegate, surface failures); `ConfigSanityCheck` (2 Clash
> delay-probe после старта — детект мёртвого probe, НЕ error-rate). Clash API
> `/connections` (без причин закрытия) + **`/logs` WebSocket** (структурный
> `{type, payload}` стрим).

### B0. Passive-телеметрия (observe-only) · P0 · ~2-3 дня · risk: low
**Сначала это.** Подписка на Clash API **`/logs` (WebSocket)** (не file-tail —
избегаем ротации/encoding/partial-line) + классификатор:
- VLESS **relay-open EOF**; реальные **outer-proxy TCP reset** (по адресу узла);
  локальные TUN/app-закрытия (upload/download); destination + connection-id;
  successful vs failed relay-opens (знаменатель).
- Без тостов и переключений. Лог-фикстуры 205004/214717 как регрессия.

**Acceptance:** [ ] на 214717 классификатор даёт 1952 relay-open EOF; [ ] НЕ
относит 733 upload-closes к outer-proxy (только 4 валидны); [ ] нет UI/failover.

### A. Opt-in urltest по пулу серверов подписки · P1 · ~2-3 дня · risk: low-med
Первая user-facing фича. **Переиспользовать `AddOutboundGroup`** — главное
недостающее: пул серверов (сейчас `GetActiveServers` отдаёт только выбранный +
same-IP). Определить: eligible-pool, protocol-compat, node-bundle/exit-IP правила,
UI-opt-in, поведение existing connections (`interrupt_exist_connections=false` →
старые коннекты не чинятся), консистентность DNS/UDP-групп.

**Acceptance:** [ ] `urltest` tagged `proxy` проходит LeakProtection (подтверждено:
LeakProtection.cs:284-345); [ ] `sing-box check` зелёный; [ ] мёртвая нода → трафик
через живую; [ ] opt-out = старое поведение.

### C. Калиброванное warning · P2 · ~2-4 дня · risk: med
**Только после B0** (иначе повторим RST-misclassification в UI). Нейтральный текст
(«необычно высокая доля сбоев открытия соединений — смените сервер / снизьте
full-tunnel-трафик»), без заявлений про DPI/перегруз. Нужны: знаменатель,
rolling-windows, startup/reconnect grace, debounce/cooldown, min sample size,
per-node атрибуция.

### B. Auto-failover по health-state · P1 · ~5-8 дней · risk: med
**Переиспользовать `AutoFailoverEngine`** — скормить ему sustained bad-node state
из B0 (не строить второй server-cycling). Контролы: min sample, N подряд плохих
окон, startup grace, cooldown, max switches/интервал, penalty expiry + recovery
probe, видимая причина + выбранная замена, **НЕ свапать молча custom-JSON конфиги**.

### D. Paired UDP path в full-tunnel · P3 · ~2-4 дня · risk: med
Только после строгого контракта идентичности пары. **Правка:**
`FindNaiveUdpSibling`/`NaivePairing` **НЕ same-host** — пара по `PairGroup` +
stripped-name fallback (same-IP-ограничение в `GetActiveServers`). Для D на полном
пуле name-fallback может вернуть другой хост → разные exit-IP. Требует:
explicit `PairGroup`, exact-host policy, verified common exit. У текущей подписки
полезной same-node TCP+UDP пары (Germany) НЕТ → краткосрочной ценности мало.

---

## Рекомендованная последовательность (правка ревью)

**B0 → A → C → B → D** (было C→A→B→D). Сначала observe-only телеметрия (правильная
классификация), потом opt-in urltest (первая безопасная user-facing), потом
калиброванный warning, потом failover, потом D.

## Доп. риски (из ревью, E1-E8)

- E1: считать «forcibly closed» нельзя без классификации (upload/download, TUN-tuple
  vs outer-proxy vs direct, relay-open vs teardown).
- E2: процент сбоев требует определённого **знаменателя** (relay-opens? conn-id?
  `/connections` не годится — короткие сбойные коннекты исчезают до polling).
- E3: норм. закрытия шумят (игры, Happy Eyeballs, speculative/cancelled) → абсолютный
  порог даст false-warn.
- E4: независимый urltest на TCP/UDP/DNS-группах → разные exit-узлы → login/geo/fraud
  поломки. Селекция по логическому bundle.
- E5: смена группы НЕ чинит existing connections (`interrupt_exist_connections=false`).
- E6: probe-success ≠ service-success (нода тянет 204, но рубит concurrency/long-lived/UDP).
- E7: тост до калибровки = ложный авторитет (эти bundle это и демонстрируют) →
  первый релиз телеметрии **observe-only**.
- E8: код-комментарии про MTU переоценивают доказанность (см. MTU-файл).

## Немедленный обход для пользователя (без релиза)

Сменить активный сервер на **Latvia HY2** (Hysteria2 = QUIC/UDP, другой протокол) —
если EOF исчезает, виновата немецкая нода; если остаётся на всех серверах →
локальный DPI/ISP. Либо не гонять full-tunnel при запущенном Roblox.

## Связь с другими планами

- [independent-review-server-health-mtu-2026-06-19.md](independent-review-server-health-mtu-2026-06-19.md) — ревью (источник правок).
- `firewall-killswitch-linux-macos-2026-06-02.md`, `vpn-connection-user-statistics-product-notes-2026-06-02.md` (Clash API телеметрия — переиспользуемо для B0).
- `mtu-default-9000-research-2026-06-19.md` — отдельная тема, НЕ связана с этим EOF.
