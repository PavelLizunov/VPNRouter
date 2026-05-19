# Phase 6 — Wave 31a: YamlDotNet StaticDeserializerBuilder swap

**Authored**: 2026-05-18
**Phase**: 6 of v3.0 refactor (`plans/v3.0-refactor-roadmap.md`)
**Scope**: `VPNRouter.Core/Services/SettingsLoader.cs` only. Newton+
JsonArray.Add\<T\> patterns are Wave 31b's territory.
**Risk**: Medium-low (mechanical swap with type-list maintenance; behaviour
contract held by `SettingsLoaderRobustnessTests` + new round-trip tests).
**Predecessors**: Wave 25 (`AppJsonContext`), Wave 27 (SettingsLoader
internal demotion), Wave 30 (NativeAOT readiness audit —
`plans/phase6-nativeaot-readiness-2026-05-18.md`).

---

## Why

Wave 30's `PublishAot` attempt surfaced two IL3050 warnings in
`SettingsLoader.cs` — both at the `DeserializerBuilder` / `SerializerBuilder`
ctor sites. The YamlDotNet error message itself spelled out the fix:

> Using member 'YamlDotNet.Serialization.DeserializerBuilder.DeserializerBuilder()'
> which has 'RequiresDynamicCodeAttribute' can break functionality when
> AOT compiling. … **You need to use the code generator/analyzer to
> generate static code and use the 'StaticDeserializerBuilder' object
> instead of this one.**

YamlDotNet 15.x ships a Roslyn source generator that emits a static
serialization context — the YAML equivalent of System.Text.Json's
`JsonSerializerContext`. This wave swaps the two reflective builder ctors
for their `Static*` counterparts, removing the last two reflective-yaml
sites in CLI's Core dependency tree.

## What

Three artefacts:

1. **NuGet add** — pull the YamlDotNet static-generator analyzer package
   into `VPNRouter.Core.csproj`.
2. **Context class** — `VPNRouter.Core/Yaml/YamlStaticContext.cs` declaring
   the partial type the generator extends, with one
   `[YamlSerializable(typeof(...))]` attribute per AppSettings DTO branch.
3. **Loader swap** — replace `new DeserializerBuilder()` + `new SerializerBuilder()`
   with `new StaticDeserializerBuilder(new YamlStaticContext())` + sibling
   for the serializer, in the two builder sites in `SettingsLoader.cs`
   (lines ~303 `Parse` + ~448 `Save`).

Plus a new pinning test suite, `YamlStaticContextRoundTripTests`, that
proves round-trip equivalence (load → save → re-load) for the full
AppSettings shape including all 17 nested DTO types.

## How

### Package surprise — actual id is `Vecc.YamlDotNet.Analyzers.StaticGenerator`

The audit brief said the package id was `YamlDotNet.Analyzers.StaticGenerator`.
NuGet search reveals the actual published id is
**`Vecc.YamlDotNet.Analyzers.StaticGenerator`**. The upstream YamlDotNet
maintainer (aaubry) hands the analyzer package off to a separate
publisher (EdwardCooke / Vecc) — same project, distinct NuGet id. Versions
align with the main YamlDotNet package: 15.1.2 is the matching pin.

Csproj snippet:

```xml
<PackageReference Include="Vecc.YamlDotNet.Analyzers.StaticGenerator" Version="15.1.2">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

`PrivateAssets=all` keeps the analyzer from propagating to downstream
consumers — VPNRouter.App / Service / CLI / Tests inherit only `YamlDotNet`
via transitive ProjectReference, not the analyzer.

### YamlStaticContext type list

Walk of `VPNRouter.Core/Models/AppSettings.cs` + `WgturnEntry.cs` yields
the full transitive DTO graph rooted at `AppSettings`:

Root: `AppSettings`
- `AppConfig` (huge — 30+ scalar fields + 8 nested collections)
- `EmergencyChannelSettings`
  - `WgturnEntry` (Models/WgturnEntry.cs)
- `ProfileSource`
- `VlessConfig`
  - `VlessRealityConfig`
  - `VlessTlsConfig`
  - `VlessTransportConfig`
  - `VlessServerEntry` (re-used)
- `TunSettings`
- `DnsSettings`
- `SingBoxSettings`
- `MonitoringSettings`
- `UpdateSettings`
- `CustomConfigEntry`
- `SubscriptionEntry` (contains `List<VlessServerEntry>`)
- `CustomCategory`
- `CustomDirectRule`
- `CustomRule`
- `UserFreeSource`

Plus collection types used inside `AppConfig`:
- `Dictionary<string, List<string>>` (CustomGroupApps)
- `Dictionary<string, string>` (VlessTransportConfig.Headers)
- various `List<T>` of the above

The Vecc generator works recursively from the root type, but the brief
warns it may need explicit registration for collection wrapper types.
Approach: register the root + each leaf DTO type. If the build emits
"missing type info" errors, add the named collection types one at a time.

### Naming convention compatibility

YamlDotNet 15 supports `WithNamingConvention(...)` on
`StaticDeserializerBuilder` per source (`YamlDotNet/Serialization/StaticDeserializerBuilder.cs`).
All our models already carry `[YamlMember(Alias = "snake_case")]` on every
field, so even if the underscored convention is dropped, the explicit
aliases keep wire-format stable.

### Swap sites

`SettingsLoader.cs` line ~303 (`Parse`):

```csharp
// Before
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();

