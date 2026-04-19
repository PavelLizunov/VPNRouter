# VPNRouter — Roadmap v2.14.11 → v2.15.x

**Baseline**: v2.14.10 stable (Free Configs tab fully landed).

**Goal**: Устранить 4 пула доработок из пользовательской сессии:
1. Автозапуск — компоненты стартуют ненадёжно после перезагрузки, нет статуса
2. Проверка конфигов в Servers/Subscriptions (как в Free)
3. UI ревью — обрезание, нелогичное расположение
4. Локализация — пробелы на некоторых страницах

---

## Priority order (execute top-to-bottom)

### First wave (critical for user workflow)
1. **v2.14.11** — Autostart retry + backoff (fix the "не стартует после перезагрузки")
2. **v2.14.12** — Service start-order: Windows Service dependencies на `Tcpip`/`Dnscache` + Delayed Autostart
3. **v2.15.0** — Status dashboard: 3 индикатора в хедере + единый "runtime overview"

### Second wave (feature parity)
4. **v2.15.1** — Refactor: extract `IConnectivityTester` from `FreeConfigTester` (generic for Servers/Subs/Free)
5. **v2.15.2** — Test button + latency/bandwidth columns in Servers tab
6. **v2.15.3** — Test button + columns in Subscriptions tab
7. **v2.15.4** — Deep verify (bandwidth) для Servers/Subs через spawned sing-box

### Third wave (polish)
8. **v2.15.5** — UI audit: собрать скриншоты всех страниц + список проблем
9. **v2.15.6** — UI fixes (обрезания, перестановки, консистентность)
10. **v2.15.7** — Tooltips + "?" help icons на неочевидных элементах

### Closing pass
11. **v2.15.8** — Localization audit (grep hardcoded strings)
12. **v2.15.9** — Localization fixes + RU/EN smoke test на каждой странице

---

# v2.14.11 — Autostart retry + backoff

**Goal**: после перезагрузки все 3 компонента (VPN, Zapret, TGProxy) поднимаются автоматически, с retry при временных сбоях.

## Problem analysis

Симптомы у пользователя:
- VPN не поднялся после reboot — вероятно TUN adapter fail (network stack не готов при старте сервиса)
- Zapret и tgproxy стартовали со второй попытки — значит retry нет, первая попытка падает молча

Возможные причины первой неудачи:
- Network stack ещё инициализируется (0-30s после boot)
- `wintun.dll` adapter create возвращает transient error
- sing-box не нашёл TAP/TUN interface
- Zapret winws зависит от WinDivert driver — может быть не загружен
- TGProxy слушает на localhost, но loopback может ещё не быть поднят

## Files to analyze (before changing)

```
VPNRouter.Core/Services/
├── VpnEngine.cs              — StartAsync(), есть ли retry?
├── SingBoxManager.cs         — LaunchProcess(), как обрабатывает transient errors
├── ZapretManager.cs          — Start(), retry отсутствует точно (проверить)
└── TelegramProxy/TgProxyManager.cs — Start(), retry?

VPNRouter.Service/
└── VPNRouterService.cs       — StartAsync() в BackgroundService, порядок вызовов
```

## Proposed changes

### Generic retry helper
```csharp
// VPNRouter.Core/Services/ResilientStarter.cs (new file)
public static class ResilientStarter
{
    public static async Task<bool> StartWithBackoffAsync(
        string componentName,
        Func<CancellationToken, Task<bool>> startFn,
        int maxAttempts = 4,
        int[] backoffSeconds = null,
        ILogger logger = null,
        CancellationToken ct = default)
    {
        backoffSeconds ??= new[] { 5, 10, 20, 40 };
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (await startFn(ct))
                {
                    logger?.LogInformation("{Component} started on attempt {Attempt}", componentName, attempt + 1);
                    return true;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                logger?.LogWarning(ex, "{Component} start attempt {Attempt} failed", componentName, attempt + 1);
            }
            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds[attempt]), ct);
            }
        }
        logger?.LogError("{Component} failed to start after {MaxAttempts} attempts", componentName, maxAttempts);
        return false;
    }
}
```

