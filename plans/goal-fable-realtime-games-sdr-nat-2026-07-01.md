# GOAL (Fable 5): realtime-игры через VPN из цензурируемой сети — глубокий research + архитектура решения (фокус: Steam Datagram Relay / NAT)

> Это research-бриф для очень способной модели. **Не спеши к ответу.** Сначала
> исследуй предметную область (протокол SDR, теория NAT, внутренности sing-box,
> поведение NAT на WireGuard/Hysteria2-exit, чужие рабочие рецепты), рассуждай
> вслух по каждому слою, рассмотри несколько подходов и их trade-off'ы — и только
> потом выдавай план. Цитируй источники. Явно отделяй проверенный факт от гипотезы.
> У тебя НЕТ контекста прошлой сессии — весь нужный материал ниже.

## Что за система

VPNRouter — process-based split-tunnel VPN (.NET 8 + Avalonia + sing-box) для
Windows/macOS/Linux/Android. Ядро — **sing-box**; на Windows-desktop бандлится форк
**sing-box-lx** с поддержкой AmneziaWG + XHTTP. Клиент поднимает **TUN**-интерфейс и
гонит трафик в один **exit-сервер** через один из транспортов:
- **VLESS + Reality** — TCP-based; UDP инкапсулируется поверх потока (xudp / packet_encoding).
- **AmneziaWG (AWG)** — WireGuard + обфускация; **нативный UDP** (эмитится как sing-box `endpoints[]` wireguard).
- **Hysteria2** — QUIC-based; **нативный UDP**.

