# Phase 6 — Wave 31b: Small JsonSerializer cleanups + JsonArray.Add<T> retirement

**Authored**: 2026-05-19
**Phase**: 6 of v3.0 refactor (`plans/v3.0-refactor-roadmap.md`)
**Scope**: Two distinct items the Wave 30 audit (commit `3000e2e`,
`plans/phase6-nativeaot-readiness-2026-05-18.md`) flagged as ready for
mechanical AOT-cleanup:
1. 5 small `JsonSerializer` call-site sites whose `JsonSerializerOptions`
   instance lacks a `TypeInfoResolver` — wire each through
   `AppJsonContext` so the generated `JsonTypeInfo` is reachable.
2. `JsonArray.Add<T>(T)` generic-overload mass-replacement (52 warnings
   across 3 files) — switch to the non-generic
   `Add(JsonNode?)` overload (via `JsonValue.Create()` for primitives).

**Predecessors**: Wave 25 (`AppJsonContext`), Wave 27 (SettingsLoader
internal), Wave 28 (`AndroidJsonContext`), Wave 30 (NativeAOT audit).

**Sibling**: Wave 31a (separate worktree) handles the YamlDotNet
`StaticDeserializerBuilder` swap in `SettingsLoader` — DO NOT TOUCH
`SettingsLoader.cs` or `VPNRouter.Core.csproj` package refs here.

---

## Why

The Wave 30 audit ran `dotnet publish VPNRouter.CLI -p:PublishAot=true`
and observed 120 IL2026/IL3050 warnings across the dependency tree.
Half of them are concentrated in:

| Pattern | Count | Files |
|---|---:|---|
| `JsonSerializerOptions` without `TypeInfoResolver` | 8 warnings (2 sites × 4 patterns) | WindowsDnsHardening, LaunchFailureCounter, ClashSingBoxApi anon types, CustomRulesImportExport export duplicate, CLI StateFile |
| `JsonArray.Add<T>(T)` collection initialisers | 52 warnings (24+16+12) | CustomConfigInjector, VlessDeepVerifier, FreeConfigDeepVerifier |

Both patterns are mechanical fixes: no behaviour change, no public-API
churn, no semantic ambiguity. Together they retire ~60 of the 120 IL
warnings the Wave 30 publish attempt surfaced — a measurable step
toward an AOT-clean compile.

The remaining 60 warnings are:
- ~32 in YamlDotNet (Wave 31a)
- ~28 across smaller call sites that the audit deferred to Wave 31+
  (anonymous `new { ... }` shapes inside one-off paths, IHttpClient
  generic helper, etc.) — handled in follow-on waves.

## What

### Part 1: 5 `JsonSerializerOptions` `TypeInfoResolver` wirings

