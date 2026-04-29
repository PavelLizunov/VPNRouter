# v2.29 — Mac UX feedback + custom direct rules

## Триггер

User report (Mac тестер, 2026-04-29) — 4 пункта от живого использования
после v2.28.7 stable cut:

1. **Simple page autostart card** показывает «Настроить автозапуск VPN
   при старте Windows» хотя пользователь на Mac.
2. **Advanced → Network → Autostart** показывает «Autostart is currently
   available on Windows only. Linux (systemd) and macOS (launchd) support
   is planned for future releases.» — пора реализовать.
3. **Applications page** при `RoutingMode=full` целиком блокируется
   (`IsEnabled="{Binding IsSplitTunnel}"`) без объяснения. Юзер думает
   что приложение сломано.
4. **Routing settings** — пользователь хочет «расширенную настройку
   конфига, у меня есть кейсы с WireGuard где хотелось бы самому
   прописывать direct правила».

## Симптом → код → fix (по каждому пункту)

### 1. Hardcoded "Windows" в SmpAutostartCardOff

**Симптом**: Mac/Linux юзер видит «при старте Windows» в Simple-mode
карточке.

**Где**: `VPNRouter.App/Localization/Strings.cs` lines 165-173:

```csharp
public static string SmpAutostartCardOff => Ru
    ? "Настроить автозапуск VPN при старте Windows"
    : "Configure VPN autostart at Windows boot";
```

**Также проверить** другие SmpAutostart*/AutostartWith* строки —
аналогичная проблема может быть везде где упоминается «Windows».

**Fix** (минимальный, без смены семантики):
- Заменить «Windows» на platform-agnostic «system» в EN, «системы» в RU.
- Или динамически: `OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux"`.

Pragmatic выбор: динамика. Юзер видит точное имя своей ОС → ощущение
"приложение знает где я".

```csharp
public static string SmpAutostartCardOff
{
    get
    {
        var os = OperatingSystem.IsWindows() ? "Windows"
               : OperatingSystem.IsMacOS()  ? "macOS"
               : "Linux";
        return Ru ? $"Настроить автозапуск VPN при старте {os}"
                  : $"Configure VPN autostart at {os} boot";
    }
}
```

Применить аналогично к:
- `AutostartWithWindows` (line 652) → `AutostartWithSystem` или
  динамический.
- `SmpAutostartLabel` (line 764) — «Запускать вместе с Windows».
- Любой другой текст где зашит «Windows».

**Acceptance**: открыть Simple page на Mac → autostart карточка пишет
«при старте macOS» / «at macOS boot». Аналогично на Linux.

---

### 2. Mac/Linux autostart implementation

**Симптом**: Advanced → Network → Autostart показывает notice
«Autostart is currently available on Windows only».

**Где**:
- `VPNRouter.App/Localization/Strings.cs` line 837 —
  `AutostartPlatformNotice`.
- Какой-то `IsAutostartSupported` или similar flag в VM (надо найти).

**Архитектура** (по платформам):

#### macOS — LaunchAgent

Standard pattern: drop a `.plist` в `~/Library/LaunchAgents/`. Пример:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "...">
<plist version="1.0">
<dict>
  <key>Label</key>                 <string>com.ninitux.vpnrouter</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Applications/VPNRouter.app/Contents/MacOS/VPNRouter</string>
    <string>--autostart</string>
  </array>
  <key>RunAtLoad</key>             <true/>
  <key>KeepAlive</key>             <false/>
  <key>ProcessType</key>           <string>Interactive</string>
