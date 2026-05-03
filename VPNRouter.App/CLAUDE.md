# VPNRouter.App

Avalonia GUI. Кросс-платформа (Windows / macOS / Linux), `net8.0` (App.csproj
без `-windows` суффикса — иначе не собирается на других платформах). Платформ-
специфичные ветки через `#if PLATFORM_WINDOWS`.

## Layout

```
App.axaml                          ← глобальные ресурсы + ThemeDictionaries (Light/Dark)
Styles/Tokens.axaml                ← дизайн-токены: цвета, отступы, радиусы. Использовать СЕМАНТИЧЕСКИЕ имена
Localization/Strings.cs            ← вся локализация. Bilingual (Ru/En). Static getters L_FieldName.
ViewModels/
  MainWindowViewModel.cs           ← основная VM (~3500 строк, partial — split across files)
  MainWindowViewModel.Localization.cs
  MainWindowViewModel.RuntimeStatus.cs
  MainWindowViewModel.ServerTesting.cs
  MainWindowViewModel.SimpleMode.cs
  FreeConfigs/
    FreeConfigsPageViewModel.cs    ← Free Configs page VM
    FreeConfigItemViewModel.cs     ← row VM
  ServerViewModel.cs / SubscriptionViewModel.cs / AppGroupViewModel.cs / etc.
Views/
  MainWindow.axaml                 ← основное окно, tab nav
  Pages/
    SimplePage.axaml               ← v2.17+ упрощённый режим
    ServersPage.axaml              ← VLESS + Custom config sub-tabs
    SubscribePage.axaml            ← подписки
    NetworkPage.axaml              ← settings (routing/leak/content/updates/autostart)
    ApplicationsPage.axaml         ← список приложений
    ToolsPage.axaml                ← Zapret + Telegram proxy
    DpiBypassPage.axaml            ← Zapret detail
    TelegramPage.axaml             ← TgProxy detail
    FreeConfigsPage.axaml          ← Free Configs (master-detail 6 sections)
```

## Дизайн-система

`Styles/Tokens.axaml` — semantic tokens. **Никогда не hardcode hex** —
использовать `{DynamicResource ...}`:

| Категория | Tokens |
|---|---|
| Surfaces | `SurfaceAppBrush`, `SurfaceSunkenBrush`, `SurfaceBaseBrush`, `SurfaceRaisedBrush` |
| Text | `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`, `TextAccentBrush` |
| Borders | `BorderSubtleBrush`, `BorderDefaultBrush`, `BorderStrongBrush`, `BorderAccentBrush` |
| Accent | `AccentBgSubtleBrush`, `AccentSolidBrush`, `AccentFgBrush`, `AccentOnSolidBrush` |
| States | `Success*`, `Warning*`, `Danger*`, `Info*` (each: `Bg`/`Border`/`Fg`/`Solid`) |
| Радиусы | `RadiusXs` (3), `RadiusSm` (6), `RadiusMd` (8), `RadiusLg` (10), `RadiusPill` |

**Reference:** `plans/vpnrouter-v2.16-arctic-theme.md` + Claude Design handoff
в `C:/tmp/vpnrouter-design/` (read-only, не комитим).

## Common patterns

### MVVM Toolkit
```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(IsConnected))]
private bool _isRunning;

[RelayCommand]
private async Task ConnectAsync() { ... }
```

### Partial classes
`MainWindowViewModel` разбит на 5 partial файлов чтоб не плодить 5000-строчный
god-object. Каждый partial — одна тематика (Localization / RuntimeStatus / etc).

### Bilingual UI
`Localization/Strings.cs` — `public static string FcRefresh => Ru ? "Обновить" : "Refresh";`
В XAML: `<TextBlock Text="{Binding L_FcRefresh}"/>` через
`MainWindowViewModel.Localization.cs` getter.

## Critical gotchas

### CheckBox.Content overflow на узких окнах
**Не использовать `Content="{Binding XLabel}"`** — bare-string Content
рендерится как no-wrap TextBlock и тянет ширину родителя за длинной локализо-
ванной подписью. Pattern:
```xaml
<CheckBox IsChecked="{Binding X}" MinHeight="0" Padding="4,0">
  <TextBlock Text="{Binding XLabel}" TextWrapping="Wrap"/>
</CheckBox>
```
Применено в Section A autostart (v2.27.0-r2), Leak Protection / Updates /
DpiBypass / Apps (v2.28.3-r1). Если overflow остался — смотреть родительский
StackPanel/Border (`HorizontalAlignment="Stretch"`, `MaxWidth`, или radio-card
pattern с `ColumnDefinitions="24,*"`).