### Integration points
- `VpnEngine.StartAsync()` → оборачиваем `SingBoxManager.Launch()` в ResilientStarter
- `ZapretManager.Start()` → то же самое на winws.exe launch
- `TgProxyManager.Start()` → то же на listener bind

### Logging
Каждая попытка должна писать в `vpnrouter{date}.log`:
```
[RESTART] VPN attempt 1/4 failed: TUN adapter create returned ERROR_NOT_READY, retrying in 5s
[RESTART] VPN attempt 2/4 failed: same error, retrying in 10s
[RESTART] VPN started on attempt 3
```

## Testing
- Reboot машины с `VPNRouter.Service` установленным + включённым автозапуском всех 3
- Проверить лог: все 3 должны показать "started on attempt N" где N >= 1
- Если всегда N=1 → попробовать reboot с быстрым стартом (hibernation wake)
- Negative test: замокать permanent failure → должно залогировать `failed after 4 attempts` и пометить компонент как `Failed` в state

## Risks
- Backoff 5+10+20+40 = 75s worst case — может раздражать на машинах где первая попытка всегда проходит; параметр должен быть конфигурируемым
- Если sing-box упал из-за плохого конфига (не transient) — 4 попытки бесполезны. Нужен whitelist retriable errors: `ERROR_NOT_READY`, `timeout`, `address already in use` (TIME_WAIT). Fatal config errors (`FATAL start service:`) — fail fast.

## Acceptance
- [ ] После reboot все 3 компонента поднимаются без ручного вмешательства (5 тестовых reboot-ов)
- [ ] Лог показывает фактические попытки и причины неудач
- [ ] Unit test на ResilientStarter (happy path + fail path + cancel)

---

# v2.14.12 — Service start-order dependencies

**Goal**: Windows Service должен стартовать ПОСЛЕ того как сеть поднялась, а не параллельно с ней.

## Problem

`VPNRouter.Service` сейчас — `SERVICE_AUTO_START` (или `SERVICE_DELAYED_AUTO_START`). Но даже с delayed start может запуститься до того как `Tcpip`/`Dnscache`/`Dhcp` полностью готовы.

## Changes

### `VPNRouter.Service/ServiceInstaller.cs`
При install передавать dependencies:
```bash
sc create VPNRouter ... depend= Tcpip/Dnscache/Dhcp start= delayed-auto
```

В `ServiceInstaller.RunSc` добавить параметр:
```csharp
private static int InstallService()
{
    var args = new[]
    {
        "create", ServiceName,
        $"binPath= \"{exePath} --service\"",
        "start= delayed-auto",
        "depend= Tcpip/Dnscache/Dhcp",  // NEW
        $"DisplayName= \"{DisplayName}\"",
    };
    // ...
}
```

### Config option: "Wait for network" (GUI)
В Settings добавить:
- `StartupDelay` (секунд, default 0) — дополнительный sleep перед стартом компонентов
- `WaitForInternet` (bool, default true) — пинговать `1.1.1.1` TCP:443 до успеха (max 60s) перед стартом VPN

## Testing
- Reboot + проверить `sc qc VPNRouter` показывает DEPENDENCIES
- Cold boot (не hibernation) — должен запуститься после network ready
- Отключить интернет, включить VPNRouter service → ждёт до появления связи, потом стартует

## Acceptance
- [ ] `sc qc VPNRouter` → DEPENDENCIES: `Tcpip, Dnscache, Dhcp`
- [ ] Cold boot тест: VPN успешно поднимается с первой попытки
- [ ] WaitForInternet toggle виден в Settings

---

# v2.15.0 — Status dashboard

**Goal**: пользователь видит состояние всех 3 компонентов с первого взгляда, не ныряя в Tools/Zapret или Tools/TGProxy.

## Design

### Header indicators
В хедере (рядом с Dark/Light toggle + Language):
```
[🟢 VPN]  [🟢 Zapret]  [⚪ TGProxy]
```
- 🟢 = running
- 🟡 = starting / retry in progress
- 🔴 = failed (с tooltip причины)
- ⚪ = disabled (не включён в настройках)

