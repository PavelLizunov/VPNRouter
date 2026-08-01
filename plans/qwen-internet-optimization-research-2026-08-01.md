# Internet-optimization research — 2026-08-01

Read-only исследование соответствия shipped-зависимостей VPNRouter актуальным
upstream-фиксам sing-box / Avalonia. Цель — найти реальные exposure'ы в
интернет-оптимизации (TUN/DNS/Clash/Android), НЕ запуская широких рефакторингов.

**Важно:** все находки ниже — это **статический code/version exposure**,
подтверждённый чтением кода и build-скриптов. **Живого A/B / sustained-load
воспроизведения НЕ проводилось** — ни один пункт не утверждает, что пользователь
уже видит баг. Каждый actionable-пункт требует measure-first валидации до любой
имплементации (см. §Валидация и §Remediation).

Маркеры: **[FACT]** — проверено в коде/скриптах текущего checkout;
**[INFER]** — рассуждение/оценка вероятного эффекта.

## Scope и версия-матрица

Scope: sing-box core (desktop + Android) и Avalonia (desktop + Android) в части,
касающейся интернет-трафика (TUN-стек, DNS, Clash log, Android-рендер/доступность).
Вне scope: zapret, TgProxy, wgturn, true-split driver, firewall.

Точная матрица текущего checkout (`AppVersion = 2.48.0-r3`,
`VPNRouter.Core/AppVersion.cs:31`):

| Компонент | Платформа | Версия | Где зашито |
|---|---|---|---|
| sing-box core | Windows | **sing-box-lx fork `1.13.13-lx-awg`** (Leadaxe/sing-box-lx commit `c7a2592e`) | [FACT] `tools/build-singbox-lx.ps1:31-38`; бандлинг `build.ps1:316,329-336` |
| sing-box core | macOS | тот же fork `1.13.13-lx-awg` | [FACT] `build-mac.sh:60` → `tools/build-singbox-lx.sh` |
| sing-box core | Linux | тот же fork `1.13.13-lx-awg` (`libcronet.so` — из upstream 1.13.14 архива) | [FACT] `.github/workflows/build-linux.yml` (`bash tools/build-singbox-lx.sh`) |
| libbox | Android | **sing-box `1.13.10`** (`tooling-libbox-singbox-1.13.10`) | [FACT] `build-android.ps1:48` |
| Avalonia | Desktop (App + Tests headless) | **12.0.4** | [FACT] `VPNRouter.App/VPNRouter.App.csproj:39-43`; `VPNRouter.Tests/VPNRouter.Tests.csproj:53-54` |
| Avalonia | Android | **12.0.3** | [FACT] `VPNRouter.Android/VPNRouter.Android.csproj:132-135` |
| SkiaSharp | Desktop | `3.119.4` stable (прямая ссылка) | [FACT] `VPNRouter.App/VPNRouter.App.csproj:51` |
| SkiaSharp | Android | `3.119.4-preview.1.1` (транзитивный пин Avalonia 12.0.3) | [FACT] комментарий `VPNRouter.App/VPNRouter.App.csproj:48` |

Upstream-референс для sing-box: **1.13.14**. Fork базируется на 1.13.13, Android
libbox — на 1.13.10; оба ниже референса.

## Ранжированные находки

Ранг = вероятность × тяжесть эффекта при условии, что exposure сработает. Все
пункты — exposure, не подтверждённый пользовательский баг.

### F1. TUN system-stack TCP NAT collision + route-address pollution — CONFIRMED EXPOSURE (measure-first)

- **[FACT]** Upstream 1.13.14 коммит `0b7ffba` фиксит коллизию TCP NAT в system-стеке
  TUN и загрязнение route-address.
  Primary: https://github.com/SagerNet/sing-box/commit/0b7ffba
- **[FACT]** Windows и Linux выбирают `system` TUN-стек и заполняют
  `RouteExcludeAddress`: `VPNRouter.Core/Services/ConfigGenerator.cs:39-40`
  (`SelectTunStack(isMacOS) => isMacOS ? "gvisor" : "system"`),
  `:1129` (`GetEffectiveRouteExcludeAddress`), `:1148-1164` (присвоение
  `RouteExcludeAddress` + `Stack = SelectTunStack(...)`).
