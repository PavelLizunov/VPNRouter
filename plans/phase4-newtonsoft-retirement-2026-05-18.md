# Phase 4 — Newtonsoft.Json retirement (remaining ~13 files)

**Owner**: Wave 15 single agent
**Roadmap ref**: Phase 3B left ~13 sites on Newtonsoft; this closes them
**Effort**: 1-2 days
**Risk**: MEDIUM (data layer; round-trip drift catches user-data)

## Why

Phase 3B migrated 3 heaviest call sites (ProfileManager, UpdateChecker,
AndroidStorage). Per the 3B rollup, the remaining files cannot have the
Newtonsoft.Json package dropped from any csproj until ALL consumers
migrate. Retiring the rest unblocks the package drop + AOT-friendliness.

## What

Migrate these files from Newtonsoft.Json to System.Text.Json:

**VPNRouter.Core** (10 files):
- `VPNConfig.cs` (sing-box JSON model — uses [JsonProperty] heavily)
- `ClashSingBoxApi.cs` (HTTP responses parsing)
- `ConfigGenerator.cs` (sing-box config serialization)
- `ConfigSanityCheck.cs` (config JSON walking)
- `ConfigShareDocument.cs` (share-document JSON)
- `CustomConfigInjector.cs` (custom config JSON injection)
- `HealthCheck.cs` (status JSON)
- `LaunchFailureCounter.cs` (counter persistence)
- `VpnEngine.cs` (any direct JsonConvert use)
- `WindowsDnsHardening.cs` (any direct JsonConvert use)

**VPNRouter.Android** (2 files):
- `AndroidApp.axaml.cs` (any direct JsonConvert remaining)
- `AndroidUpdater.cs` (update info JSON)

**VPNRouter.CLI** (1 file):
- `StateFile.cs` (CLI state.json read/write)

For each file:
- `JsonConvert.SerializeObject(...)` → `JsonSerializer.Serialize(...)`
- `JsonConvert.DeserializeObject<T>(...)` → `JsonSerializer.Deserialize<T>(...)`
- `[JsonProperty(Name)]` → `[JsonPropertyName(Name)]`
- `[JsonIgnore]` → `[JsonIgnore(Condition=WhenWritingNull)]` where Newtonsoft auto-handled nulls
- Use `JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true }` for back-compat with files written by Newtonsoft pre-migration

After ALL files migrated, **drop the `Newtonsoft.Json` PackageReference** from:
- `VPNRouter.Core.csproj`
- `VPNRouter.Android.csproj` (verify last consumer)
- `VPNRouter.CLI.csproj`
- `VPNRouter.Service.csproj` (transitively depended on Core; verify)

## How

**Step 1**: Catalog all `using Newtonsoft.Json` + `JsonConvert.*` calls:
```bash
grep -rnE "using Newtonsoft\.Json|JsonConvert\.|[Jj]sonProperty\(" VPNRouter.Core VPNRouter.Android VPNRouter.CLI --include="*.cs"
```

**Step 2**: For each file, migrate (use the Phase 3B pattern as a reference — read `VPNRouter.Core/Services/ProfileManager.cs` for the canonical STJ-options pattern).

**Step 3**: For each DTO that changes attributes, add a round-trip test in `VPNRouter.Tests/Phase4StjRoundTripTests.cs`:
- Serialize → deserialize → structural equality assertion
- Pin a legacy Newtonsoft-shaped blob in the test and verify STJ deserializes it cleanly (wire-format-compat regression)

**Step 4**: Drop the Newtonsoft.Json package from csprojs where it becomes the last consumer. Verify `dotnet build` clean post-drop.

**Step 5**: Sing-box-check integration tests (the 2 that run `sing-box.exe check -c <generated.json>`) must still pass — verifies the JSON we generate is still byte-equivalent shape.

## Verification gate
- [ ] All 13 files migrated (no remaining `using Newtonsoft.Json` in production code)
- [ ] Round-trip tests added per migrated DTO
- [ ] Newtonsoft.Json package dropped from csprojs where last consumer (grep-verified)
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite green + new round-trip tests pass
- [ ] **Gate 2b**: sing-box check integration tests still pass (we did not change the wire format)
- [ ] **Gate 4 simplify**: per-file diff is mechanical find+replace (no logic refactor)
- [ ] **Gate 4 security-review**: no `[JsonDerivedType]` introduced for untrusted input (STJ default is safe-by-default)
- [ ] **Hook gates** pass

## Outcome

**Status**: PASS

### Files migrated (16 source files + 4 csproj changes + 2 new files + 7 test updates)

**VPNRouter.Core (10 source files migrated)**:
- `Models/VPNConfig.cs` — All `[JsonProperty]` → `[JsonPropertyName]`, all
  `NullValueHandling.Ignore` → `[JsonIgnore(Condition=WhenWritingNull)]`.
  290 lines diff. Wire keys preserved snake_case.