Click → переход на соответствующую вкладку.

### Main page "Overview" section (optional, на Servers tab сверху)
Карточки (как в Free Configs Overview):
```
┌─ VPN ────────┬─ Zapret ─────┬─ TGProxy ────┐
│ 🟢 Connected │ 🟢 Running   │ ⚪ Disabled  │
│ config-name  │ Discord+YT   │              │
│ 192.168.1.5  │ strategy alt3│              │
│ uptime 2h 14m│ uptime 2h 14m│              │
└──────────────┴──────────────┴──────────────┘
```

## Files

### New: `VPNRouter.App/ViewModels/RuntimeStatusViewModel.cs`
```csharp
public partial class RuntimeStatusViewModel : ObservableObject
{
    [ObservableProperty] private ComponentStatus _vpnStatus = ComponentStatus.Idle;
    [ObservableProperty] private ComponentStatus _zapretStatus = ComponentStatus.Idle;
    [ObservableProperty] private ComponentStatus _tgProxyStatus = ComponentStatus.Idle;
    [ObservableProperty] private string? _vpnDetail;      // "config-name (192.168.1.5)"
    [ObservableProperty] private string? _zapretDetail;   // "Discord+YT alt3"
    [ObservableProperty] private string? _tgProxyDetail;  // "127.0.0.1:1082"
    [ObservableProperty] private TimeSpan? _vpnUptime;
    // ... uptime для остальных
    
    // Subscribe to events from VpnEngine, ZapretManager, TgProxyManager
    // Update properties on UI thread
}
```

### New: `VPNRouter.App/Controls/RuntimeStatusBadge.axaml`
Reusable UserControl: иконка + текст + цвет. Используется в хедере и в Overview карточках.

### Modified: `MainWindow.axaml`
Добавить блок индикаторов в header.

### Events
`VpnEngine`, `ZapretManager`, `TgProxyManager` должны поднять `event EventHandler<StatusChangedEventArgs> StatusChanged` (если ещё нет). RuntimeStatusViewModel subscribes.

## Acceptance
- [ ] Хедер показывает 3 индикатора, цвет соответствует реальному состоянию
- [ ] Click по индикатору переключает Tab
- [ ] Tooltip на 🔴 содержит причину (последняя ошибка из лога)
- [ ] При старте retry (v2.14.11) показывает 🟡 с "attempt N/4"
- [ ] Overview карточки на Servers tab (если решим добавлять)

## Risks
- Нужен способ передать данные из Core → App VM без circular dep. Use events + weak subscription.
- Polling vs events: events предпочтительнее, но нужно убедиться что все 3 менеджера их эмитят корректно.

---

# v2.15.1 — Refactor: generic IConnectivityTester

**Goal**: подготовить почву для тестирования Servers/Subscriptions, переиспользуя логику FreeConfigTester.

## Current state

- `FreeConfigTester.cs` — принимает `FreeConfigEntry`, тестирует TCP+TLS
- `FreeConfigDeepVerifier.cs` — принимает `FreeConfigEntry`, спавнит sing-box + HTTP тест

Но `FreeConfigEntry` specific для Free tab (url, sourceName, status enum и т.д.).

## Refactor

### New abstraction
```csharp
// VPNRouter.Core/Services/Connectivity/IConnectivityTarget.cs
public interface IConnectivityTarget
{
    string Host { get; }
    int Port { get; }
    string? Sni { get; }
    string VlessOutboundJson { get; }  // serialized sing-box outbound config
}

// VPNRouter.Core/Services/Connectivity/ConnectivityTestResult.cs
public sealed record ConnectivityTestResult(
    bool TcpOk,
    int? TcpLatencyMs,
    bool TlsOk,
    int? TlsLatencyMs,
    bool HttpOk,
    int? HttpLatencyMs,
    double? BandwidthMbps,
    string? ErrorMessage);
```