</dict>
</plist>
```

`launchctl load` для enable, `launchctl unload + rm` для disable.
NOT root-level (`/Library/LaunchDaemons/`) — запускаем UI-app, не sing-box
напрямую. UI-app сам запустит sing-box через NOPASSWD sudo (уже работает
из v2.28.6-r6).

API:
- `MacAutostartManager.Install(): Task<bool>` — пишет .plist + launchctl load.
- `MacAutostartManager.Uninstall(): Task<bool>` — launchctl unload + delete.
- `MacAutostartManager.IsInstalled(): bool` — `File.Exists(plistPath)`.

#### Linux — XDG autostart desktop file

Standard pattern: `~/.config/autostart/vpnrouter.desktop`:

```ini
[Desktop Entry]
Type=Application
Name=VPNRouter
Exec=/usr/bin/vpnrouter --autostart
X-GNOME-Autostart-enabled=true
NoDisplay=false
Hidden=false
```

Простейший подход — XDG autostart работает в GNOME / KDE / XFCE / etc.
Альтернатива — systemd user service (`~/.config/systemd/user/vpnrouter.service`),
больше шансов запустить headless, но сложнее.

Рекомендация: **XDG autostart** для v2.29 (90% юзеров на DE с
graphical session).

API такой же как Mac: Install / Uninstall / IsInstalled.

#### Auto-start VPN on app launch

Когда юзер ставит галочку «Auto-start VPN with system», нам надо чтобы
не только UI запустился, но и tunnel поднялся автоматически. Это уже
есть в Windows: `--autostart` CLI flag → `MainWindowViewModel`
читает → запускает `_engine.StartAsync` без user-click.

`MainWindow.xaml.cs` (или `Program.cs`) уже парсит `--autostart` —
надо проверить cross-platform.

**Acceptance**:
- На Mac toggle Autostart → osascript prompt → .plist файл создан.
- Logout → login → VPNRouter автоматически в трее.
- На Linux toggle Autostart → .desktop файл создан в
  ~/.config/autostart/.
- Login → VPNRouter автоматически в трее.
- AutostartPlatformNotice удалён — теперь поддержка везде.

---

### 3. Applications page блокировка при full-tunnel

**Симптом**: при `RoutingMode=full` весь content на Apps page disabled.
Юзер не понимает почему.

**Где**: `VPNRouter.App/Views/Pages/ApplicationsPage.axaml` line 82-83:

```xml
<Grid ColumnDefinitions="120,*"
      IsEnabled="{Binding IsSplitTunnel}">
```

**Логика**: при full tunnel выбор приложений игнорируется (всё идёт
через VPN), поэтому контролы заблокированы.

**Проблема**: silent disable даёт ощущение «сломано». Нет hint что
происходит и как исправить.

**Fix варианты**:

**A. Заменить disable на оверлей с объяснением.**
   Контролы остаются интерактивными визуально, но поверх — карточка
   «You're in full-tunnel mode. App selection isn't used. Switch to
   split tunnel to enable.» с кнопкой «Switch to split tunnel» которая
   меняет RoutingMode.

```xml
<Grid>
  <Grid ColumnDefinitions="120,*"
        IsEnabled="{Binding IsSplitTunnel}"
        Opacity="{Binding IsSplitTunnel, Converter={StaticResource BoolTo10or05Converter}}">
    <!-- existing apps content -->
  </Grid>

  <!-- Overlay: shown only on full tunnel -->
  <Border IsVisible="{Binding IsFullTunnel}"
          HorizontalAlignment="Center" VerticalAlignment="Center"
          Background="{DynamicResource SurfaceRaisedBrush}"
          CornerRadius="{StaticResource RadiusMd}"
          Padding="20,16" MaxWidth="380"
          BorderThickness="1" BorderBrush="{DynamicResource BorderDefaultBrush}">
    <StackPanel Spacing="10">
      <TextBlock Text="{Binding L_AppsFullTunnelOverlayTitle}"
                 FontWeight="SemiBold" FontSize="13"/>
      <TextBlock Text="{Binding L_AppsFullTunnelOverlayBody}"
                 TextWrapping="Wrap" FontSize="11"
                 Foreground="{DynamicResource TextMutedBrush}"/>
      <Button Content="{Binding L_AppsFullTunnelOverlayAction}"
              Command="{Binding SwitchToSplitTunnelCommand}"
              HorizontalAlignment="Stretch"
              Background="{DynamicResource AccentSolidBrush}"
              Foreground="{DynamicResource AccentOnSolidBrush}"/>
    </StackPanel>
  </Border>