| File | Line(s) | What changes |
|---|---:|---|
| `VPNRouter.Core/Services/WindowsDnsHardening.cs` | 265-269, 298-309 | `private class HardeningState` + `private class SavedRegValue` → `internal sealed class`. Register both in `AppJsonContext`. Wire `HardeningStateOptions.TypeInfoResolver = Combine(AppJsonContext.Default, DefaultJsonTypeInfoResolver())`. |
| `VPNRouter.Core/Services/LaunchFailureCounter.cs` | 234-239, 64-73 | `State` is already `public sealed class` — just register in `AppJsonContext` + wire `JsonOptions.TypeInfoResolver`. |
| `VPNRouter.Core/Services/ClashSingBoxApi.cs` | 132, 276 | Hoist `new { path = configPath }` and `new { name }` anonymous types to named records `ClashSetConfigDto(string Path)` + `ClashSelectProxyDto(string Name)` with `[JsonPropertyName]` matching pre-Phase-6 wire format (lowercase `path` + `name`). Register in `AppJsonContext`. Reuse the existing `SerializerOptions` instance (after wiring its `TypeInfoResolver`) for both Serialize + Deserialize. |
| `VPNRouter.Core/Services/CustomRulesImportExport.cs` | 474 | The export uses `new JsonSerializerOptions { WriteIndented = true }` inline AND serialises `List<object>` of `Dictionary<string, object>` — registering that recursive `object`-typed shape in `AppJsonContext` is not feasible without restructuring the DTO. Approach: keep the export path on the reflective fallback (it's user-export-triggered, not in any hot path), but factor the inline options into a private `ExportOptions` field so the duplication goes away. Document the reflective-fallback reasoning. |
| `VPNRouter.CLI/Helpers/StateFile.cs` | 55-59 | `RunState` is already `public class` — register in `AppJsonContext` + wire `StateFile.Options.TypeInfoResolver`. |

### Part 2: `JsonArray.Add<T>` mass-replacement

Confirmed from the Wave 30 publish log:

| File | Unique line positions | Warning count (×2 for IL3050+IL2026) |
|---|---:|---:|
| `VPNRouter.Core/Services/CustomConfigInjector.cs` | 12 | 24 |
| `VPNRouter.Core/Services/VlessDeepVerifier.cs` | 8 | 16 |
| `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs` | 6 | 12 |

The warnings are triggered by C# overload resolution picking
`JsonArray.Add<T>(T)` (which has `RequiresUnreferencedCode` +
`RequiresDynamicCode`) instead of `JsonArray.Add(JsonNode?)` (the
non-generic instance-method overload added in STJ 8.0).

The compile-time fix: pass a `JsonNode?` directly.

```csharp
// before
arr.Add("string");
arr.Add(new JsonObject { ... });
arr.Add(intValue);

// after — wrap primitives in JsonValue.Create() (non-generic overloads
// exist for bool/int/long/string/etc. — none of them are AOT-flagged);
// JsonObject already IS a JsonNode but the C# compiler still picks the
// generic if there's any ambiguity, so an explicit cast pins it.
arr.Add(JsonValue.Create("string"));
arr.Add((JsonNode?)new JsonObject { ... });
arr.Add(JsonValue.Create(intValue));
```

For collection initialisers (`new JsonArray { a, b }`) which the
compiler desugars to `.Add(a); .Add(b);` calls, we either:

a. Spell out the initialiser into explicit `Add` lines so the cast/wrap
   is visible.
b. Cast each element inline: `new JsonArray { (JsonNode?)x, (JsonNode?)y }`.

Option (b) is more concise; option (a) is more legible. We'll pick (b)
for short 2-3-element initialisers and (a) for the few cases where
option (b) would push the line >120 columns.

## How

**Step 1** — Write this brief, commit (no code changes yet).

**Step 2 (Part 1, Cleanup 1)** — `WindowsDnsHardening.cs`:
- Flip `private class HardeningState` + `private class SavedRegValue`
  to `internal sealed class HardeningState` / `internal sealed class SavedRegValue`.
- Register both in `AppJsonContext` (`using VPNRouter.Core.Services;`
  already present — direct add of `[JsonSerializable(typeof(HardeningState))]`
  + `[JsonSerializable(typeof(SavedRegValue))]` alphabetically).
- Wire `HardeningStateOptions.TypeInfoResolver = Combine(...)`.

**Step 3 (Part 1, Cleanup 2)** — `LaunchFailureCounter.cs`:
- `State` already public; add `[JsonSerializable(typeof(LaunchFailureCounter.State))]`
  to `AppJsonContext`.
- Wire `JsonOptions.TypeInfoResolver = Combine(...)`.

**Step 4 (Part 1, Cleanup 3)** — `ClashSingBoxApi.cs`:
- Add `internal sealed record ClashSetConfigDto([property: JsonPropertyName("path")] string Path)`.
- Add `internal sealed record ClashSelectProxyDto([property: JsonPropertyName("name")] string Name)`.
- Register both in `AppJsonContext`.
- Wire `SerializerOptions.TypeInfoResolver` (currently has only
  `PropertyNameCaseInsensitive` + `NumberHandling`).
- Replace `JsonSerializer.Serialize(new { path = configPath })` with
  `JsonSerializer.Serialize(new ClashSetConfigDto(configPath), SerializerOptions)`.
- Replace `JsonSerializer.Serialize(new { name })` with
  `JsonSerializer.Serialize(new ClashSelectProxyDto(name), SerializerOptions)`.

**Step 5 (Part 1, Cleanup 4)** — `CustomRulesImportExport.cs`:
- The export at line 474 serialises `List<object>` containing
  `Dictionary<string, object>` whose values are `int`, `string`,
  `List<int>`, `List<string>`. Registering that recursive `object`-typed
  shape in AppJsonContext is not feasible.
- Solution: factor the inline `new JsonSerializerOptions { WriteIndented = true }`
  into a private `ExportSingBoxOptions` field at top of class. No
  `TypeInfoResolver` wiring — the reflective fallback is the correct
  path here. Document in field xmldoc.
- Note: the file's existing `JsonOptions` field (SnakeCaseLower) is used
  for the VPNRouter JSON export path which serialises `List<CustomRule>`
  — that DTO IS registerable. But the brief said "5 small cleanups" and
  the existing path is already working. Defer broader CustomRule
  registration to a separate audit.

**Step 6 (Part 1, Cleanup 5)** — `StateFile.cs`:
- Register `RunState` in `AppJsonContext` (need `using VPNRouter.CLI.Commands;`
  — but Core can't depend on CLI; check if RunState is reachable).
- If RunState lives in CLI assembly: declare a sibling `CliJsonContext`
  in `VPNRouter.CLI/Helpers/CliJsonContext.cs`, register `RunState`
  there, wire `StateFile.Options.TypeInfoResolver` to chain
  `CliJsonContext.Default` + `AppJsonContext.Default` + reflective.

**Step 7 (Part 2)** — `CustomConfigInjector.cs`:
- 12 line positions (172, 518, 696, 704, 733, 768, 825 ×2, 1081, 1231,
  1237, 1268). Walk through each, apply the wrap/cast.
- For collection initialisers with 2-3 elements (lines 768, 825):
  inline cast.
- For multi-line `.Add(new JsonObject {...})` patterns (lines 696, 704,
  733, 1081): explicit `(JsonNode?)` cast on the argument.
- For string `.Add(s)` (lines 172, 518, 1231, 1237, 1268):
  `JsonValue.Create(s)`.

**Step 8 (Part 2)** — `VlessDeepVerifier.cs`:
- 8 line positions (286, 292, 303, 304, 312, 313, 596, 598). All in
  collection initialisers — apply inline cast.

**Step 9 (Part 2)** — `FreeConfigDeepVerifier.cs`:
- 6 line positions (409, 415, 426, 428, 436, 437). All in collection
  initialisers — apply inline cast.

**Step 10** — Verification gate: build, test, grep.

## Acceptance

- [ ] `dotnet build VPNRouter.sln -c Release` — 0 errors
- [ ] `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`
  — full regression bar passes (especially HardeningState read,
  LaunchFailureCounter round-trip, ClashSingBoxApi body shape,
  CustomRulesImportExport round-trip, StateFile read, and the 3
  JsonArray files' round-trip tests)
- [ ] Grep `\.Add\(` results in the 3 JsonArray files no longer trigger
  IL3050 — verified by re-running the AOT publish (warning count down
  from 120 to ~68)
- [ ] Grep `JsonSerializer\.Serialize\s*\(\s*new\s*\{` in
  `ClashSingBoxApi.cs` returns ZERO hits
- [ ] Brief Outcome section filled
- [ ] `AppJsonContext` has 4 new entries: `HardeningState`, `SavedRegValue`,
  `LaunchFailureCounter.State`, `ClashSetConfigDto`, `ClashSelectProxyDto`
  (alphabetical order preserved)
- [ ] `CliJsonContext` created with `RunState` entry

## Verification gate (per `plans/v3.0-execution-methodology.md`)

1. Build green — `dotnet build VPNRouter.sln -c Release`
2. Tests green — `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`
3. Test build green — built as part of (1)
4. Code review pass — no public-API churn (visibility flips are
   `private → internal`, which only expands reachability to the same
   assembly + InternalsVisibleTo VPNRouter.Tests)
5. Roadmap entry — Wave 31b row added to Phase 6 progress section in
   `plans/v3.0-refactor-roadmap.md`
6. Outcome filled in below

---

## Outcome

### Part 1 — 5 small JsonSerializer cleanups

| # | File | Status |
|---|---|---|
| 1 | `WindowsDnsHardening.cs` | TBD |
| 2 | `LaunchFailureCounter.cs` | TBD |
| 3 | `ClashSingBoxApi.cs` | TBD |
| 4 | `CustomRulesImportExport.cs` | TBD |
| 5 | `StateFile.cs` (CLI) | TBD |

### Part 2 — JsonArray.Add<T> mass-replacement

| File | Audit estimate | Actual lines fixed | Result |
|---|---:|---:|---|
| `CustomConfigInjector.cs` | 24 warnings | TBD | TBD |
| `VlessDeepVerifier.cs` | 16 warnings | TBD | TBD |
| `FreeConfigDeepVerifier.cs` | 12 warnings | TBD | TBD |

### Build + tests

TBD

### Commit hashes

TBD

### New types added

TBD

### Notes / surprises

TBD