### Implementations
- `FreeConfigEntry implements IConnectivityTarget` (adapter)
- `ServerListItem implements IConnectivityTarget`
- `SubscriptionServer implements IConnectivityTarget`

### Generic tester
```csharp
// VPNRouter.Core/Services/Connectivity/ConnectivityTester.cs
public sealed class ConnectivityTester
{
    public async Task<ConnectivityTestResult> TestAsync(
        IConnectivityTarget target,
        ConnectivityTestOptions options,
        CancellationToken ct) { ... }
}

// FreeConfigTester.cs → internal wrapper, calls ConnectivityTester
```

## No behavior change in v2.15.1
Free Configs tab должен работать идентично — refactor только под капотом.

## Acceptance
- [ ] Free Configs работает без изменений
- [ ] `IConnectivityTarget` покрыт unit-тестами
- [ ] Все 3 модели (FreeConfigEntry, ServerListItem, SubscriptionServer) имплементируют интерфейс

---

# v2.15.2 — Test button in Servers tab

**Goal**: пользователь может протестировать свои сохранённые VLESS сервера без переключения на Free.

## UI changes

### `VPNRouter.App/Views/Pages/ServersPage.axaml`
Добавить колонки:
- ⏱ Ping (ms)
- 📶 Status (🟢 Ok / 🟡 Slow / 🔴 Failed)
- Last tested

Добавить кнопки:
- "Test" (per-row) — тестирует одну строку
- "Test all" — тестирует все
- Sort by ping (ascending)

### `ServersPageViewModel`
```csharp
[ObservableProperty] private bool _isTestingAll;
[ObservableProperty] private int _testProgress;

[RelayCommand]
private async Task TestServer(ServerListItemViewModel item)
{
    item.TestStatus = TestStatus.Testing;
    var result = await _connectivityTester.TestAsync(item, ..., ct);
    item.ApplyResult(result);
}

[RelayCommand]
private async Task TestAll() { ... }
```

## No deep verify yet
Только TCP+TLS (быстро, ~2s/сервер). Deep verify с bandwidth — в v2.15.4.

## Acceptance
- [ ] Test button виден на каждом VLESS сервере
- [ ] Статусы/пинги сохраняются в `AppSettings` (чтобы не пропадали между запусками)
- [ ] "Test all" — progress bar + Cancel
- [ ] Sort by ping работает

---

# v2.15.3 — Test button in Subscriptions tab

**Goal**: то же самое для серверов из подписок.

## Similar to v2.15.2
`SubscriptionsPageViewModel` + `SubscriptionsPage.axaml`:
- Per-row Test button
- "Test all" per-subscription
- Колонки Ping/Status/Last tested

## Edge case: auto-refresh подписки
Когда подписка обновляется (24h cron или ручной refresh), список серверов меняется. Решение:
- Тестовые результаты хранятся в по `(host, port, id)` ключу
- После refresh серверы с совпадающим ключом сохраняют статус, новые — Unknown

## Acceptance
- [ ] Test в Subscriptions работает так же как в Servers
- [ ] После refresh подписки старые результаты не теряются для пересекающихся серверов

---

# v2.15.4 — Deep verify for Servers/Subscriptions

**Goal**: измерять bandwidth и HTTP-доступность для своих серверов, не только public Free.

## UI
- Кнопка "Deep verify" рядом с "Test all"
- Выбор preset: Gaming / Streaming / Chat / Best (как в Free)
- Список только top-N после базового TCP+TLS теста

## Files
- Переиспользуем `FreeConfigDeepVerifier` через `IConnectivityTarget` (после v2.15.1 refactor)
- Добавить в каждую VM счётчик verified/tested/total + progress bar

## Acceptance
- [ ] Deep verify работает на Servers и Subs идентично Free
- [ ] Результаты с bandwidth сохраняются в AppSettings
- [ ] Sort by bandwidth работает

---

# v2.15.5 — UI audit

**Goal**: собрать полный список проблемных мест UI для систематического фикса.