- **[FACT]** macOS использует `gvisor` (`ConfigGenerator.cs:39-40`) → **не подвержена
  system-stack половине** этого фикса.
- **[INFER]** Fork 1.13.13-lx-awg не содержит `0b7ffba` → Windows/Linux теоретически
  могут ловить TCP NAT-коллизию/грязные route при определённых раскладках адресов.
  Реальная частота и пользовательский эффект неизвестны.
- **Статус:** confirmed version/code exposure; **user impact требует A/B до фикса.**

### F2. DNS loopback shared-dedup deadlock — MEASURE FIRST

- **[FACT]** Upstream 1.13.14 коммит `72a8723` фиксит deadlock в shared-дедупликации
  DNS loopback-запросов.
  Primary: https://github.com/SagerNet/sing-box/commit/72a8723
- **[FACT]** Этому фиксу не хватает ни shipped fork (1.13.13), ни Android libbox (1.13.10).
- **[FACT]** Локальная DNS-топология много-серверная и loopback-нагруженная:
  `vpn-dns` (DoH, `DomainResolver = "local-dns"`) / `local-dns` / `dns-system`
  (OS-resolver) / `dns-direct`, плюс `hijack-dns` route-правило и loopback
  slipstream-endpoint'ы: `ConfigGenerator.cs:1033-1036` (`dns-system`),
  `:1172-1174` (hijack-dns), `:1451-1454` (`dns-direct`), `:1561-1571`
  (loopback `127.0.0.1` dns-tunnel outbound), `:2137-2167` (`BuildVpnDnsServer`,
  `DomainResolver = "local-dns"`).
- **[INFER]** Топология совпадает с профилем, на котором upstream-фикс значим,
  но deadlock — вероятностный; без нагрузки не наблюдается.
- **Статус:** **MEASURE FIRST** — sustained mixed-DNS workload на fork vs upstream
  1.13.14 до какого-либо решения.

### F3. Android libbox 1.13.10: DNS connection-pool leak + DNS deadline — MEASURE FIRST

- **[FACT]** Android libbox = sing-box 1.13.10 (`build-android.ps1:48`); ему не хватает
  фикса утечки DNS connection-pool `d166f0d` (1.13.12) и фикса DNS deadline `6548c17`.
  Primary: https://github.com/SagerNet/sing-box/commit/d166f0d ,
  https://github.com/SagerNet/sing-box/commit/6548c17
- **[FACT]** Android использует тот же DoH `vpn-dns`-транспорт (общий
  `ConfigGenerator`), так что DNS-путь релевантен.
- **[INFER]** При длительной нагрузке возможны рост памяти/сокетов и DNS-stall'ы;
  без замера это гипотеза.
- **Статус:** **MEASURE FIRST** — память/сокеты + DNS-stall'ы под sustained load;
  при подтверждении — ротация libbox на проверенный более новый upstream,
  НЕ локальные workaround'и.

### F4. Clash log subscriber-level fix — CONFIRMED EXPOSURE (A/B)

- **[FACT]** Upstream 1.13.14 коммит `6397675` фиксит обработку на уровне
  подписчика Clash-лога.
  Primary: https://github.com/SagerNet/sing-box/commit/6397675
- **[FACT]** Shipped fork не содержит фикса. Локальный `ClashLogStream` подписывается
  на `/logs?level=info` и питает health/failover:
  `VPNRouter.Core/Services/ClashLogStream.cs:93`
  (`return new Uri($"{scheme}://{uri.Authority}/logs?level=info{token}")`).
- **[INFER]** Возможна разница в событиях/их числе, влияющая на health-сигналы;
  эффект нужно измерить, не додумывать.
- **Статус:** confirmed code/version exposure; **behavior impact требует A/B
  event-level/count сравнения.**

### F5. Android Avalonia 12.0.3 → 12.0.4 — НЕ дефект; контролируемый upgrade-validation