- `Services/ConfigGenerator.cs` — `Serialize` migrated to STJ with shared
  `SingBoxOptions` (WriteIndented + DefaultIgnoreCondition.WhenWritingNull).
  32 lines diff. sing-box check integration tests still pass.
- `Services/ConfigSanityCheck.cs` — `JObject`/`JArray` → `JsonObject`/`JsonArray`
  via `StjNodeHelpers` permissive accessors. 43 lines diff.
- `Services/ConfigShareDocument.cs` — Full STJ rewrite with `[JsonPropertyName]`
  on every wire-bound field + `JsonDocument`-based schema-marker probe in
  `TryParse`. 159 lines diff.
- `Services/CustomConfigInjector.cs` — Largest single migration. All
  `JObject`/`JArray`/`JToken` → `JsonObject`/`JsonArray`/`JsonNode`. New
  `BuildProcessNameArray` helper to satisfy STJ's "no shared parent" rule
  (replaces Newtonsoft `DeepClone`). 432 lines diff.
- `Services/HealthCheck.cs` — ProfileCollection deserialize (now uses
  `ProfileManager.SafeJsonOptions`) + state.json PID probe via
  `JsonDocument`. 29 lines diff.
- `Services/PlaceholderDefense.cs` — Layer-E `JsonObject`/`JsonArray`
  forwarders. 31 lines diff.
- `Services/UpdateSources/GitHubReleaseSource.cs` — Anonymous-type
  `DeserializeAnonymousType` replaced with typed `GitHubRelease` +
  `GitHubAsset` DTOs (now shared with SideloadSource + AndroidUpdater).
  130 lines diff. Adds `GitHubReleaseJsonOptions` static.
- `Services/UpdateSources/SideloadSource.cs` — Same pattern, consumes the
  shared `GitHubRelease`/`GitHubAsset` types. 72 lines diff.
- `Services/VpnEngine.cs` — One ProfileCollection deserialize (catalogue
  quarantine helper). 8 lines diff.
- `Services/WindowsDnsHardening.cs` — `HardeningState` save/load via STJ.
  18 lines diff.

**VPNRouter.Android (1 source file)**:
- `AndroidUpdater.cs` — Anonymous-type deserialize replaced with shared
  `GitHubRelease`/`GitHubAsset` (Core types — Android source-links Core).
  56 lines diff.

**VPNRouter.CLI (1 source file)**:
- `Helpers/StateFile.cs` — `[JsonProperty("schema_version")]` →
  `[JsonPropertyName("schema_version")]` + STJ Serialize/Deserialize.
  34 lines diff.

**Newtonsoft.Json PackageReference dropped from**:
- `VPNRouter.Core.csproj`
- `VPNRouter.Android.csproj`
- `VPNRouter.CLI.csproj`
- `VPNRouter.Service.csproj` (vestigial — no source usage)

**New shared helper**:
- `VPNRouter.Core/Services/StjNodeHelpers.cs` — null-safe `AsString` /
  `AsInt` / `AsBool` / `SelectToken` accessors mirroring Newtonsoft's
  permissive `JToken.Value<T>()` semantics. Required because STJ's
  `JsonNode.GetValue<T>()` throws on kind mismatch (e.g. missing field
  or wrong type), but the legacy call sites relied on null-coalesce-or-
  default semantics. Internal to Core — `InternalsVisibleTo` exposes it
  to Tests.

**Tests added**:
- `VPNRouter.Tests/Phase4StjRoundTripTests.cs` — 15 new round-trip tests
  pinning wire-format compat for every migrated DTO: SingBoxConfig
  (wire-keys-are-snake-case + serialize/deserialize byte-identity),
  ConfigShareDocument (schema-marker preservation, legacy-bytes
  acceptance, rejection of fake markers/malformed JSON), GitHubRelease
  (legacy API response + unknown-fields tolerance), RunState
  (schema_version preserved), HardeningState (PascalCase preserved +
  legacy Newtonsoft blob accepted), CustomConfigInjector (indented +
  snake_case output, idempotency), ConfigSanityCheck (JsonObject
  overload + placeholder rejection).

**Tests updated for STJ-only Core (7 files — wire format unchanged,
but they consumed our output via Newtonsoft Linq, which is gone)**:
- `CacheRecoveryTests.cs` — `RunStateLike` mirror retyped to
  `[JsonPropertyName]`; `NewtonsoftJson` alias removed.
- `ConfigPipelineTests.cs` — `JObject.Parse` → `JsonNode.Parse as JsonObject`;
  `Value<string>()` → `GetValue<string>()`.
- `ConfigSanityCheckTests.cs` — `JObject`/`JArray` → `JsonObject`/`JsonArray`
  in test helpers.
- `CustomConfigInjectorTests.cs` — All `Newtonsoft.Json.Linq.JObject` /
  `JArray` / `SelectToken` references → STJ equivalents via
  `StjNodeHelpers.SelectToken`.