Пользователь — в **РФ**. Ключевое ограничение: **RU TSPU/DPI замораживает
foreign-DC IP для tcp И udp** (net4people/bbs #490), поэтому **direct (мимо VPN) для
игр не работает** — игровые IP душатся. Значит любое решение обязано держать игры
**внутри туннеля**. Режим — **full-tunnel** (весь трафик через TUN → proxy).

## Проблема (симптом)

Realtime-игры через туннель работают непоследовательно:

| Игра | Netcode | Результат | Транспорт |
|---|---|---|---|
| **Roblox** | RakNet (один game-сервер, realtime UDP) | **РАБОТАЕТ** | AWG (после фиксов DNS+MTU) |
| **Dota 2 / CS2** | **Steam Datagram Relay (SDR)** — сетка релеев | **НЕ РАБОТАЕТ** | VLESS + AWG + Hysteria2 — **одинаково** |
| Steam сам (магазин/друзья/загрузки, TCP+web) | — | **РАБОТАЕТ** | любой |

Симптом Dota: в списке регионов «**Задержка: ОШИБКА**» на всех регионах;
матчмейкинг не коннектит. Тестер подтвердил: «**проблема 100% SDR**» (сам Steam ок,
падает только SDR-часть Dota). **Транспорт-независимость** (падает на TCP-VLESS,
UDP-AWG и QUIC-Hy2 одинаково, при рабочем Roblox) — важнейшая улика.

## Что уже установлено (проверенные ФАКТЫ, не гипотезы)

1. **Туннель форвардит и TCP, и UDP нормально.** Диаг показал: TCP к Steam
   (`20.209.216.97:443`) = **40 мс**; игровой UDP к релеям (`155.133.x`,
   `162.254.x:27xxx`) и STUN (`128.116.31.74:3478`) идут **через
   `endpoint/wireguard[proxy]`** (в туннеле, не мимо). Это НЕ баг форвардинга.
2. **DNS-фикс (v2.45.0-r8):** раньше VPN-DNS был DoH к AdGuard через proxy; холодный
   резолв занимал **12–56 сек** (TLS-handshake блэкхолился на MTU). Заменено на
   **plain-UDP DNS внутри туннеля** → резолв <200мс. Это починило браузинг и Roblox.
3. **MTU-фикс (r8):** TUN MTU обрезан до MTU AWG-эндпоинта (**1280**), чтобы крупные
   пакеты не фрагментировались/дропались.
4. **Full-cone NAT (v2.45.0-r10) — ГИПОТЕЗА, живьём НЕ подтверждена:** в sing-box TUN
   было `endpoint_independent_nat=false` (дефолт). Поставили `true` (full-cone) —
   рассуждение: SDR получает ответ/координацию **с другого адреса релея**, чем ушёл
   запрос, и при keyed-by-(src,dst) sing-box **дропает** этот cross-relay ответ. Это
   TUN-уровень → объясняет транспорт-независимость. **Нужно критически проверить,
   достаточно ли этого / необходимо ли / не red herring ли.**

## Вопросы для research (мясо — здесь рассуждай долго)

### 1. Как реально работает Steam Datagram Relay на уровне пакетов?
Relay PoP selection; протокол измерения латентности («ping всех регионов»); порты
(STUN 3478, 27xxx); ticket-based auth; как SDR делает NAT-traversal. **Почему именно
region-ping/матчмейкинг ломается через single-exit туннель, а обычный Steam (TCP) —
нет?** Приходят ли ответы релеев с ДРУГОГО (IP:port), чем ушёл запрос (это ядро
full-cone гипотезы)?

### 2. Слой NAT: клиент vs exit-сервер — где рвётся?
- Достаточно ли full-cone на **клиентском TUN** (r10), или решающий — NAT на
  **exit-сервере**?
- Какой NAT презентует каждый exit: **AmneziaWG/WireGuard** = Linux MASQUERADE —
  это endpoint-independent mapping+filtering (full-cone) или restricted/symmetric?
  При каких условиях деградирует? **VLESS/Reality** (UDP via xudp) и **Hysteria2**
  (QUIC) — как их серверная сторона делает UDP-NAT?
- Какой NAT-тип **требует** SDR (full-cone / restricted-cone / symmetric)? В какой
  точке пути `app → TUN → tunnel → exit → relay` он ломается?
- Почему Roblox (один сервер, ответ с того же адреса) переживает не-full-cone, а
  SDR (сетка релеев) — нет?

### 3. Полное пространство решений (client + server + game)
- **Клиент (sing-box TUN/outbound):** `endpoint_independent_nat`, `udp_timeout`,
  `udp_disable_domain_unmapping`, `stack` (system vs gVisor), `packet_encoding`/xudp
  на outbound, любые UDP-NAT-релевантные опции.
- **Сервер (exit):** full-cone NAT (nftables `fullcone`/masquerade, conntrack
  tuning) — отдельно для WG/AWG-masquerade и для sing-box-server UDP-handling.
- **Игра/Steam:** launch-опции, SDR-config, relay pinning, отключение SDR.

### 4. Prior art — как это делают ДРУГИЕ?
Как рабочие setup'ы гоняют Steam-игры (Dota/CS2) через VPN/proxy из цензурируемой
сети? WireGuard-based коммерческие VPN, Xray/sing-box community, AmneziaVPN,
net4people/bbs, форумы SteamDeck/Linux gaming через VPN. **Есть ли известный
known-good рецепт?** Что конкретно они меняют (NAT, транспорт, relay-config)?

### 5. Таксономия «игровой netcode vs VPN»
Классифицируй режимы отказа: **single-server-UDP (RakNet/Roblox)** vs
**relay-mesh (SDR/Dota/CS2)** vs **P2P-direct**. Для каждого — какой слой решает:
транспорт (TCP-HoL vs UDP-native), NAT-тип, DNS, MTU? Построй матрицу «класс игры ×
слой × фикс». Это даст архитектуру, а не точечную заплатку.

### 6. Протокол верификации
Из sing-box диага (`singbox.log` + `current.json`): как **однозначно
подтвердить/опровергнуть** full-cone гипотезу? Какие сигнатуры в логе показывают,
что cross-relay ответы дропаются vs доставляются? Какой контролируемый тест
**изолирует** client-NAT vs server-NAT vs relay-reachability (например: STUN
NAT-type тест с exit-сервера; сравнение поведения при EIN=true/false; tcpdump на
exit)?

## Что нужно на выходе (deliverables)

1. **Полное root-cause объяснение** почему SDR рвётся через single-exit VPN — на
   уровне пакетов, со ссылками.
2. **Приоритизированный конкретный план фиксов** по слоям client/server/game — с
   ТОЧНЫМИ изменениями (sing-box опции + значения; nftables-правила для exit;
   Steam launch-опции). Явно: что делать в первую очередь, что если не поможет.
3. **Однозначный протокол верификации** (что снять, на что смотреть) — чтобы
   подтвердить фикс без гадания.
4. **Таксономия + fix-матрица** «класс игры × слой».
5. **Честная оценка fixability:** что чинит клиент (VPNRouter), что требует
   exit-сервера (VPS, управляется пользователем), что фундаментально тяжело при
   RU-TSPU. Критически оцени, не является ли r10 full-cone фикс недостаточным или
   red herring.

## Ограничения / грануляции для рассуждений

- **Цитируй источники** (Valve SDR docs, GitHub ValveSoftware/GameNetworkingSockets,
  net4people/bbs, sing-box docs, литература по NAT-типам/RFC 4787 EIM/EIF). Отделяй
  факт от инференса.
- **Direct не вариант** — RU-TSPU душит игровые IP; решение держит игры в туннеле.
- **Codebase** = .NET 8 + sing-box (Windows: форк sing-box-lx с AWG). Exit-серверы
  пользовательские (VLESS/AWG/Hy2 на Linux). Клиентский конфиг генерится из
  `ConfigGenerator.cs` (sing-box JSON: TUN inbound + endpoints/outbounds + route + dns).
- **Не спеши.** Пройди каждый слой, исследуй прежде чем предлагать, взвесь
  несколько подходов. Цель — решение, которое ТОЧНО поможет игрокам в РФ, а не
  правдоподобная догадка.
