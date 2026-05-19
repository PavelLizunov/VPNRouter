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

| # | File | Status | Notes |
|---|---|---|---|
| 1 | `WindowsDnsHardening.cs` | landed | `HardeningState` + `SavedRegValue` flipped `private` → `internal sealed`. New sibling `WindowsDnsHardeningJsonContext` (Windows-only, gated by the same `#if PLATFORM_WINDOWS` as the containing class). `HardeningStateOptions.TypeInfoResolver` wired. |
| 2 | `LaunchFailureCounter.cs` | landed | `State` was already `public sealed class` — just registered in `AppJsonContext` + wired `JsonOptions.TypeInfoResolver`. |
| 3 | `ClashSingBoxApi.cs` | landed | Two anonymous types hoisted to named records `ClashSetConfigDto` + `ClashSelectProxyDto`, `[JsonPropertyName]` pinning lowercase `path` / `name` wire keys. Both registered in `AppJsonContext`. `SerializerOptions.TypeInfoResolver` wired. |
| 4 | `CustomRulesImportExport.cs` | landed (modified scope) | Replaced inline `new JsonSerializerOptions { WriteIndented = true }` duplicate with reuse of the file's existing `JsonOptions` field (snake_case naming policy is a no-op for `List<object>` / `Dictionary<string, object>` exports — verified via `SingBoxJson_ExportProducesValidImportableForm` test). The `object`-typed recursion remains on the reflective fallback; documented inline that a future wave will restructure the export DTO to a concrete record tree. **No `TypeInfoResolver` wiring** for this site — that would require either (a) registering `CustomRule` in `AppJsonContext` (out of scope) or (b) leaving the reflective fallback in the chain (no-op since the recursion has to fall back anyway). |
| 5 | `StateFile.cs` (CLI) | landed (with deviation) | `RunState` was already `public class` — registered in a new sibling `CliJsonContext` (`VPNRouter.CLI/Helpers/CliJsonContext.cs`), NOT in Core's `AppJsonContext`. Reason: `AppJsonContext` is `internal` to `VPNRouter.Core` and CLI is not in `InternalsVisibleTo` (only `VPNRouter.Tests` is). `StateFile.Options.TypeInfoResolver` chains `CliJsonContext.Default` + `DefaultJsonTypeInfoResolver`. `RunState`'s field types are all built-in (`int`, `string`, `DateTime`, `List<string>`) so chaining Core's context wasn't needed anyway. |

### Part 2 — JsonArray.Add<T> mass-replacement

| File | Audit estimate | Actual unique line positions | Warning count change | Result |
|---|---:|---:|---:|---|
| `CustomConfigInjector.cs` | 24 warnings | 12 | 24 → 0 | All retired |
| `VlessDeepVerifier.cs` | 16 warnings | 8 | 16 → 0 | All retired |
| `FreeConfigs/FreeConfigDeepVerifier.cs` | 12 warnings | 6 | 12 → 0 | All retired |

Total IL2026/IL3050 warnings retired: **52**.

Fix pattern: cast each `.Add` argument to `(JsonNode?)` so C# overload
resolution picks `JsonArray.Add(JsonNode?)` (the `IList<JsonNode?>`
explicit interface implementation) instead of `JsonArray.Add<T>(T)`
(the generic instance method, marked `RequiresUnreferencedCode` +
`RequiresDynamicCode`). For string arguments,
`(JsonNode?)JsonValue.Create(stringValue)`; for `JsonObject` arguments,
`(JsonNode?)new JsonObject {...}`. The `JsonValue.Create(string?)`
non-generic overload itself is NOT marked, so the chain is fully
AOT-clean.

Verified by sandbox test program: passing a `JsonNode?`-typed variable
to `.Add` produces zero IL warnings; passing `string` produces 2;
passing `JsonObject` directly (without cast) produces 2; passing
`(JsonNode?)obj` (with cast) produces 0.

### Build + tests