## Deliverable
Markdown-файл `.claude/plans/v2.15-ui-audit.md` со списком:
```markdown
## Servers page
- [ ] "Add server" button обрезается при width < 800px
- [ ] Колонка "Config" переполняется для длинных имён
- [ ] Tooltip на Connect button не показывает текущий статус

## Subscriptions page
- [ ] ...

## Applications page
- [ ] ...
```

## Process
1. Запустить app, сделать скриншот каждой из 8 страниц в 3 разрешениях (1280×800 минимум, 1920×1080 дефолт, 2560×1440 максимум)
2. Пройтись по каждому скриншоту, отметить проблемы
3. Также проверить Dark и Light темы
4. Проверить RU и EN локализацию (иногда RU текст длиннее и вызывает обрезание)

## Acceptance
- [ ] UI audit file содержит >= 20 пунктов (ориентир, не минимум)
- [ ] Каждый пункт привязан к конкретной странице + разрешению + теме
- [ ] Приоритезировано (Critical/Major/Minor)

---

# v2.15.6 — UI fixes

**Goal**: починить все Critical и Major из audit.

## Approach
- Один коммит = одна конкретная проблема (чтобы было легко откатить если сломали что-то)
- После каждого фикса — визуальная проверка все 3 разрешения × 2 темы × 2 языка

## Common fix patterns
- Overflow → `TextTrimming="CharacterEllipsis"` + Tooltip с полным текстом
- Fixed widths → `MinWidth` + `*` star sizing
- Button groups обрезаются → wrapping в `WrapPanel`
- Long RU text в tabs → icon + tooltip вместо длинного title

## Acceptance
- [ ] Все Critical фикшены
- [ ] Все Major фикшены
- [ ] Minor перенесены в отдельный backlog (или добавлены в v2.15.7)

---

# v2.15.7 — Tooltips + help icons

**Goal**: сделать неочевидные элементы самодокументирующимися.

## Scope
- "?" icon рядом с каждой нетривиальной опцией в Settings
- Tooltip объясняет что опция делает + когда включать/выключать
- На первой странице (Servers) — dismissible quickstart banner (как в Free) для новых пользователей

## Examples
- "Block on VPN fail" → tooltip: "Блокирует трафик выбранных приложений если VPN упал, чтобы не было утечки в открытую сеть"
- "Kill switch" → tooltip: "Разрывает ВСЮ сеть при падении VPN (отличается от Block on VPN fail, который блокирует только выбранные приложения)"

## Acceptance
- [ ] >= 15 tooltips добавлено в критичные места
- [ ] Quickstart banner на главной странице для first-run пользователей
- [ ] Локализация tooltips — RU и EN

---

# v2.15.8 — Localization audit

**Goal**: найти все hardcoded строки, составить список страниц с пробелами.

## Process
```bash
# Grep hardcoded English/Russian в axaml
grep -rn 'Text="[A-Za-zА-Яа-я]' VPNRouter.App/Views/ \
  | grep -v 'x:Static' \
  | grep -v 'Binding'

# Grep в C# (fallback strings)
grep -rn '"[А-Яа-я][^"]\{5,\}"' VPNRouter.App/ VPNRouter.Core/ \
  | grep -v 'Strings\.' \
  | grep -v '//'
```

## Deliverable
`.claude/plans/v2.15-localization-audit.md`:
```markdown
## FreeConfigsPage.axaml
- Line 123: `Text="Проверка..."` — hardcoded, нужно Strings.FcTesting
- Line 456: `ToolTip.Tip="Deep verify"` — нужна локализация

## SettingsPage.axaml
- ...
```

## Acceptance
- [ ] Audit file содержит все найденные hardcoded строки
- [ ] Оценка объёма работы (кол-во строк для перевода)

---

# v2.15.9 — Localization fixes

**Goal**: закрыть все пробелы из audit, smoke test обеих языковых версий.

## Process
- Для каждой hardcoded строки:
  1. Добавить property в `Strings.cs` (RU + EN)
  2. Заменить в axaml/C# на `Strings.XxxYyy`
  3. Визуально проверить что текст рендерится на обоих языках
- Smoke test: переключить язык → пройти все 8 страниц + все диалоги → ничего не осталось на другом языке

