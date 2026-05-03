# VPNRouter.Tests

xUnit test project. Покрывает Core (regression / unit) **и** App (headless
Avalonia GUI). Harness активен, см. секцию ниже.

## Layout

```
VPNRouter.Tests.csproj           ← references Core + App, Avalonia.Headless + Avalonia.Headless.XUnit
TestAppBuilder.cs                ← assembly-level Avalonia AppBuilder для [AvaloniaFact]
UnitTest1.cs                     ← Core regression + UI-data tests без dispatcher (~4500 строк)
HeadlessGuiTests.cs              ← MainWindow / AboutWindow smoke + button click routing
PageScreenshotTests.cs           ← per-page PNG snapshots в screenshots/
ScreenshotHelper.cs              ← Capture + CapturePage помощники
VisualDiffHelper.cs              ← v2.31.5: SkiaSharp pixel-tolerance compare для baseline-diff
VisualDiffTests.cs               ← v2.31.5: regression-diff DpiBypass / Telegram / Tools против screenshots/baseline/
ViewModelTests.cs                ← MainWindowViewModel wiring (e.g. SmpAutostartChecked re-notify)
ConfigGeneratorDuplicateNameTests.cs  ← наследие отдельного класса
VpnEngineTunFingerprintTests.cs       ← наследие отдельного класса
```

## Headless harness

`TestAppBuilder` декорирован `[assembly: AvaloniaTestApplication(...)]`,
поэтому каждый `[AvaloniaFact]` / `[AvaloniaTheory]` поднимает Avalonia
на dispatcher-thread теста. `UseHeadlessDrawing=false` + `UseSkia()` дают
offscreen-render → `window.CaptureRenderedFrame()` для PNG snapshots.

Когда писать `[Fact]` (plain xUnit) vs `[AvaloniaFact]`:
- `[Fact]` — pure data / converter / VM-list тесты (не трогают dispatcher).
- `[AvaloniaFact]` — конструируешь `MainWindowViewModel`, Window, `Show()`,
  RaiseEvent, CaptureRenderedFrame, любые Avalonia-controlled пути.

## Test classes

| Класс | Что покрывает | Count |
|---|---|---|
| `GetEffectiveServersTests` | `VlessConfig.GetEffectiveServers` — backward-compat для legacy single-server fields | 4 |
| `ConfigGeneratorTests` | DNS strategy, route rules, outbound generation для split/full tunnel | 14 (+2 skipped) |
| `Inject_ActualCustomConfig_SingBoxCheck` | integration test: custom config injection passes `sing-box check` | 1 |
| `Inject_WithBypassRussianTraffic_PassesSingBoxCheck` | integration test: geo bypass injection passes | 1 |
| `VlessServersResolverTests` | v2.28.2: subscription→VLESS aggregation в 8 case'ах | 8 |
| `ConfigGeneratorEmptyServersGuardTests` | v2.28.2: hard guard + e2e subscribe→resolve→generate | 2 |
| `Generate_FromSubscribeMode_PassesSingBoxCheck` | v2.28.2: integration — generated JSON valid под sing-box check | 1 |
| `FreeConfigAggregatorPreserveTests` | v2.28.3-r5: cache merge logic (Verified preserved, recent Ok preserved, etc.) | 9 |
| `ProfileManagerJsonDosGuardTests` | v2.31.0-r1 (CO-4): `MaxDepth=32` JSON guard | 2 |
| `HealthMonitorTimerRaceTests` | v2.31.0-r1 (CO-1): atomic timer-swap conservation | 1 |
| `FirewallManagerLocalizedNetshTests` | v2.31.0-r1 (CO-5): block-aware netsh parser | 2 |
| `RuntimeStatusDetectorHandleLeakTests` | v2.31.1-r1 (AU-9): `Process[]` dispose pattern callable-stability | 2 |
| `TcpPingOnlyPlausibilityGateTests` | v2.31.2-r1 (F-25 prevent-new): preserve LatencyMs on probe failure | 1 |
| `FreeConfigCacheMigrationTests` | v2.31.3-r1 (F-25 heal-old): sub-5ms LatencyMs reset to 0 | 1 |
| `FreeConfigItemViewModelDisplayTests` | v2.31.3-r1: Verified+0 → "— ✓✓" (graceful unknown state) | 2 |
| `BoolToChevronConverterTests` | v2.31.0-r4 (F-3): default vs param glyph paths | 2 |
| `AvailableRuleTypesSurfaceTests` | v2.31.0-r4 (AU-10): domain_regex + process_path в Cards-mode ComboBox | 1 |
| `MainWindowViewModelTests` (ViewModelTests.cs) | v2.27 Bug B: SmpAutostartChecked re-notify on three inputs | 1 |
| `HeadlessGuiTests` | MainWindow/AboutWindow ctor smoke + width screenshots + button input routing | 4 |
| `PageScreenshotTests` | 9 page snapshots + NetworkPage Autostart sub-tab + 3 narrow-window variants | 14 |
| `VisualDiffTests` | v2.31.5: pixel-tolerance regression vs `screenshots/baseline/` для DpiBypass / Telegram / Tools (Windows-only) | 3 |

