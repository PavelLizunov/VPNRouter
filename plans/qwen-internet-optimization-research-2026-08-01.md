# Internet-optimization research — 2026-08-01

Read-only исследование соответствия shipped-зависимостей VPNRouter актуальным
upstream-фиксам sing-box / Avalonia. Цель — найти реальные exposure'ы в
интернет-оптимизации (TUN/DNS/Android), НЕ запуская широких рефакторингов.

**Важно:** все находки ниже — это **статический code/version exposure**,
подтверждённый чтением кода и build-скриптов. **Живого A/B / sustained-load
воспроизведения НЕ проводилось** — ни один пункт не утверждает, что пользователь
уже видит баг. Каждый actionable-пункт требует measure-first валидации до любой
имплементации (см. §Валидация и §Remediation).

**Adversarial validation (2026-08-01).** Claude Opus независимо перепроверил
Qwen-проход по локальному коду/скриптам и upstream-коммитам и **исправил
переоценки**: убрана route-address pollution из F1, переформулирован механизм F2,
понижен и сужен F3, F4 (Clash log) переведён в non-actionable, скорректирована
версия-матрица и Avalonia-список. **Живых тестов при этой валидации тоже НЕ
проводилось** — это перечитывание кода и первоисточников, не воспроизведение.

Маркеры: **[FACT]** — проверено в коде/скриптах текущего checkout;
**[INFER]** — рассуждение/оценка вероятного эффекта.

## Scope и версия-матрица

Scope: sing-box core (desktop + Android) и Avalonia (desktop + Android) в части,
касающейся интернет-трафика (TUN-стек, DNS, Android-рендер/доступность).
Вне scope: zapret, TgProxy, wgturn, true-split driver, firewall.

Точная матрица текущего checkout (`AppVersion = 2.48.0-r3`,
`VPNRouter.Core/AppVersion.cs:31`):

| Компонент | Платформа | Что реально бандлится | Где зашито |
|---|---|---|---|
| sing-box core | Windows | fork `Leadaxe/sing-box-lx`, **запиненный commit `c7a2592e`**; ldflags-лейбл версии = `1.13.13-lx-awg` | [FACT] `tools/build-singbox-lx.ps1:31,37,162`; бандлинг `build.ps1:316,329-336` |
| sing-box core | macOS | **тот же запиненный source-commit `c7a2592e`**, но `constant.Version` **подделывается версией приложения VPNRouter** (скрипт получает её первым аргументом) | [FACT] `build-mac.sh:60` → `tools/build-singbox-lx.sh:18,23,98` |
| sing-box core | Linux | **тот же запиненный source-commit `c7a2592e`**, `constant.Version` тоже = версия приложения; `libcronet.so` отдельно — из upstream-архива **1.13.14** с SHA256-пином | [FACT] `.github/workflows/build-linux.yml:101,106-112`; `tools/build-singbox-lx.sh:18,23,98` |
| libbox | Android | **sing-box `1.13.10`** (`tooling-libbox-singbox-1.13.10`) | [FACT] `build-android.ps1:48` |
| Avalonia | Desktop (App + Tests headless) | **12.0.4** | [FACT] `VPNRouter.App/VPNRouter.App.csproj:39-43`; `VPNRouter.Tests/VPNRouter.Tests.csproj:53-54` |
| Avalonia | Android | **12.0.3** | [FACT] `VPNRouter.Android/VPNRouter.Android.csproj:132-135` |
| SkiaSharp | Desktop | `3.119.4` stable (прямая ссылка) | [FACT] `VPNRouter.App/VPNRouter.App.csproj:51` |
| SkiaSharp | Android | предположительно `3.119.4-preview.1.1` транзитивно — **[INFER]**, см. F5 | [INFER] прямого пина и lock-файла нет |

Уточнения по матрице (важны, чтобы не переоценивать находки):

- **[FACT]** Только Windows-сборка репортит себя как `1.13.13-lx-awg`. На
  macOS/Linux `constant.Version` **форжится версией VPNRouter**
  (`build-singbox-lx.sh:23` `VER="${1:-…}"` → `:98` `-X …/constant.Version=$VER`),
  поэтому **нельзя** называть их self-reported core-версию `1.13.13-lx-awg`.
- **[FACT]** Единственное надёжное доказательство upstream-базы форка — не строка
  версии, а **`go.mod` форка**, где `sing-tun` = **v0.8.10**:
  https://raw.githubusercontent.com/Leadaxe/sing-box-lx/c7a2592e750406ade9ebaae1d0fdb7482fc0773e/go.mod
- **[FACT]** Windows-релизный upload **требует fork-тегов**: `build.ps1:325-334`
  бросает, если нет `publish\sing-box-lx.exe` или в `version` нет `with_awg` +
  `with_xhttp`. **Локальная сборка без `-Upload`** такого требования не имеет и
  может забандлить upstream-бинарь.
