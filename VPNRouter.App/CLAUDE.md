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

## Тесты

Тесты ViewModel'ов отсутствуют (headless Avalonia harness — backlog).
Тестируется только Core layer. Manual repro для UI bugs через `tools/live-test-r1.ps1`.
