# VPNRouter.Tests

xUnit test project. **Только Core layer** — UI тесты (headless Avalonia harness)
в backlog.

## Layout

```
VPNRouter.Tests.csproj      ← references VPNRouter.Core, InternalsVisibleTo разрешает доступ к internal API
UnitTest1.cs                ← все тесты в одном файле (~1900 строк, 30+ tests)
```

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

## Backlog: UI testing

Headless Avalonia harness не настроен. Когда понадобится:
1. Добавить `Avalonia.Headless` пакет
2. Test fixture создаёт `MainWindow` без display
3. Манипулирует через VM bindings, проверяет state

См. `plans/ui-testing-workflow.md` (если будет создан).