- **[FACT]** Текущая цель сравнения upstream на 2026-08-01 — **v1.13.15
  (релиз 2026-07-29)**: https://github.com/SagerNet/sing-box/releases/tag/v1.13.15
  Это **кандидат на аудит/тест**, а не автоматически «безопасная» версия: её
  собственные изменения нами не инспектированы.

## Ранжированные находки

Ранг = вероятность × тяжесть эффекта при условии, что exposure сработает. Все
пункты — exposure, не подтверждённый пользовательский баг.

### F1. TUN system-stack TCP NAT collision — CONFIRMED EXPOSURE (measure-first)

Только коллизия TCP NAT. **Route-address pollution из этой находки удалена** —
см. §Опровергнуто.

- **[FACT]** sing-box `0b7ffba` — это бамп зависимости `sing-tun`; сам фикс лежит
  в `sing-tun` `8caaa93`.
  Primary: https://github.com/SagerNet/sing-box/commit/0b7ffba
  Underlying: https://github.com/SagerNet/sing-tun/commit/8caaa93f8de5697701c2e19ad39b92a17985828c
- **[FACT]** Точный триггер: **один и тот же source IP:port переиспользуется для
  ДРУГОГО destination, пока старая NAT-запись ещё жива** → пакеты могут уехать не
  туда. Это про быструю переиспользуемость эфемерных портов, а НЕ про долгий
  одиночный поток — **эксперимент Qwen с long-lived flow был неверен**.
- **[FACT]** Windows и Linux выбирают `system` TUN-стек:
  `VPNRouter.Core/Services/ConfigGenerator.cs:39-40`
  (`SelectTunStack(isMacOS) => isMacOS ? "gvisor" : "system"`).
- **[FACT]** macOS использует `gvisor` (там же) → **не подвержена**.
- **[INFER]** Форк на запиненном `c7a2592e` (sing-tun v0.8.10 по go.mod) не содержит
  `8caaa93` → на Windows/Linux exposure есть; частота и пользовательский эффект
  неизвестны.
- **Статус:** confirmed version/code exposure; **user impact требует замера до фикса.**

### F2. Детерминированный self-deadlock вложенного single-flight в DNS — MEASURE FIRST

- **[FACT]** Upstream-фикс: https://github.com/SagerNet/sing-box/commit/72a8723e13b9574664f4c78e588069fa4aca6fc9
- **[FACT]** Механизм — **детерминированный**, не вероятностный: внешний DNS-запрос Q
  на транспорте T1 внутри себя бутстрапит транспорт T2, которому нужен **тот же
  запрос Q**; single-flight-дедупликация ждёт сама себя.
- **[FACT]** Подходящий кандидат в нашей конфигурации ровно один класс —
  **hostname + `DomainResolver = "local-dns"`**:
  - `vpn-dns` в DoH-режиме: `Server = "dns.adguard-dns.com"` (при BlockAds) или
    hostname пользовательского DoH, `DomainResolver = "local-dns"` —
    `ConfigGenerator.cs:2137-2166`;
  - proxy-outbound'ы, заданные **hostname** (не IP-литералом), с
    `DomainResolver = "local-dns"` — `ConfigGenerator.cs:1643`; плюс
    `route.default_domain_resolver = "local-dns"` — `:2121`.
- **[FACT]** Что **НЕ** является уликой (убрано из находки): `local-dns` — это DoH по
  **IP-литералу** `1.1.1.1` (`ConfigGenerator.cs:1003-1017`), резолвить нечего;
  `dns-system` — `type: local` (OS-резолвер); slipstream-loopback `127.0.0.1` — тоже
  литерал. Ни один из них вложенный бутстрап не порождает.
- **[FACT]** `hijack-dns` route-правило — `ConfigGenerator.cs:2002`
  (в исходной версии отчёта строка была указана неверно).
- **[INFER]** Форк на `c7a2592e` фикса не содержит; Android libbox 1.13.10 тем более.
- **Статус:** **MEASURE FIRST** — целевой запрос, не широкая нагрузка (см. §Валидация).

### F3. Android libbox 1.13.10 — P3, СУЖЕНО до BypassRu UDP DNS (measure-first)

Исходное утверждение Qwen про «Android DoH leak/deadline» **опровергнуто** и снято.

- **[FACT]** Ни `d166f0d`, ни `6548c17` **не затрагивают HTTPS/DoH-транспорт** —
  широкий вывод «все Android-пользователи с DoH затронуты» **неверен**.
  Primary: https://github.com/SagerNet/sing-box/commit/d166f0da8b3d87ae65897989e9eb5778306d4172 ,
  https://github.com/SagerNet/sing-box/commit/6548c1711032a1a9b89ad44184f81b96fa472c97