</Grid>
```

**B. Inline banner поверх content** (не оверлей-модалка а полоска в
   шапке таба):

```
┌─ Applications ───────────────────────┐
│ ⓘ Full-tunnel mode is active —       │
│   app selection is ignored. [Switch] │
├──────────────────────────────────────┤
│ [список приложений как обычно,       │
│  visible но disabled с opacity 0.5]  │
└──────────────────────────────────────┘
```

**Recommendation: B** — банер не ломает muscle-memory юзера, делает
причину explicit, дает кнопку немедленного fix.

**New strings**:
- `AppsFullTunnelBanner` — «Full-tunnel mode is active. App selection
  is ignored — all traffic goes through VPN.» / «Активен Full-tunnel.
  Выбор приложений игнорируется — весь трафик идёт через VPN.»
- `AppsFullTunnelBannerAction` — «Switch to split tunnel» / «Переключить
  на Split tunnel»

**New command** в MainWindowViewModel:
```csharp
[RelayCommand]
private void SwitchToSplitTunnel() {
    IsSplitTunnel = true;
    SaveSettings();
}
```

**Acceptance**: full tunnel → Apps page показывает banner на верху +
список dim 50% opacity. Click [Switch] → быстро переключает в split
tunnel + список становится full opacity + interactive.

---

### 4. Custom direct rules

**Симптом**: пользователь хочет добавлять свои direct-правила
(например, CIDR / domain) напрямую в config. Use case: WireGuard +
другой VPN — хочет чтобы определённые сети не шли через VPNRouter.

**Текущее состояние**: `ConfigGenerator.cs` берёт `ProfileManager`
processes + auto-generates route rules. Юзер нигде не редактирует
direct-правила вручную (кроме full-custom JSON mode).

**Дизайн**:

#### Schema — новое поле в `AppSettings.App`

```csharp
// Models/AppSettings.cs
public class AppConfig {
    // ... existing fields ...

    /// <summary>v2.29: user-defined direct-routing rules. Each rule
    /// matches a destination (domain / IP / CIDR / port) and routes
    /// it OUT of the VPN tunnel (action: direct). Useful for cases
    /// like running WireGuard alongside split-tunnel VPNRouter, or
    /// excluding LAN ranges from the tunnel.</summary>
    public List<CustomDirectRule> CustomDirectRules { get; set; } = new();
}

public class CustomDirectRule {
    /// <summary>Match type: "domain", "domain_suffix", "domain_keyword",
    /// "ip_cidr", "process_name", "port".</summary>
    public string Type { get; set; } = "domain";

    /// <summary>Match value(s). Comma-separated for multi-value (e.g.
    /// "192.168.0.0/16, 10.0.0.0/8" for ip_cidr).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional human label.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>True = rule is active. Allows toggling without delete.</summary>
    public bool Enabled { get; set; } = true;
}
```

#### ConfigGenerator integration

В `BuildRouteRules`, **до** auto-generated process_name rules, inject
custom direct rules. Each yields a sing-box rule like:

```jsonc
{
    "domain_suffix": ["lan.local", "internal.corp"],
    "action": "direct"
}
```

или для ip_cidr:

```jsonc
{
    "ip_cidr": ["192.168.0.0/16", "10.0.0.0/8"],
    "action": "direct"
}
```

#### UI — Network → Routing → "Advanced" expander

На NetworkPage в Routing section добавить collapsed expander:

```
▾ Custom direct rules ┌─ (advanced)
  Add rules to bypass VPN for specific domains, IPs, or networks.
  Useful when you have another VPN (e.g. WireGuard) running.

  ┌──────────────────────────────────────────────┐
  │ Type       Value                  Enabled    │
  │ ───────────────────────────────────────────  │
  │ ip_cidr    10.0.0.0/8             [✓]  [✕]  │
  │ ip_cidr    192.168.0.0/16         [✓]  [✕]  │
  │ domain     internal.corp          [✓]  [✕]  │
  └──────────────────────────────────────────────┘

  [+ Add rule] [Import from text...]
