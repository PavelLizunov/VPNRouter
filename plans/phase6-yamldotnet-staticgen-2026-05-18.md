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

_To be filled in after verification gate passes._

### Tooling state

- TBD

### What actually happened

- TBD

### Files changed / LOC delta

- TBD

### Tests added

- TBD

### Surprises

- TBD

### Commit hashes (worktree `worktree-agent-ae9c2310a04cdf9bd`)

- TBD