- **[FACT]** Desktop на Avalonia 12.0.4, Android на 12.0.3 (см. матрицу).
  Official 12.0.4 release: https://github.com/AvaloniaUI/Avalonia/releases/tag/12.0.4
- **[FACT]** Релевантные фиксы после 12.0.3:
  - TalkBack PointToScreen — https://github.com/AvaloniaUI/Avalonia/pull/21402
  - cached bidi double reorder — https://github.com/AvaloniaUI/Avalonia/pull/21351
  - ItemTemplate compiled-binding DataContext inference — https://github.com/AvaloniaUI/Avalonia/pull/21248
  - SkiaSharp stable 3.119.4 update — https://github.com/AvaloniaUI/Avalonia/pull/21434
- **[FACT]** Android сейчас разрешает транзитивный SkiaSharp `3.119.4-preview.1.1`
  (пин Avalonia 12.0.3); desktop уже на stable `3.119.4`.
- **[INFER]** Расхождение версий само по себе не баг. Фиксы 12.0.4 потенциально
  улучшают TalkBack/bidi/ItemTemplate на Android, но это надо проверить, а не
  предполагать.
- **Статус:** **не дефект.** Записать контролируемое направление upgrade-validation
  (build → RU/EN bidi visual → TalkBack → ItemTemplate-экраны → lifecycle smoke);
  keep/revert по итогу evidence.

## Валидация (минимальные эксперименты)

Общий принцип: **measure-first.** Никакой имплементации до результата.

- **F1 (TUN):** A/B двух сборок (fork 1.13.13 vs upstream 1.13.14) на Windows/Linux
  с `system`-стеком: прогнать трафик, дающий пересечение TUN route-address с
  реальными маршрутами, + длительный TCP-поток; снять sing-box-лог на предмет
  NAT-коллизии/грязных route. macOS (gvisor) — вне этого эксперимента.
- **F2 (DNS deadlock):** sustained mixed-DNS workload (DoH vpn-dns + local-dns +
  dns-system + hijack-dns + loopback slipstream) на fork vs upstream 1.13.14;
  смотреть зависания/дедлоки DNS-запросов.
- **F3 (Android DNS):** sustained load на Android-сборке; замер памяти/сокетов +
  DNS-stall'ы; сравнить libbox 1.13.10 vs кандидата на ротацию.
- **F4 (Clash log):** A/B event-level/count сравнение подписки `/logs?level=info`
  на fork vs upstream 1.13.14; сверить, что health/failover видит те же события.
- **F5 (Avalonia):** контролируемый upgrade Android до 12.0.4 → build + RU/EN bidi
  visual, TalkBack, ItemTemplate-экраны, lifecycle smoke; keep/revert по evidence.

## Remediation (минимальное направление)

Явно **без** широких переписываний и dependency-harmonization.

- **F1/F2/F4 (desktop core):** при подтверждении эффектом — ротация базы fork'а
  sing-box-lx на проверенный более новый upstream (к 1.13.14+), с сохранением
  текущих build-time патчей (AWG/XHTTP/WSAEFAULT/H4) и re-pin коммита. Точечное
  действие, не переписывание `ConfigGenerator`.
- **F3 (Android):** при подтверждении — ротация libbox на проверенный более новый
  upstream-тег; НЕ локальные workaround'и в Java/Core.
- **F5 (Android Avalonia):** точечный bump `VPNRouter.Android.csproj` 12.0.3 → 12.0.4
  (и синхронный SkiaSharp-переход на stable, если upgrade оставим), только после
  положительного validation; иначе revert.
- **Явно НЕ добавлять:** Serilog/YAML/HttpClient-рефакторы и прочую
  dependency-harmonization — они не обоснованы этим исследованием.

## Опровергнуто / non-actionable

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
  `### Internet-optimization research — 2026-08-01` (все четыре actionable
  направления, measure-first gate).
- Смежное: SDR/realtime-games кластер в `OPEN-DEFECTS.md` (AWG WSAENOBUFS/MTU) —
  не пересекать: тот кластер про lx-core bind/MTU, этот — про upstream sing-box
  фиксы 1.13.11–1.13.14 и Avalonia.
