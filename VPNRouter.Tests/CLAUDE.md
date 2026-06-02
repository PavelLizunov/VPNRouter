# VPNRouter.Tests

xUnit test project. Покрывает Core (regression / unit) **и** App (headless
Avalonia GUI). Harness активен, см. секцию ниже.

## Layout

Один класс — один файл (после Phase 2E extraction, 2026-05-17). `UnitTest1.cs`
удалён, 42 класса развалены по `<ClassName>.cs`. Auto-discovered через
`Get-ChildItem VPNRouter.Tests\*.cs`. Постоянные helpers / infrastructure:

```
VPNRouter.Tests.csproj           ← references Core + App, Avalonia.Headless + Avalonia.Headless.XUnit
TestAppBuilder.cs                ← assembly-level Avalonia AppBuilder для [AvaloniaFact]
HeadlessGuiTests.cs              ← MainWindow / AboutWindow smoke + button click routing
PageScreenshotTests.cs           ← per-page PNG snapshots в screenshots/
ScreenshotHelper.cs              ← Capture + CapturePage помощники
VisualDiffHelper.cs              ← v2.31.5: SkiaSharp pixel-tolerance compare для baseline-diff
VisualDiffTests.cs               ← v2.31.5: regression-diff DpiBypass / Telegram / Tools против screenshots/baseline/
ViewModelTests.cs                ← MainWindowViewModel wiring (e.g. SmpAutostartChecked re-notify)
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

> После Phase 2E extraction (2026-05-17): один класс — один `.cs` файл.
> 42 класса из бывшего `UnitTest1.cs` развалены по `<ClassName>.cs`. Любой
> новый regression-тест ДОЛЖЕН идти в свой файл (не складировать в
> shared bag). Auto-discovery: `Get-ChildItem VPNRouter.Tests\*Tests.cs`.

### Бывший `UnitTest1.cs` (Phase 2E)

| Класс / файл | Что покрывает |
|---|---|
| `GetEffectiveServersTests` | `VlessConfig.GetEffectiveServers` — backward-compat для legacy single-server fields |
| `ConfigGeneratorTests` | DNS strategy, route rules, outbound generation для split/full tunnel + 2 sing-box check integration tests |
| `LeakProtectionAppSettingsTests` | F-12 / parity audit P0: `ValidateAppSettings` defence-in-depth backstop for silent ConfigMode flips |
| `LeakProtectionTests` | `ValidateConfig` invariants + protocol-aware dispatch (VLESS/Hy2/TUIC, v2.30.1-r4) + smart-mode local-dns + proxy-udp branch |
| `VlessUriParserTests` | `VlessUriParser.Parse` URI shapes + invalid-input rejection |
| `CustomConfigInjectorTests` | Custom-config inject pipeline: split-tunnel routing inject, action-vs-legacy dispatch, sing-box check integration. v2.40 additions: fail-CLOSED `dns.final` + `EnsureSynthesizedRemoteDns` (synth Cloudflare DoH via proxy when no proxy-detour DNS), `InjectDnsRules(proxyTag)` per-app DNS synth for include-split, `FindRemoteDnsTag` excludes `dns-direct` (32 tests) |
| `VlessServersResolverTests` | v2.28.2: subscription→VLESS aggregation в 8 case'ах |
| `ConfigGeneratorEmptyServersGuardTests` | v2.28.2: hard guard + e2e subscribe→resolve→generate + sing-box check integration |
| `FreeConfigAggregatorPreserveTests` | v2.28.3-r5: cache merge logic (Verified preserved, recent Ok preserved, etc.) |
| `FreeConfigKeepPolicyTests` | v2.28.5: trim policy on search end (drops dead/unverified entries) |
| `FreeConfigSavedRetentionTests` | v2.28.6 Phase 1: Saved-list 30d retention cap + LastVerifyFailedAt schema |
| `FreeConfigEntrySchemaTests` | v2.28.6 Phase 1: FreeConfigEntry schema additions |
| `FreeConfigFreshnessTierTests` | v2.28.6 Phase 5: freshness math (tier classification, opacity, sort key) |
| `FreeConfigRecheckMergeTests` | Recheck merge: success keeps fresh values; failure restores prior good values |
| `AutostartHelperShapeTests` | v2.29.0-r2: cross-platform AutostartHelper shape + idempotency |
| `CustomDirectRulesGeneratorTests` | v2.29.0-r4: `ConfigGenerator.BuildCustomDirectRouteRule` + `ApplyCustomDirectRules` insertion order |
| `CustomDirectRulesParserTests` | v2.29.0-r4: text-format parser/serializer for "Custom direct rules" textbox |
| `FreeConfigDeepVerifyCheckpointTests` | v2.29.0-r7+ Phase 3C: `LastDeepVerifyAt` field + 6h skip window |
| `CustomRulesV2_30_ParserTests` | v2.30.0: full custom rules engine — parser branch |
| `CustomRulesV2_30_GeneratorTests` | v2.30.0: full custom rules engine — ConfigGenerator branch |
| `CustomRulesV2_30_MigrationTests` | v2.30.0: migration from v2.29.0-r4 CustomDirectRule schema |
| `CustomRulesImportExportTests` | v2.30.0-r3: 3-format import/export (CSV / VPNRouter JSON / sing-box-native) |
| `ServerUriParserTests` | v2.30.1-r3: multi-protocol URI parsing (Hy2 / TUIC / SS-2022 / ShadowTLS) |
| `LeakProtectionMultiProtocolTests` | v2.30.1-r4: per-protocol outbound validation dispatch |
| `ProfileManagerJsonDosGuardTests` | v2.31.0-r1 (CO-4): `MaxDepth=32` JSON guard |
| `HealthMonitorTimerRaceTests` | v2.31.0-r1 (CO-1): atomic timer-swap conservation |
| `HealthMonitorRecoveryGapTests` | v2.31.5-r2: post-crash recovery via `_shouldBeRunning` intent flag (User-reported VPN-loss bug) |
| `FirewallManagerLocalizedNetshTests` | v2.31.0-r1 (CO-5): block-aware netsh parser |
| `RuntimeStatusDetectorHandleLeakTests` | v2.31.1-r1 (AU-9): `Process[]` dispose pattern callable-stability |
| `TcpPingOnlyPlausibilityGateTests` | v2.31.2-r1 (F-25 prevent-new): preserve LatencyMs on probe failure |
| `FreeConfigCacheMigrationTests` | v2.31.3-r1 (F-25 heal-old): sub-5ms LatencyMs reset to 0 |
| `AvailableRuleTypesSurfaceTests` | v2.31.0-r4 (AU-10): domain_regex + process_path в Cards-mode ComboBox |
| `FreeConfigItemViewModelDisplayTests` | v2.31.3-r1: Verified+0 → "— ✓✓" (graceful unknown state) |
| `BoolToChevronConverterTests` | v2.31.0-r4 (F-3): default vs param glyph paths |
| `SubscriptionFetcherParserTests` | v2.31.5+: 3 subscription body formats (JSON wrapper / raw base64 / plain URIs) + dedup + unsupported-scheme filter |
| `MergeUserCustomizationTests` | v2.31.6-r10 Phase F: extracted `MergeUserCustomization` helper (CustomGroupApps, CustomCategories, .exe normalisation) |
| `PowerEventListenerTests` | v2.31.6-r10 Phase D: Windows session/power event listener (idempotent Start, safe Dispose) |
| `EmergencyChannelConfigTests` | r9 Phase 2 (wgturn-core integration): config defaults / null-safety |
| `EmergencyChannelManagerTests` | EmergencyChannelManager lifecycle state transitions |
| `EmergencyChannelEngineTests` | EmergencyChannelEngine lifecycle state transitions |
| `AppSettingsEmergencyChannelTests` | AppSettings — EmergencyChannel section defaults / null-safety |
| `PlaceholderGuardTests` | v2.32.3 (Z:\kanareik incident follow-up): kill placeholder credentials for every user — PlaceholderGuard paths |

### Sibling regression suites (existing, not touched by 2E)

Auto-discovery: `Get-ChildItem VPNRouter.Tests\*Tests.cs`. Other files
(`*Helper.cs`, `TestAppBuilder.cs`, `ViewModelTests.cs`,
`HeadlessGuiTests.cs`, `PageScreenshotTests.cs`, `VisualDiffTests.cs`)
follow the same convention.

### v2.40.0 additions (not from 2E)

| Класс / файл | Что покрывает |
|---|---|
| `ProcessQueryTests` | v2.40.0-r3 (audit P0 handle-leak sweep): `ProcessQuery.AnyAlive`/`CountAlive` handle-safe wrappers — input guards, positive/negative cases, params overload, 500-call callable-stability soak. Mirrors `RuntimeStatusDetectorHandleLeakTests` (9 tests, 7 `[Fact]` + 2 `[Theory]`) |
| `RoutingAppListEditorTests` | v2.40.0-r2 additions: `RoutingAppListEditor.IsStillRoutedByAnother` survivor-guard so `ScrubRoutingForApp` won't over-remove a process name another group still routes (21 tests) |
| `DiagnosticsRedactorTests` | v2.39.0-r1+ diagnostics export redaction; v2.40.0-r1 additions: `obfs_password`/`plugin_opts`, URL userinfo drop, Authorization/Bearer-token redaction (15 tests) |
| `FreeConfigsApplyGateTests` | v2.40.0 FC interaction gates: Verified-only Connect/Apply, IsBusy guard, target/maxPing clamps (3 tests) |

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

**Следствие — drift невидим в CI.** Раз CI крутится на Linux, pixel-diff
слой там НИКОГДА не выполняется. Baseline drift проскользнул v2.37 → v2.38
(redesign DpiBypass/Telegram/Tools) и был пойман только ручным прогоном
2026-06-02. Поэтому VisualDiffTests завязан как **pre-ship gate** на dev-VM:
`ship-rolling-candidate` skill, Pre-flight step 5 — единственное место где
diff реально гоняется. Если меняешь эти 3 страницы интенционально —
refresh baseline в том же ship-commit (workflow выше).

## Roadmap

- Расширить `VisualDiffTests` на Subscribe page, когда найдём способ
  деттерминированно cleanup'ить cached subscriptions перед capture.