- **[FACT]** Единственный **plain-UDP** DNS-путь в конфиге — RU-bypass резолвер
  `77.88.8.8` (`type: "udp"`, `Detour = "dns-direct"`), который добавляется только
  при включённом **BypassRu**: `ConfigGenerator.cs:848-854`.
- **[FACT]** Desktop-форк (база 1.13.13) уже содержит фиксы 1.13.12 → **desktop вне
  этой находки**; речь только про Android libbox `1.13.10` (`build-android.ps1:48`).
- **[INFER]** Exposure узкий: Android + BypassRu ON + активный UDP-DNS трафик.
  Никакого утверждения про всех Android-пользователей.
- **Статус:** **P3, measure-first.** При подтверждении — ротация libbox на
  проверенный более новый upstream, НЕ локальные workaround'ы.

### F4 — снят как actionable

Clash-log находка переведена в §Опровергнуто / non-actionable.

### F5. Android Avalonia — P3 controlled upgrade research, НЕ дефект

- **[FACT]** Desktop на Avalonia 12.0.4, Android на 12.0.3 (см. матрицу).
  12.0.4: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.4
- **[FACT]** Из прежнего списка релевантны только два пункта:
  - TalkBack PointToScreen — https://github.com/AvaloniaUI/Avalonia/pull/21402
  - SkiaSharp stable bump — https://github.com/AvaloniaUI/Avalonia/pull/21434