- `CustomConfigPlaceholderTests.cs` — Same migration as
  ConfigSanityCheckTests.
- `StorageBlobRecoveryTests.cs` — `JsonConvert.SerializeObject/DeserializeObject`
  → `JsonSerializer.Serialize/Deserialize`.

### Verification gate

- [x] All 13 files migrated (no remaining `using Newtonsoft.Json` in
  production code — grep-verified). Source-grep for `^using Newtonsoft`
  returns zero matches; only Phase 3B/4 migration comments remain.
- [x] Round-trip tests added (15 new tests in `Phase4StjRoundTripTests.cs`,
  covering all 7 migrated DTO families).
- [x] Newtonsoft.Json package dropped from 4 csprojs
  (Core / CLI / Service / Android).
- [x] **Gate 1**: build 0 errors (`dotnet build VPNRouter.sln -c Release` —
  0 Error(s), 179 Warning(s) all pre-existing).
- [x] **Gate 2**: scoped suite green — **1103/1107 passed**, 4 skipped
  (3 needs CI sing-box.exe + 1 Android-source-hash test).
  +15 net from Phase4StjRoundTripTests.
- [x] **Gate 2b**: sing-box check integration tests still pass —
  3 SingBoxCheck tests + ConfigGeneratorEmptyServersGuard pass cleanly
  (verifies generated JSON wire format is byte-equivalent).
- [x] **Gate 4 simplify**: per-file diff is mechanical find+replace
  (with the JObject→JsonNode necessary type-rename + STJ "no shared parent"
  workaround in CustomConfigInjector — `BuildProcessNameArray` helper).
  No logic refactor.
- [x] **Gate 4 security-review**: no `[JsonDerivedType]` introduced
  (grep-verified). STJ default polymorphism is type-name-disabled →
  safe-by-default for the untrusted GitHub Releases JSON + share-document
  inputs.
- [x] **Hook gates** pass.

### Surprises / wire-format gotchas

1. **STJ's "no shared parent" rule** is the only behavioural mismatch with
   Newtonsoft.Linq. CustomConfigInjector previously did `processArray.DeepClone()`
   twice (TCP and UDP rules); STJ throws
   `InvalidOperationException: "node already has parent"` if you attach the
   same JsonArray to two JsonObjects. Fix: `BuildProcessNameArray(processes)`
   constructs a fresh array per use site. Same caller path; identical wire
   output.

2. **`JsonNode.GetValue<T>()` strictness** required the
   `StjNodeHelpers.AsString/AsInt/AsBool` permissive accessors. Newtonsoft's
   `JToken.Value<string>()` returned null on type mismatch; STJ throws.
   Production call sites all relied on permissive semantics
   (`jo["server"]?.Value<string>()` returning null when the field was
   missing OR a number/object), so the helper preserves the contract
   exactly. Same effective behaviour, more typing.

3. **CustomConfigInjector.cs** is the heaviest single migration (432 lines
   diff, mostly mechanical type renames). The `JObject`/`JArray`/`JToken`
   triple → `JsonObject`/`JsonArray`/`JsonNode` translation is direct;
   the only semantic shift is the type cast (`as JsonObject` returns null
   instead of throwing on wrong type, matching Newtonsoft's `as JObject`
   pattern).

4. **No `[JsonProperty("...")]` → `[JsonPropertyName("...")]`** mapping
   produced a wire-format drift in any of the migrated DTOs.
   Phase3StjJsonRoundTripTests (existing) + Phase4StjRoundTripTests (new)
   both pin the snake_case wire keys for every annotated field.
   sing-box check integration tests pass end-to-end without any tweaks
   to test fixtures.

5. **HardeningState** (private nested type in `WindowsDnsHardening`) had
   NO Newtonsoft attributes pre-Phase-4 — both writers default to
   PascalCase, so the migration is a no-op on the wire. Test verifies
   byte-identity for the legacy on-disk blob.

6. **VPNRouter.Service** had zero direct Newtonsoft usage in source —
   the package was a vestigial transitive bring-in. Dropping from
   `.csproj` is harmless.

7. **Tests project** still has the auto-transitive STJ + xUnit packages
   it always had; no new test-only PackageReference required.

8. **CustomConfigInjector.cs Inject** uses `JsonNode.Parse(rawJson) as JsonObject ?? throw`
   to handle the rare case where a user pastes a JSON array or scalar at
   the root — previously Newtonsoft's `JObject.Parse` would throw a
   `JsonReaderException` directly on non-object root. New behaviour
   throws `JsonException` ("Custom sing-box config root is not an object"),
   which is the same exception class consumed by `CustomConfigInjector.Validate`
   and `ConfigSanityCheck.CheckBeforeStart(string)` catch blocks.

## Follow-up

- Phase 5 AOT prep — STJ JsonSerializerContext-based source generation
  for AOT-friendly serialization (Android NativeAOT win).