### NumericUpDown bind to int
`NumericUpDown.Value` это `decimal?` — пушит null когда юзер очищает поле.
Bind на `int` падает с `InvalidCastException`. Решение: bind на `int?` +
`?? fallback` в usage. Pattern (v2.28.3-r4):
```csharp
[ObservableProperty] private int? _latencyGoalMaxPingMs = 300;
// usage:
var ping = LatencyGoalMaxPingMs ?? 300;
```

### SaveSettings → Reload → ConfigMode race
`ApplyPendingChangesInternalAsync` (line ~1367) делает:
```csharp
SaveSettings();
_settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);
```
Reload очищает `Vless.Servers` (в YAML пусто в subscribe mode). Поэтому
`VpnEngine.Apply` обязан звать `VlessServersResolver.Resolve` перед
`ConfigGenerator.Generate` (см. v2.28.2 silent leak).

### ConfigMode в SaveSettings
Line 1544 (после v2.28.3-r6 повторно ревьюнут):
```csharp
_settings.App.ConfigMode = IsSubscribeMode ? "subscribe"
                          : IsVlessMode ? "generated"
                          : "custom";
```
**`IsSubscribeMode` побеждает.** Если флаг не сброшен — даже при `IsVlessMode=true`
персистится `subscribe`. См. `ApplyFreeConfigAsync` v2.28.3-r4 fix:
обязательно `IsSubscribeMode = false` перед SaveSettings.

### Custom sub-tab → ConfigMode='custom' footgun (v2.28.2-r2)
`OnSelectedServerModeIndexChanged` flips `IsVlessMode=false` если sub-tab
"Custom". Без custom_config — VPN на следующем старте сломается. Guard в
SaveSettings: если `wantsCustomMode && !hasCustomConfig` → fallback на
"subscribe" / "generated".

## UI design rules (avoid recurring revisions)

User feedback after r10..r13 surfaced ~5 same-class revisions. Lessons
encoded as rules below. Apply BEFORE every UI iteration to avoid the
back-and-forth.

### A. Adapt to narrow window from the start

VPNRouter's default window is 520×640 but users resize down to ~360 px
and up to ~900 px. Layouts that look fine at design-mock width WILL
break at narrow.

**Rule A1**: Any horizontal Grid with ≥3 children needs either
- A narrow sibling layout (vertical stack) gated by an `IsXxxNarrow`
  VM flag, OR
- `MinWidth` on every star/Auto column to guarantee non-zero width
  during resize, OR
- A wrap (UniformGrid / WrapPanel) with `Stretch` items.

**Rule A2**: Set the breakpoint AFTER measuring the wide layout's
fixed-width sum. If wide form needs N px (fixed cols + spacing +
button), breakpoint = N + ~80 px comfort margin. Rules page wide
Add-form: 130 + 160 + 8×4 + button~80 = 402 + 2×* → need ≥562 px →
breakpoint 620 (with content padding).

**Rule A3**: Two `IsVisible` bindings on one element doesn't work in
Avalonia. Wrap-and-divide: outer parent carries one condition, each
inner sibling carries the other.

**Rule A4**: Drive `IsXxxNarrow` from `SizeChanged` in code-behind
(Avalonia has no container queries). Also fire on `AttachedToVisualTree`
so the initial layout is correct before the user resizes.

### B. Strict design tokens — no improvisation

Every revision the user said «без отсебятины» / «строго по дизайну».

**Rule B1**: BEFORE writing XAML, fetch the design CSS for that
selector and copy: `padding`, `border-radius`, `border`, `background`,
`font-size`, `font-weight`, `gap`. Don't approximate.

**Rule B2**: Avalonia `Padding=H,V` and CSS `padding:V H` differ in
order. CSS `padding:5px 10px` = vertical 5, horizontal 10 → Avalonia
`Padding="10,5"`. Don't rely on memory; check.