## Acceptance
- [ ] 0 hardcoded UI строк (grep выше возвращает пустоту)
- [ ] Smoke test пройден для RU и EN
- [ ] Нет overflow из-за длины RU текста (если есть — добавить в v2.15.6 backlog)

---

## Operational notes (для Claude при implementation)

### Ветвление и коммиты
- Работаем на `main` как обычно (нет feature branches в этом проекте)
- 1 релиз = 1 связная серия коммитов, в конце версия + release

### Обязательно после каждого релиза
1. Bump `VPNRouter.Core/AppVersion.cs`
2. `dotnet build VPNRouter.sln` → 0 errors
3. Commit specific files (НЕ `git add -A`)
4. `git push origin main && git push github main`
5. `build.ps1 -Version "X.Y.Z" -Upload`
6. `gh release edit vX.Y.Z --prerelease --notes "..."`
7. Wait for macOS CI (~50s)
8. После user approval → `gh release edit vX.Y.Z --prerelease=false --latest`

### Регрессии — красная линия
- Любая регрессия в существующей работе Free Configs = блокер release
- Любая регрессия в VPN startup (v2.14.11) = блокер (это наоборот, то что чиним)

### Как принимать решения по приоритетам
- Если v2.14.11 не решает проблему с автозапуском за 2 коммита → перейти в investigation режим, не продолжать с v2.14.12+ пока не понятно root cause
- Если v2.15.1 refactor ломает Free Configs → rollback, сделать его позже меньшими шагами

### Откладываемое
Не входит в этот roadmap, но упомянуто в памяти:
- tg-ws-proxy C# rewrite (`memory/tg-ws-proxy-rewrite.md`)
- Отдельный документ, не трогаем пока эти 4 блока не закрыты

---

## Summary table

| Version  | Block | Deliverable                              | Est. effort |
|----------|-------|------------------------------------------|-------------|
| v2.14.11 | 1     | ResilientStarter + 3 call sites + tests  | M           |
| v2.14.12 | 1     | Service deps + WaitForInternet toggle    | S           |
| v2.15.0  | 1     | Status dashboard (header + overview)     | L           |
| v2.15.1  | 2     | Refactor IConnectivityTarget             | M           |
| v2.15.2  | 2     | Servers tab testing UI                   | M           |
| v2.15.3  | 2     | Subscriptions tab testing UI             | S           |
| v2.15.4  | 2     | Deep verify для Servers/Subs             | M           |
| v2.15.5  | 3     | UI audit document                        | S           |
| v2.15.6  | 3     | UI fixes (Critical + Major)              | L           |
| v2.15.7  | 3     | Tooltips + help icons                    | M           |
| v2.15.8  | 4     | Localization audit                       | S           |
| v2.15.9  | 4     | Localization fixes + smoke test          | M           |

Legend: S = 1-2h, M = 3-5h, L = 1-2 days

---

## Status tracker

- [x] v2.14.11 — Autostart retry  ← shipped in v2.15.0 (merged release)
- [x] v2.14.12 — Service dependencies  ← shipped in v2.15.0 (merged release)
- [x] v2.15.0  — Status dashboard  ← **SHIPPED 2026-04-19 as prerelease, awaiting user test**
- [x] v2.15.1  — hotfix: dashboard navigation + IsConnected sync (shipped 2026-04-19)
- [x] v2.15.2  — TcpTlsProbe + Servers/Subscriptions testing UI (shipped 2026-04-19, **awaiting user test**)
- [x] v2.15.3  — Deep verify (bandwidth) для Servers/Subs via spawned sing-box (shipped 2026-04-19, **Block 2 DONE, awaiting user test**)
- [x] v2.15.4  — Block 3: UI audit + polish + tooltips for Settings toggles (shipped 2026-04-19)
- [x] v2.15.5  — Block 4: localization pass, ~30 strings (shipped 2026-04-19, **Block 3+4 DONE, awaiting user test**)

Обновлять этот чеклист по мере продвижения (чтобы пережить context compact).