```

«Import from text» pop-out для bulk paste — формат
`type:value` per line. Удобно когда юзер копирует список CIDR'ов из
RFC1918 / своего WireGuard config.

#### Validation

- Перед apply: validate каждое rule:
  - `ip_cidr`: parse через `IPNetwork.Parse` или регex.
  - `domain` / `domain_suffix`: regex check.
  - `port`: 1-65535.
- На invalid: красная iconка inline в строке + tooltip с reason.
- Save заблокирован если есть invalid rules.

#### Persistence

YAML config (`config.yaml` под `app:`):

```yaml
app:
  routing_mode: split
  custom_direct_rules:
    - type: ip_cidr
      value: "10.0.0.0/8, 192.168.0.0/16"
      comment: "WireGuard tunnel"
      enabled: true
    - type: domain_suffix
      value: "internal.corp"
      enabled: true
```

#### Tests

- `CustomDirectRulesGeneratorTests` (5 cases):
  1. Empty list → no extra rules in generated config.
  2. Single ip_cidr rule → matching sing-box rule produced.
  3. Multiple value (CSV) → array in JSON.
  4. Disabled rule → skipped.
  5. Invalid value → ConfigGenerator throws or logs warning.

#### Migration

`SettingsMigrator.cs` — backward compat: missing field defaults to
empty list. No schema break.

**Acceptance**:
- Network → Routing → expand "Custom direct rules" → empty list.
- Click [+ Add rule] → row appears with editable Type / Value.
- Type "ip_cidr" + value "192.168.0.0/16" → save.
- Connect VPN → traffic to 192.168.0.0/16 goes direct, everything
  else через VPN.
- Disable the rule → traffic to that range goes through VPN again.
- Verify в `current.json` (sing-box generated config) что route.rules
  содержит `{"ip_cidr": ["192.168.0.0/16"], "action": "direct"}`
  раньше других rules.

---

### 5. Free Configs — больше параллелизма

**User comment** (Mac): «вот это можно делать ассинхронно — не
последовательно отправлять по 1 запросу а хуярить сразу пачку запросов».

**Текущее состояние** (после v2.28.5-r2 batched flow):

```
ВНУТРИ батча:
  TCP+TLS test  — параллельно, semaphore = 80, timeout 1.5s
  Deep verify   — параллельно, semaphore = 5  (sing-box spawn cost)

МЕЖДУ батчами:
  Полностью последовательно — batch 1 done → batch 2 starts.
  Pool fetch (14 источников) — НЕ проверял; возможно тоже sequential.