## Запустить

```bash
# Все тесты:
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release

# Только v2.28.x регрессионные:
dotnet test ... --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~Generate_FromSubscribeMode_PassesSingBoxCheck"

# Skip integration tests (требуют sing-box.exe):
dotnet test ... --filter "FullyQualifiedName!~SingBoxCheck"
```

## Critical patterns

### sing-box check integration
2 теста запускают `sing-box.exe check -c <generated.json>` и assert exit 0.
Skip gracefully если бинарь не установлен:
```csharp
var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
if (!File.Exists(singBoxPath)) return; // CI без бинаря — skip
```

### testhost lock
**Параллельные `dotnet test` + `dotnet build` ломают друг друга** через testhost
process locking DLL. После теста testhost держит DLL ещё 2-3 секунды. Если
сразу пушим build — MSB3027/MSB3021 errors. Решение: либо ждать,
либо `taskkill /F /IM testhost.exe`.

### Internal helpers exposed
`VPNRouter.Core.csproj` имеет `<InternalsVisibleTo Include="VPNRouter.Tests"/>`.
Это позволяет тестировать `internal static` методы напрямую без публичного API:
- `FreeConfigAggregator.PreservePreviousValidation` — internal static helper.

## Headless tests — known issues

- `dotnet test` без `--filter` иногда зависает в CI / dev VM при последовательном запуске PageScreenshotTests + HeadlessGuiTests из-за того, что dispatcher-thread shutdown не всегда чистый между классами. Workaround: запускать классы отдельно (`--filter "FullyQualifiedName~HeadlessGuiTests"`) или передавать `-p:VSTestUseMSBuildOutput=false` чтобы освободить testhost. Это инфраструктурный quirk, не bug продукта.
- `MainWindowViewModel` ctor создаёт filesystem-зависимые объекты (settings, logger). Тесты на VM-уровне могут переписывать друг другу `%ProgramData%\VPNRouter\config.yaml` если запускать параллельно. Сейчас xUnit держит их в одном AppDomain, так что коллизий нет, но если `[Collection]` появится — проверить.

## Visual-diff baseline (v2.31.5)

`VisualDiffTests` сравнивает свежий PNG-снимок страницы с pinned baseline в
`VPNRouter.Tests/screenshots/baseline/` через `VisualDiffHelper.Compare`
(SkiaSharp). Threshold: 2% пикселей могут отличаться по сумме |ΔR|+|ΔG|+|ΔB|
больше 30 единиц — это абсорбирует AA-noise но ловит реальные regressions
(removed control, theme inverted, layout shift).

### Pinned pages

`page-dpi-bypass`, `page-telegram`, `page-tools` — статичные layouts без
зависимости от cached state (subscriptions, free pool). FreeConfigs / Servers
интенционально НЕ baselined потому что их рендер варьируется от состояния
кэша между прогонами.

### Refresh workflow когда страница интенционально меняется

```bash
# 1. Регенерируем актуальные снимки
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PageScreenshotTests"

# 2. Pin'им новый baseline
copy VPNRouter.Tests\screenshots\page-foo.png ^
     VPNRouter.Tests\screenshots\baseline\page-foo.png

# 3. Verify
dotnet test ... --filter "FullyQualifiedName~VisualDiffTests"

# 4. Commit обновлённый baseline вместе с UI-change
git add VPNRouter.Tests/screenshots/baseline/page-foo.png
```

### Cross-platform note

VisualDiffTests skip silently на Mac/Linux (`OperatingSystem.IsWindows()`
guard). Headless Skia рендерит чуть иначе на разных OS из-за font hinting
+ AA strategy — поддерживать per-platform baselines triples maintenance
без proportional regression-coverage gain. PageScreenshotTests (которые
проверяют view-tree assembly + bindings) работают на всех 3 платформах
и покрывают cross-platform layer.

## Roadmap

- Расширить `VisualDiffTests` на Subscribe page, когда найдём способ
  деттерминированно cleanup'ить cached subscriptions перед capture.