- **[FACT]** Снято: cached bidi double reorder (RU/EN — **LTR**, bidi-путь не наш)
  и ItemTemplate compiled-binding validation (Android-шаблоны — `FuncDataTemplate`
  в C#, не XAML compiled bindings).
- **[FACT]** Дополнительные кандидаты для Android:
  - File properties — https://github.com/AvaloniaUI/Avalonia/pull/21307
  - system-back event — https://github.com/AvaloniaUI/Avalonia/pull/21246
- **[INFER]** (было ошибочно помечено как FACT) Android разрешает SkiaSharp
  `3.119.4-preview.1.1`: у `Avalonia.Skia` 12.0.3 это минимальная зависимость
  (https://www.nuget.org/packages/Avalonia.Skia/12.0.3), а прямого пина SkiaSharp в
  `VPNRouter.Android.csproj` и lock-файла в репо нет — **фактический resolve не
  проверен**. Доказательство: `dotnet list package --include-transitive` по
  Android-проекту, когда доступен SDK.
- **[FACT]** Уже существуют релизы Avalonia **12.0.5, 12.1.0, 12.1.1**
  (https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.1) → валидировать
  актуальную выверенную цель, а не слепо пинить 12.0.4.
- **Статус:** **не дефект, P3** — контролируемое upgrade-research.

### F6. Ротация базы форка на актуальный upstream v1.13.15 — NEW, measure-first

- **[FACT]** Текущая база — запиненный `c7a2592e` (sing-tun v0.8.10). Актуальный
  upstream на 2026-08-01 — **v1.13.15 (2026-07-29)**:
  https://github.com/SagerNet/sing-box/releases/tag/v1.13.15
- **[INFER]** Ротация базы форка на v1.13.15 забирает и более новый `sing-tun`
  (включая F1-фикс `8caaa93`), но её **прочие изменения нами не инспектированы** —
  не утверждаем, что они безопасны или что чинят что-то ещё у нас.
- **Обязательные условия ротации:** сохранить AWG/XHTTP и downstream-патчи
  (`tools/build-singbox-lx.ps1` / `.sh`: WSAEFAULT send+recv, H4 reserved-byte gate),
  перепинить commit **и одновременно** перепинить версию + SHA256 `libcronet.so`
  на Linux (`.github/workflows/build-linux.yml:106-112` — сейчас 1.13.14).
- **Статус:** **measure-first** — сначала аудит diff'а и тест сборки, потом решение.
  Смежно: открытый backlog-пункт про отсутствие валидации `-SingBoxPath`/SHA256
  бандлимого бинаря (`plans/OPEN-DEFECTS.md`) — ротация усиливает его актуальность,
  но это отдельная запись, не дублируем.

## Валидация (минимальные эксперименты)

Общий принцип: **measure-first.** Никакой имплементации до результата.

- **F1 (TUN TCP NAT collision):** через system-TUN прогнать **>20 000 коротких TCP-
  соединений за пять минут минимум на два разных destination** (быстрый оборот
  эфемерных портов) и детектировать **доставку не тому destination**
  (cross-destination delivery). Долгий одиночный поток триггером НЕ является.
  macOS (gvisor) — вне эксперимента.
- **F2 (DNS self-deadlock):** BlockAds ON, через тоннель запросить
  **`dns.adguard-dns.com`**; повторить с **реальным hostname VLESS/proxy-сервера**
  (когда outbound задан hostname'ом). Сравнить форк vs выверенный кандидат 1.13.15.
  Широкая sustained-нагрузка НЕ требуется — механизм детерминированный.
- **F3 (Android BypassRu UDP DNS):** минимальный замер **только при включённом
  BypassRu** (путь `77.88.8.8`, `ConfigGenerator.cs:848-854`): рост сокетов/памяти +
  DNS-stall'ы. **Negative control — DoH-only прогон** (BypassRu OFF): там эффекта
  быть не должно.
- **F5 (Avalonia):** сначала `dotnet list package --include-transitive` по
  Android-проекту (подтвердить/опровергнуть SkiaSharp preview), затем контролируемый
  upgrade на **актуальную выверенную** версию → build + TalkBack PointToScreen,
  Android file-properties и system-back сценарии, lifecycle smoke; keep/revert по
  evidence.
- **F6 (base rotation):** собрать форк на базе v1.13.15 с сохранёнными
  AWG/XHTTP+downstream-патчами, прогнать F1/F2-эксперименты на нём как на
  сравнительной ветке; параллельно перепинить libcronet (версия + SHA256).

## Remediation (минимальное направление)

Явно **без** широких переписываний и dependency-harmonization.

- **F1/F2/F6 (desktop core):** при подтверждении эффектом — ротация базы fork'а
  sing-box-lx на **выверенный** более новый upstream (кандидат — v1.13.15), с
  сохранением текущих build-time патчей (AWG/XHTTP/WSAEFAULT/H4), re-pin коммита и
  синхронным re-pin libcronet (версия + SHA256). Точечное действие, не переписывание
  `ConfigGenerator`.
- **F3 (Android):** при подтверждении на BypassRu-пути — ротация libbox на
  проверенный более новый upstream-тег; НЕ локальные workaround'ы в Java/Core.
- **F5 (Android Avalonia):** точечный bump `VPNRouter.Android.csproj` на актуальную
  выверенную версию (и SkiaSharp-переход на stable, если resolve подтвердится и
  upgrade оставим), только после положительного validation; иначе revert.
- **Явно НЕ добавлять:** Serilog/YAML/HttpClient-рефакторы и прочую
  dependency-harmonization — они не обоснованы этим исследованием.

## Опровергнуто / non-actionable

- **[FACT] Route-address pollution (бывшая половина F1) — не применима.** Фикс
  https://github.com/SagerNet/sing-tun/commit/b1c48c12e2c667a880d9682636ae68145ca06df1
  относится исключительно к `auto_redirect`/nftables-пути. VPNRouter **не эмитит
  `auto_redirect`** ни в одной конфигурации → этот код у нас не исполняется.
  `RouteExcludeAddress` к нему отношения не имеет.
- **[FACT] Clash log subscriber-level fix (бывший F4) — non-actionable.**
  https://github.com/SagerNet/sing-box/commit/6397675 — уровень логов по умолчанию
  `info`, а мы запрашиваем ровно `info` (`ClashLogStream.cs:93`,
  `/logs?level=info`), так что запрошенный уровень совпадает с дефолтным. Сам
  стрим включается только под env-флагом **`VPNROUTER_CONN_HEALTH`**
  (`VpnEngine.cs:90,993-1004`) и **observe-only** — не триггерит ни failover, ни
  toast (`ConnectionHealthClassifier.cs:76-77`). Ни actionable-эффекта, ни
  backlog-пункта.
- **[FACT] Широкий Android DoH leak/deadline (исходный F3) — опровергнут:**
  `d166f0d` и `6548c17` не затрагивают HTTPS/DoH-транспорт. Осталась только узкая
  P3-формулировка про BypassRu UDP DNS (см. F3).
- **[FACT] Android process-search regression (фикс в sing-box 1.13.11) — не применим:**
  Android маршрутизирует приложения через VpnService allowed/disallowed package-списки
  и не эмитит `process_name`-правил.
- **[FACT] Валидация VLESS `packet_encoding` (1.13.14) — не применима:** runtime не
  эмитит `packet_encoding`; верификаторы эмитят только валидный `xudp`.
- **[FACT] sing-mux UDP-фикс — не применим:** VPNRouter не генерирует
  multiplex/sing-mux.
- **[INFER] Package version skew сам по себе — не дефект** (см. F5): расхождение
  версий требует evidence, а не автоматического bump'а.
- **[INFER] Широкие Serilog/YAML/HttpClient-рефакторы — не обоснованы;** не добавлять.

## Связь

- Backlog-записи: `plans/OPEN-DEFECTS.md`, секция
  `### Internet-optimization research — 2026-08-01` (measure-first gate).
- Смежное: SDR/realtime-games кластер в `OPEN-DEFECTS.md` (AWG WSAENOBUFS/MTU) —
  не пересекать: тот кластер про lx-core bind/MTU, этот — про upstream sing-box
  фиксы и Avalonia.