**Rule B3**: Use only semantic tokens (`SurfaceBaseBrush`,
`AccentFgBrush`, `BorderSubtleBrush`, etc.). No raw hex (the only
exception so far: `#33000000` in iOS-toggle thumb shadow, mirroring
design's literal `rgba(0,0,0,.2)`).

**Rule B4**: Don't add wrappers / cards / decoration not in the design
even when the user verbally requests them — they often reverse course
later when re-reviewing. If user *insists* on a deviation, comment
the XAML explicitly: `<!-- v2.X.Y per user override; design has no
wrapper -->`. Two recent regressions (r10 wrapper Border) fit this
pattern.

### C. Component behavior parity

**Rule C1**: When replacing a stock control with a custom one (e.g.
`MenuFlyout` → custom `Flyout` with Border + Buttons), preserve the
stock's behaviors:
- Auto-close on item click → wire `Click` handler that calls
  `parentButton.Flyout?.Hide()`.
- Outside-click close → built-in to `Flyout`, no extra work.
- Escape close → built-in.

**Rule C2**: For state-flip controls (toggle, segmented selector),
wrap state-driving converters with token resource keys
(`BoolToBrushConverter` with `"ActiveResourceKey|InactiveResourceKey"`)
so theme switching stays automatic.

### D. Single-language UI

**Rule D1**: Don't hardcode English in XAML when the project supports
RU/EN. Add `Strings.X` + `L_X` even for tiny mini-labels (`ACTION`,
`(opt.)`, etc.). The user explicitly flagged a mix of RU body text +
EN labels in r13.

**Rule D2**: Reference Russian copy in the design's HTML directly
when localizing — copy verbatim from `RulesPage.html` etc.

### E. Audit before claiming completion

**Rule E1**: After implementing, re-fetch the design and walk
selector-by-selector through the relevant CSS, checking each property
against the XAML. List gaps in the response. Don't say "implemented per
design" until that walk is complete.

**Rule E2**: If user asks "ты всё сделал по дизайну?" — answer with
an explicit table listing each design element vs implemented status.
Honest gap-flagging is faster than silent partial fixes.

### F. State sync across VM-list rebuilds

**Rule F1**: Any time a VM ObservableCollection rebuild happens (e.g.
`RebuildCustomRulesList`), every secondary view derived from it must
also refresh: filtered list, grouped views (Read mode), counts
(`RulesFilterCountAll/Direct/Proxy/Block`). Wire these into the rebuild
chain so they can't desync.

**Rule F2**: If a setter like `_settings.Vless.Servers = aggregated`
mutates settings in-memory inside the Engine layer, the next call to
`SettingsLoader.Save(settings)` will persist the mutation. Either
reload-fresh-then-mutate-only-needed-field-then-save, or refactor the
mutation to a returned value (don't mutate in place). v2.30.0-r8
subscription leak fits this.

## Тесты

Headless Avalonia harness активен (с v2.31.5+) — `VPNRouter.Tests/TestAppBuilder.cs`
поднимает Avalonia на dispatcher-thread теста через `[AvaloniaFact]` /
`[AvaloniaTheory]`, `UseSkia()` + `UseHeadlessDrawing=false` дают
offscreen-render для PNG snapshots.

Покрытие App-layer'а:

- `HeadlessGuiTests` (4) — MainWindow / AboutWindow ctor smoke + button
  input routing
- `PageScreenshotTests` (14) — 9 page snapshots + NetworkPage Autostart
  sub-tab + 3 narrow-window variants (520 / 440 / 360 / 300 / 720 / 500
  / 400 px), inspectional PNG'и в `screenshots/` (gitignored)
- `VisualDiffTests` (3, v2.31.5+) — pixel-tolerance regression vs
  pinned `screenshots/baseline/*.png` для DpiBypass / Telegram / Tools.
  Threshold 2% pixels >30 RGB-sum. Windows-only.
- `AvailableRuleTypesSurfaceTests` — `MainWindowViewModel.AvailableRuleTypes`
  Cards-mode ComboBox содержит `domain_regex` + `process_path`
- `MainWindowViewModelTests` (`ViewModelTests.cs`) — `SmpAutostartChecked`
  re-notify on three inputs (v2.27 Bug B)
- `FreeConfigItemViewModelDisplayTests` (2, v2.31.3-r1) — Verified+0
  rendering как "— ✓✓" (graceful unknown state)
- `BoolToChevronConverterTests` (2, v2.31.0-r4) — chevron-glyph converter
  default + param paths

Полный inventory + visual-diff baseline refresh workflow —
`VPNRouter.Tests/CLAUDE.md`.

Manual repro для UI bugs где удобнее — `tools/live-test-r1.ps1`.