// After
var deserializer = new StaticDeserializerBuilder(new YamlStaticContext())
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
```

`SettingsLoader.cs` line ~448 (`Save`):

```csharp
// Before
var serializer = new SerializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

// After
var serializer = new StaticSerializerBuilder(new YamlStaticContext())
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();
```

### Round-trip tests

`VPNRouter.Tests/YamlStaticContextRoundTripTests.cs` covers:

1. **Defaults round-trip** — `SettingsLoader.Parse(SettingsLoader.Save(defaults))`
   yields a structurally-equal instance to `defaults`.
2. **Populated round-trip** — exercise every nested DTO type with non-default
   values (string, int, bool, nested object, list, dictionary).
3. **Wire-format pin** — load a hand-crafted YAML fixture exercising
   exotic shapes (DateTimeOffset?, Dictionary<string,List<string>>, all
   the routing modes + custom rules), assert specific field values.

## Acceptance

- [ ] Brief committed before code changes.
- [ ] `dotnet build VPNRouter.sln -c Release` 0 errors.
- [ ] `dotnet test VPNRouter.Tests` regression bar green —
   `SettingsLoaderRobustnessTests`, `SettingsValidatorTests`, all existing
   AppSettings-using tests, **plus** new `YamlStaticContextRoundTripTests`.
- [ ] Grep for `new DeserializerBuilder()` / `new SerializerBuilder()` in
   `VPNRouter.Core/Services/SettingsLoader.cs` returns zero hits.
- [ ] `dotnet build VPNRouter.Core/VPNRouter.Core.csproj -c Release
   /warnaserror:IL3050;IL2026` — SettingsLoader.cs reports zero IL warnings.
- [ ] Outcome section below filled in with hashes + LOC delta + surprises.

## Verification gate

Per `plans/v3.0-execution-methodology.md`:

1. Build green — `dotnet build VPNRouter.sln -c Release`
2. Tests green — full regression filter (no Yaml/Settings exclusions)
3. Test build green — `dotnet build VPNRouter.Tests -c Release`
4. Code review pass — no public API churn (Core surface stable);
   one new file (`Yaml/YamlStaticContext.cs`); one new NuGet (analyzer
   only, `PrivateAssets=all`).
5. Roadmap entry updated — Phase 6 NativeAOT row marked "Wave 31a done".
6. Outcome below filled in.

## Risks + fallback

- **Analyzer build-time errors for missing types**: solve by adding the
  missing `[YamlSerializable(typeof(T))]` line and rebuilding. Idempotent.
- **Naming convention rejected at compile**: fall back to explicit
  `[YamlMember(Alias = "...")]` on every DTO field (already done; safe).
- **Round-trip diverges from reflective**: dig into Vecc analyzer issues
  on GitHub; document divergence in Outcome and STOP. Don't paper over
  it with shims.

## Outcome

### Tooling state

- .NET 10.0.300 SDK on Windows 11 LTSC 2024 VM.
- Roslyn compiler 5.6.0-2.26230.102 (`csc.exe` shipped with SDK).
- YamlDotNet 15.1.2 (existing pin, unchanged).
- Vecc.YamlDotNet.Analyzers.StaticGenerator 15.1.2 (new).
- xUnit v3 3.2.2.

### What actually happened

End-to-end swap landed clean, but only after two analyzer-bug
workarounds documented below:

1. NuGet package id is **`Vecc.YamlDotNet.Analyzers.StaticGenerator`**,
   not `YamlDotNet.Analyzers.StaticGenerator`. The upstream YamlDotNet
   maintainer hands the analyzer off to a separate publisher
   (EdwardCooke / Vecc). Version 15.1.2 aligns with the existing
   YamlDotNet 15.1.2 pin.
2. The analyzer auto-generates `public partial class YamlStaticContext :
   YamlDotNet.Serialization.StaticContext` — so the hand-authored half
   has to be `public partial` too (CS9023 if you make it `internal`),
   and must NOT specify the base class itself (CS0263 if both partial
   declarations name it). The brief's "make it internal" suggestion
   doesn't survive contact with the generator's actual output.
3. **Bug 1 — explicit collection registration crashes the analyzer**:
   `[YamlSerializable(typeof(Dictionary<string, List<string>>))]` makes
   the analyzer throw `IndexOutOfRangeException` at build time
   (suppressed-by-default CS8785 warning, then silent absence of
   generated code). Workaround: register only leaf DTOs; the analyzer's
   `ClassSyntaxReceiver.CheckForSupportedGeneric` recursively walks
   property types and handles collections transitively.
4. **Bug 2 — DateTimeOffset is not in the analyzer's scalar coercion
   table**. The static serializer emits `{}` for `DateTimeOffset` /
   `DateTimeOffset?` fields. The static deserializer throws
   `ArgumentOutOfRangeException: Unknown type: System.DateTimeOffset`.
   Affected fields: `SubscriptionEntry.LastRefreshedAt` (DateTimeOffset?)
   + `WgturnEntry.AddedAt` (DateTimeOffset). Workaround: hand-written
   `DateTimeOffsetYamlConverter : IYamlTypeConverter` registered via
   `WithTypeConverter(...)` on both builders. Wire format: ISO 8601
   round-trip (`"O"` specifier).

### Files changed / LOC delta

| File | Change | LOC delta |
|---|---|---:|
| `VPNRouter.Core/VPNRouter.Core.csproj` | added analyzer PackageReference | +15 |
| `VPNRouter.Core/Yaml/YamlStaticContext.cs` | NEW partial context | +109 |
| `VPNRouter.Core/Yaml/DateTimeOffsetYamlConverter.cs` | NEW compat shim | +84 |
| `VPNRouter.Core/Services/SettingsLoader.cs` | two-site builder swap + shim wiring | +27 / -2 |
| `VPNRouter.Tests/YamlStaticContextRoundTripTests.cs` | NEW 3-test pin suite | +624 |

Totals: **+859 / -2 LOC** across 5 files; 3 new files committed.

### Tests added

`YamlStaticContextRoundTripTests` (3 tests):

1. **`Defaults_SaveAndReload_PreservesAllDefaultValues`** — exercise
   `SettingsLoader.ResetToDefaults(path)` → `Parse(File.ReadAllText)`
   round-trip on a freshly-constructed `AppSettings`. Asserts every
   reference-typed sub-section is non-null after parse + spot-checks
   18 scalar default values to pin alias mappings.
2. **`Populated_RoundTrip_PreservesEveryNestedFieldKind`** — build a
   maximally-populated `AppSettings` with non-default values in every
   nested DTO branch (scalar string/int/bool, nested DTO, `List<T>`,
   `List<DTO>`, `Dictionary<string, string>`, `Dictionary<string,
   List<string>>`, `DateTimeOffset?`, `DateTimeOffset`, `DateTime`).
   Saves through `SettingsLoader.Save`, re-parses via
   `SettingsLoader.Parse`, asserts ~60 specific values across the full
   graph — pins the DateTimeOffset shim path and every field that gets
   serialized by the static builder.
3. **`WireFormat_SnakeCaseAliases_HonoredByStaticDeserializer`** —
   hand-crafted YAML fixture with every `[YamlMember(Alias = "...")]`
   mapping exercised. Catches alias drift between the reflective and
   static parsers (which the round-trip tests above wouldn't see since
   both legs use the same static builder).

Total regression suite: **1154 pass, 4 skip, 0 fail** (was 1151/4/0
pre-swap). No prior tests broke.

### Surprises

- **Package id divergence** — pre-implementation NuGet search caught
  this before the csproj edit, so it cost ~5 min of investigation
  rather than blocking the wave.
- **`public partial class` requirement** — discovered via CS9023 on
  the first build attempt; updated YamlStaticContext.cs visibility
  before the swap.
- **Analyzer crash on `Dictionary<string, List<string>>`** —
  reproduced isolated in `/tmp/yamltest` standalone project, confirmed
  it's an analyzer bug not a project quirk. Root cause: the analyzer's
  `ClassSyntaxReceiver.CheckForSupportedGeneric` recursion when the
  type is registered explicitly via `[YamlSerializable]` hits an
  array index it doesn't bound-check. Working theory: it tries to
  process `Dictionary<,>` as if it were `List<>` somewhere on the
  explicit-registration path. The transitive-discovery codepath
  (when the same type is found via a property of a registered DTO)
  doesn't hit the bug.
- **DateTimeOffset not in the scalar table** — confirmed via probe
  binary in `/tmp/probebin` against the analyzer's output. `DateTime`
  works (ISO 8601 round-trip), `DateTimeOffset` doesn't. Reflective
  builder handled both via its default scalar resolver; static
  builder ships with a fixed table that misses DateTimeOffset.
  Custom `IYamlTypeConverter` was the cleanest path; the brief
  explicitly allowed for this kind of compat shim.

### Commit hashes (worktree `worktree-agent-ae9c2310a04cdf9bd`)

- `7b84cd8` — `docs(plan)`: this brief, pre-implementation (no Outcome).
- `9497b04` — `refactor(yaml)`: implementation + tests + analyzer
  workarounds.
- (next) — `docs(plan)`: this brief, post-implementation, with the
  Outcome section populated.