```

**Где можно поднять параллелизм**:

#### 5a. Pool fetch parallelism

`FreeConfigPoolFetcher.cs` / `FreeConfigAggregator.FetchPoolAsync()`:
скачивает 14 источников. Скорее всего sequentially (`foreach
source... await client.GetAsync...`).

**Fix**: параллельно через `Task.WhenAll`:
```csharp
var tasks = sources.Select(s => FetchOneAsync(s, ct));
var results = await Task.WhenAll(tasks);
var pool = results.SelectMany(r => r).ToList();
```

**Подводные**:
- HTTP rate limits на отдельных хостах — 14 источников из 14 разных
  хостов, каждый получает 1 запрос, не проблема.
- DNS resolution storm — все 14 hosts resolve одновременно. Не
  критично; OS DNS resolver справится.
- Memory: 14 × ~500KB pool fragment = ~7MB peak vs ~500KB sequential.
  Незначительно.
- Cancellation: с `Task.WhenAll` нужен careful exception handling
  (одна failure не должна валить остальные).

**Estimate**: 1-2 часа. Easy win.

#### 5b. Cross-batch overlap

Сейчас:
```
batch 1 → TCP+TLS (10s) → deep verify (40s)
batch 2 →                                          TCP+TLS (10s) → ...
```

Можно:
```
batch 1 → TCP+TLS (10s) → deep verify (40s)
batch 2 →                  TCP+TLS (10s) → deep verify (...)
batch 3 →                                  TCP+TLS (10s) → ...
```

Pipeline: пока batch N в deep verify, batch N+1 уже делает TCP+TLS.

**Подводные**:
- **Memory peak**: вместо 1 batch (~500 entries в памяти) держим 2-3
  batch одновременно (~1500). При pool 25k это всё ещё <1% от total
  pool memory peak — приемлемо.
- **Port exhaustion**: TCP+TLS на 80 параллельных + 5 параллельных
  sing-box deep verify = ~85+ active sockets. На 16k ephemeral ports
  с 2-min TIME_WAIT это safe headroom. Двойной overlap (batch N+1
  тоже 80 parallel) = ~165 sockets — всё ещё ОК.
- **CPU**: TLS handshake CPU-heavy. На weak machines (Atom CPU,
  старый ноут) — заметная нагрузка. Mitigation: гате через
  `Environment.ProcessorCount`, если <4 cores → не overlap'им.
- **Sing-box memory**: 5 deep-verify × ~50MB = 250MB. Overlap'нем
  2 batch'а — всё ещё 5 deep verify (одна semaphore на всю pipe).
  Не растёт.
- **Status text complexity**: текущий status «Batch 4/57 · TCP+TLS
  · tested 145/500» становится двусмысленным когда 2 батча идут.
  Нужно либо упростить (общий counter found/target), либо показать
  по-разному.

**Estimate**: 4-6 часов. Сложнее чем 5a, но biggest UX win — пока
deep-verify готовит результаты, следующий batch уже warmup'ит TCP.

#### 5c. Deep-verify semaphore раскачать с 5 до 8-10

Сейчас sing-box spawn cap = 5 потому что:
- Каждый instance бьет 2 порта (SOCKS + Clash API)
- Memory ~50MB
- Spawn time ~500ms

Можно поднять до 8-10 на современных machines. Но риск — на VM /
старых ноутах CPU spike + memory pressure.

**Mitigation**: adaptive cap based on `Environment.ProcessorCount`:
- 1-3 cores: cap 3
- 4-7 cores: cap 5 (текущее)
- 8+ cores: cap 8

**Подводные**:
- **Port exhaustion** растёт линейно с cap. 10 sing-box × 2 ports +
  outgoing → ещё ~30 sockets per instance в peak = 300+ ephemeral.
  Близко к лимиту на heavy load.
- **Memory peak**: 10 × 50MB = 500MB. На 8GB машине ОК, на 4GB — больно.
- **Sing-box spawn race**: parallel spawn 10 процессов одновременно
  редко вызывает port-collision; semaphore + free-port lookup
  справляются.

**Estimate**: 2-3 часа. Включить adaptive cap.

#### 5d. HTTP probe parallelism (already done)

`FreeConfigDeepVerifier.ProbeViaSocksAsync` — уже параллельный per
deep-verify task (5 одновременно). Не sequential.

**Estimate**: 0 (уже сделано).

#### 5e. Async DNS pre-resolve

Идея: до TCP-test pre-resolve все hostname'ы pool через
`Dns.GetHostAddressesAsync` параллельно. Кэшировать. Тогда TCP probe
не делает DNS lookup внутри connect timeout.

**Подводные**:
- DNS rate limits на upstream resolvers — 25k hostnames через один
  resolver может trigger ban. Mitigation: throttle to 100-200/sec.
- Memory: 25k × ~80 bytes = 2MB host→IP map. ОК.
- DNS cache TTL — наши IP могут устареть к моменту когда дойдёт TCP
  probe. Mitigation: re-lookup if TCP fails with HostNotFound.

**Estimate**: 4-6 часов. Marginal win — TCP probe всё равно делает
internal DNS lookup; pre-cache ускоряет только cold path.

### Recommended sequence для item 5

| Sub-item | Effort | UX delta | Risk |
|---|---|---|---|
| 5a — pool fetch parallel | 1-2h | Faster initial load (1-3s saved) | Low |
| 5b — cross-batch overlap | 4-6h | Visibly faster mid-search progress | Medium (status text rework) |
| 5c — deep-verify cap 5→8 (adaptive) | 2-3h | 30-40% faster verify on 8+ core machines | Low-Medium |
| 5d — HTTP probe parallelism | 0 | already done | n/a |
| 5e — async DNS pre-resolve | 4-6h | Marginal | Medium-High |

**Recommend ship 5a + 5c + 5b** in v2.29 cycle. Skip 5d (done) and 5e
(low ROI, high complexity).

Total: ~7-11 часов. Можно засунуть в один -rN или разбить как 5a+5c
в r5 / 5b в r6.

## Priority + sequencing

| Item | Priority | Estimate | Dep |
|---|---|---|---|
| 1. Hardcoded "Windows" → dynamic OS | P0 | 1h | none |
| 2a. Mac autostart (LaunchAgent) | P1 | 4-6h | needs Mac test host |
| 2b. Linux autostart (XDG) | P1 | 2-3h | needs Linux test host |
| 3. Apps page full-tunnel banner | P0 | 2-3h | none |
| 4. Custom direct rules | P2 | 8-12h | tests + UI |
| 5a. Pool fetch parallel | P1 | 1-2h | none (easy win) |
| 5b. Cross-batch overlap | P1 | 4-6h | status-text rework |
| 5c. Deep-verify adaptive cap | P2 | 2-3h | none |

**P0 (быстрые fixes для UX-боли)** ship together as **v2.29.0-r1**
(items 1 + 3) — это ~3-4 часа работы, immediate user-visible improvements.

**P1 (autostart)** ship as **v2.29.0-r2** или -r3 — 6-9 часов.
Тестирование требует доступ к Mac (slovn@192.168.0.246) и какой-то
Linux машины. Linux можно протестить через VirtualBox VM нашего
build pipeline (Ubuntu для AppImage CI).

**P2 (custom direct rules)** — самая большая работа. Может пойти как
**v2.29.0-r4** или отдельный **v2.29.1** patch. UI + schema + tests +
docs ~8-12 часов.

## Implementation order для v2.29.0

```
v2.29.0-r1: items 1 + 3 + 5a       (P0 + easy wins, ~5h)
v2.29.0-r2: item 2a (Mac autostart)         ~5h
v2.29.0-r3: item 2b (Linux autostart)       ~2h
v2.29.0-r4: item 5b + 5c (perf parallelism) ~7h
v2.29.0-r5: item 4 (custom direct rules)    ~10h
v2.29.0:    cut stable когда user confirms всё работает
```

Или сжать сильнее: **r1 = 1+3+5a**, **r2 = 2+5b+5c**, **r3 = 4**.
Зависит от темпа feedback цикла с user'ом.

## Связь с другими планами

- Android v3.0 Phase 1+ continues parallel — `plans/vpnrouter-android-research.md`.
- `plans/vpnrouter-mac-startup-smoketest.md` — backlog item для Mac
  CI который бы поймал v2.28.6-r1..r5 sudoers баг. Mac autostart
  fix может зацепить smoketest заодно.
- Тесты ConfigGenerator уже есть в `VPNRouter.Tests/UnitTest1.cs`
  — добавим `CustomDirectRulesGeneratorTests` рядом.

## Acceptance — после v2.29.0 cut

- [ ] Mac юзер открывает Simple page → autostart карточка пишет «macOS»
  не «Windows».
- [ ] Mac юзер: Network → Autostart → **рабочая** настройка через
  LaunchAgent.
- [ ] Linux юзер: Network → Autostart → **рабочая** настройка через
  XDG autostart.
- [ ] Apps page при full-tunnel: banner с объяснением + кнопка переключения,
  список не disabled visually.
- [ ] Routing settings → "Custom direct rules" expander → можно добавлять
  ip_cidr / domain / port → попадают в generated sing-box config с
  action:direct → работает в реальном VPN сценарии (verified юзером
  с WireGuard кейсом).
- [ ] Free Configs первый load (pool fetch) ощутимо быстрее (5a).
- [ ] Free Configs deep-verify фаза на 8+core machines идёт ~30-40%
  быстрее чем v2.28.7 (5c).
- [ ] Status text «Search → Found N/target» обновляется визибл-заметно
  чаще когда несколько batches идут одновременно (5b).
- [ ] Все пункты confirmed user → cut stable.