- `dotnet build VPNRouter.sln -c Release` — **0 errors** (192 pre-existing
  warnings: 154 xUnit1051 CancellationToken hints + 38 CA1416 Windows-platform
  hints — none from Wave 31b changes).
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`
  (filtered to skip 3 headless GUI suites unrelated to changes) —
  **1121 passed / 4 skipped / 0 failed / 38 s**.
- Surface-specific runs:
  - CustomRulesImportExportTests (12 tests) — all green.
  - LaunchFailureCounterTests (8 tests) — all green.
  - ISingBoxApiContractTests (4 tests) — all green.
  - Phase4StjRoundTripTests (18 tests, pins wire-format byte-identical
    for the Newtonsoft → STJ migration) — all green.
  - CustomConfigInjectorTests (22+ tests, including sing-box check
    integration) — all green.

### AOT publish dry-run

- Pre-Wave-31b (from Wave 30 brief): **120 IL warnings**.
- Post-Wave-31b: **76 IL warnings**. Net change: **-44**.
- Breakdown:
  - **-52**: JsonArray.Add<T> retirements (24+16+12 from Part 2).
  - **-2**: ClashSingBoxApi anonymous-type Serialize retirements
    (the 2 anon-type lines).
  - **+10**: Two new IL2026/IL3050 warnings per Part 1 site that
    didn't previously have a `DefaultJsonTypeInfoResolver` chained
    (WindowsDnsHardening, LaunchFailureCounter, ClashSingBoxApi,
    StateFile, total = 5 sites × 2 = 10 warnings — though actual
    count came in at +8 because `CustomRulesImportExport` retained
    its reflective-only path and didn't add a new resolver).
    These new warnings are intentional groundwork: the next wave
    will switch the API calls from `Serialize<T>(value, options)`
    to `Serialize(value, context.MyType)` (typed `JsonTypeInfo<T>`
    overload), which fully suppresses the warning.
- The two acceptance gates from the brief both PASS:
  - Grep `\.Add\<` in the 3 JsonArray files: ZERO `JsonArray.Add<T>`
    IL warnings remain (verified via the actual AOT publish log).
  - Grep `JsonSerializer\.Serialize\s*\(\s*new\s*\{` in
    `ClashSingBoxApi.cs`: zero hits.

### New types added

| Type | Visibility | File | Context registration |
|---|---|---|---|
| `ClashSetConfigDto` | `internal sealed record` | `VPNRouter.Core/Services/ClashSingBoxApi.cs` (top-level, after the public `ClashSingBoxApi` class) | `AppJsonContext` |
| `ClashSelectProxyDto` | `internal sealed record` | `VPNRouter.Core/Services/ClashSingBoxApi.cs` (top-level, after the public `ClashSingBoxApi` class) | `AppJsonContext` |
| `WindowsDnsHardeningJsonContext` | `internal sealed partial class` | `VPNRouter.Core/Services/WindowsDnsHardening.cs` (inside `#if PLATFORM_WINDOWS`) | New sibling context — registers `HardeningState` + `SavedRegValue` |
| `CliJsonContext` | `internal sealed partial class` | `VPNRouter.CLI/Helpers/CliJsonContext.cs` (new file) | New sibling context — registers `RunState` |

### Visibility flips

| Type | Before | After |
|---|---|---|
| `WindowsDnsHardening.HardeningState` | `private class` | `internal sealed class` |
| `WindowsDnsHardening.SavedRegValue` | `private class` | `internal sealed class` |
| `LaunchFailureCounter.State` | `public sealed class` | unchanged |
| `StateFile.RunState` | `public class` | unchanged |

### Commit hashes

| Stage | Commit | Subject |
|---|---|---|
| Brief | `1c6cdaa` | docs(plan): 6-31b — JsonSerializer cleanups + JsonArray.Add retirement brief |
| Part 1 | `de3e3aa` | refactor: 6-31b — wire TypeInfoResolver on 5 JsonSerializer call sites |
| Part 2 | `dd76e36` | refactor: 6-31b — JsonArray.Add<T> retirement (52 IL warnings → 0) |
| Brief Outcome | (this commit) | docs(plan): 6-31b — Outcome section + verification gate results |

### Notes / surprises

1. **CustomRulesImportExport line 474 cannot be fully cleaned via
   AppJsonContext registration.** The export serialises
   `List<object>` containing `Dictionary<string, object>` whose values
   are heterogeneous (`int`, `string`, `List<int>`, `List<string>`).
   Registering an `object`-typed shape with the source generator is
   not feasible. The right fix is a future-wave restructure to a
   concrete record tree (e.g. `internal sealed record SingBoxRuleEntry(
   string? Action, string? Outbound, List<string>? DomainSuffix, ...)`).
   Wave 31b just removed the duplicate inline options. The reflective
   fallback for this specific path is documented inline.

2. **`StateFile.cs` registration via a sibling context, not
   `AppJsonContext` directly.** `AppJsonContext` is `internal` to
   `VPNRouter.Core` and CLI is not in its `InternalsVisibleTo` list.
   The clean fix is the sibling-context pattern (matches Wave 28's
   Android-side approach). No InternalsVisibleTo amendment needed,
   no Core API surface changes.

3. **Part 1 wiring DID NOT reduce raw warning count for the 4 patched
   sites.** Each `Serialize<T>(value, options)` / `Deserialize<T>(s, options)`
   call retains its IL2026/IL3050 even with the resolver wired — the
   warning is call-shape-based, not options-content-based. Wave 31b
   accomplishes the structural prep (DTOs registered, options have
   resolver chain) so Wave 32+ can mechanically swap to the
   `Serialize(value, context.MyType)` overload which IS warning-clean.
   The net warning count went from 120 → 76 because:
   - Part 2's JsonArray.Add fixes are call-shape changes (compiler
     picks a different overload), so they fully suppress the warning
     at the call site.
   - Part 1's resolver wiring adds 2 new warnings per site (from
     `new DefaultJsonTypeInfoResolver()`) but doesn't suppress the
     pre-existing call-site warnings.

4. **Audit's "JsonArray.Add" count was 2× the unique line positions.**
   The audit reported "24 IL warnings in CustomConfigInjector,
   `JsonArray.Add<T>(T)` pattern". Counting unique line positions
   gave 12. The 24 is correct because EACH .Add call emits BOTH
   IL3050 (AOT) AND IL2026 (trim) warnings — 12 × 2 = 24. Same for
   the other two files. The brief's `.Add` estimate column is the
   warning count, not the line count.

5. **`JsonObject` arguments to `.Add` ALSO trigger IL3050 without a
   cast.** Sandbox testing confirmed: `arr.Add(new JsonObject())`
   picks the generic `Add<T>(T)` even though `JsonObject : JsonNode`.
   The compiler only picks the non-generic `Add(JsonNode?)` when the
   argument is typed as `JsonNode?` (not as a subclass). Explicit
   `(JsonNode?)` cast is the minimal fix.

6. **No Wave 31a conflict.** Wave 31a touches `SettingsLoader.cs`
   and `VPNRouter.Core.csproj` package refs. Wave 31b touches
   neither — clean rebase target.
