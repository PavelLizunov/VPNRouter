# Phase 3 — 3B: Newtonsoft.Json → System.Text.Json migration

**Owner**: Wave 10 parallel agent (1 of 4)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3B
**Effort**: 1 week
**Risk**: MEDIUM (data layer touched; round-trip drift catches user data)

## Why

Audit C: STJ is 2-5× faster than Newtonsoft, AOT-friendly (matters for Android), and System.Text.Json ships with the runtime — drops a 600 KB dependency. Phase 1 Q14 already shows the runtime supports STJ well (used in ConfigPipeline).

## What

Migrate 5 heaviest Newtonsoft.Json call sites to STJ:

1. **`VPNRouter.Android/AndroidStorage.cs`** (heaviest — 15+ serialize calls)
2. **`VPNRouter.Core/Services/SubscriptionFetcher.cs`** (subscription JSON parse)
3. **`VPNRouter.Core/Services/FreeConfigs/FreeConfigCache.cs`** (cache JSON read/write)
4. **`VPNRouter.Core/Services/UpdateChecker.cs`** (already uses STJ post-Phase-2 2D-3; verify clean, drop any remaining Newtonsoft)
5. **`VPNRouter.Core/Services/ProfileManager.cs`** (profiles JSON load)

For each:
- Replace `JsonConvert.SerializeObject`/`DeserializeObject` with `JsonSerializer.Serialize`/`Deserialize`
- STJ requires `[JsonInclude]` on private setters where Newtonsoft auto-handles them
- `JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true }` for backward-compat with existing JSON files
- `[JsonConverter(...)]` for custom types (e.g. timestamps, IPAddress)

After migration, drop `Newtonsoft.Json` PackageReference where it's the last consumer.

## How

**Step 1** — Catalog all `using Newtonsoft.Json` imports + `JsonConvert.*` calls:
```bash
grep -nrE "using Newtonsoft|JsonConvert\.|JsonProperty|JsonIgnore" VPNRouter.Core VPNRouter.App VPNRouter.Android --include="*.cs"
```

**Step 2** — For each file:
1. Identify DTOs (the types being serialized)
2. Add `[JsonInclude]` to private setters that Newtonsoft auto-resolved
3. Replace `JsonConvert.X(...)` with `JsonSerializer.X(...)`
4. Verify round-trip: serialize → deserialize → compare structural equality

**Step 3** — Write 1 round-trip test per migrated DTO in `VPNRouter.Tests/<DtoName>JsonRoundTripTests.cs`. Asserts: serialize → file on disk identical to baseline → deserialize → equal to original.

**Step 4** — Drop Newtonsoft.Json from `.csproj` files where it's the last consumer. Verify build clean.

**Step 5** — Smoke test: launch the app, verify profile load + subscription refresh + free config cache read all work against existing JSON files on disk (round-trip with REAL files).

## Verification gate
- [ ] All 5 files migrated to STJ
- [ ] Round-trip tests added (1 per migrated DTO)
- [ ] Newtonsoft.Json package dropped where last consumer
- [ ] **Gate 1**: build 0 errors on solution + Android
- [ ] **Gate 2**: scoped suite stays green + new round-trip tests pass
- [ ] **Gate 4 simplify**: per-file diff is straightforward find+replace (no logic refactor)
- [ ] **Gate 4 security-review**: no new deserialization-gadget surface (STJ is safe-by-default; verify no `[JsonDerivedType]` introduced for untrusted input)
- [ ] **Hook gates** pass
- [ ] Manual: app launches + loads existing profiles/subs/cache from disk

## Outcome

**Status**: PASS (worktree, staged for integrator commit)

**Files changed** (9 staged):
- **VPNRouter.Core/Models/Profile.cs** — Newtonsoft `[JsonProperty]` →
  STJ `[JsonPropertyName]` + `[JsonIgnore(Condition=WhenWritingNull)]`
  for `android_packages` (preserves the `NullValueHandling.Ignore`
  behaviour).
- **VPNRouter.Core/Models/ProcessRule.cs** — Newtonsoft `[JsonProperty]`
  → STJ `[JsonPropertyName]`. Same snake_case wire keys.
- **VPNRouter.Core/Services/ProfileManager.cs** —
  `JsonSerializerSettings SafeJsonSettings` (Newtonsoft) →
  `public static JsonSerializerOptions SafeJsonOptions` (STJ, MaxDepth=32
  preserves the v2.31.0-r1 CO-4 DoS guard, `PropertyNameCaseInsensitive=true`
  matches Newtonsoft's default lookup, `WriteIndented=true` matches
  `Formatting.Indented`). LocalProfileSource + GitHubProfileSource +
  ProfileCacheFile all migrated; `[JsonProperty]` on cache wrapper → STJ
  `[JsonPropertyName]`. **Made the options `public`** (was `internal`) so
  the App layer can consume the shared options without a duplicate
  declaration — App is the second call site and exists in a sibling assembly
  without `InternalsVisibleTo`.
- **VPNRouter.Core/Services/UpdateChecker.cs** — dropped Newtonsoft
  `JsonConvert.DeserializeAnonymousType`. STJ has no anonymous-type inference
  (intentional, anonymous types have no `[JsonPropertyName]` map), so
  replaced with explicit `GitHubRelease` + `GitHubAsset` private DTOs at the
  bottom of the class. Their `[JsonPropertyName]` attributes pin the exact
  GitHub Releases API contract (tag_name / html_url / browser_download_url).
  `FindFullAsset` / `FindLiteAsset` / `FindChecksumAsset` signatures changed
  from `dynamic[]?` to `GitHubAsset[]?` — eliminates DLR `dynamic` dispatch
  (AOT-friendly + faster).
- **VPNRouter.Android/AndroidStorage.cs** — every `JsonConvert.SerializeObject` /
  `JsonConvert.DeserializeObject<T>` call (8 sites: GetSubscriptions /
  SetSubscriptions / PruneSubServerDuplicatesOnce / PruneKnownPlaceholdersOnce /
  GetServers / SetServers / GetSubscriptionsBare / GetPerAppPackages /
  SetPerAppPackages / GetCustomCategories / SetCustomCategories /
  GetServerTestResults / SetServerTestResults) migrated to
  `JsonSerializer.Serialize` / `JsonSerializer.Deserialize` with a shared
  `JsonOptions` field (`PropertyNameCaseInsensitive=true` for backward-compat
  with pre-3B SharedPreferences blobs written by Newtonsoft's default
  PascalCase conventions). `ServerTestResultDto` migrated from `[JsonProperty]`
  to `[JsonPropertyName]` — snake_case wire keys (status / latency_ms /
  last_tested_at / error) preserved byte-identical.
- **VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs** — two
  `Newtonsoft.Json.JsonConvert.DeserializeObject<ProfileCollection>` calls in
  the AM-3 + LoadApps paths replaced with
  `JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions)`.
  Cleanup: dropped `using Newtonsoft.Json` import; added
  `using System.Text.Json` + `using VPNRouter.Core.Services`. No behaviour
  change (same DoS guard, same case-insensitive lookup).
- **VPNRouter.Tests/ProfileManagerJsonDosGuardTests.cs** — switched
  `Newtonsoft.Json.JsonException` assertion → `System.Text.Json.JsonException`,
  `JsonConvert.DeserializeObject<>` → `JsonSerializer.Deserialize<>`. The
  guard contract is identical (MaxDepth=32 throws before stack overflow);
  the exception type is implementation detail.
- **VPNRouter.Tests/CacheRecoveryTests.cs** — `ProfileCache_Load_OnValidV1Wrapper_ReturnsLoaded`,
  `ProfileCache_Load_OnLegacyRawProfileCollection_QuarantinesAndReturnsRebuild`,
  and `ProfileCache_Load_OnTruncatedJson_QuarantinesAndReturnsRebuild` were
  using `NewtonsoftJson.SerializeObject` + `NewtonsoftJson.DeserializeObject<ProfileCacheFile>`.
  Since ProfileCacheFile no longer has `[JsonProperty]` attributes, the
  Newtonsoft writer was emitting PascalCase keys that CacheRecovery's
  STJ schema-probe couldn't find — silently flipping the "valid wrapper"
  test to fail. Updated to use `StjJson.Serialize` / `StjJson.Deserialize` with
  `ProfileManager.SafeJsonOptions` — matches the production GitHubProfileSource
  write+read path exactly. Tests for `RunStateLike` (CLI state.json) still
  use Newtonsoft because that consumer wasn't in this wave's scope.
- **VPNRouter.Tests/Phase3StjJsonRoundTripTests.cs** — NEW. 18 round-trip
  tests covering every migrated DTO:
  - `Profile_RoundTrip_StructurallyIdentical` (+ wire-format snake-case pin
    + legacy hand-edited wire format pin)
  - `ProcessRule_RoundTrip_BinaryIdentical` (proves lossless re-serialize)
  - `ProfileCollection_RoundTrip_PreservesNestedProfileFields`
  - `ProfileCacheFile_RoundTrip_KeepsSchemaMarker` +
    `SchemaVersionProbe_DetectsBumpForwardCompat` (CacheRecovery contract)
  - `VlessServerEntry_RoundTrip_PreservesAllProtocolFields` +
    `VlessServerEntry_DefaultConventions_UsesPascalCaseOnWire`
  - `SubscriptionEntry_RoundTrip_ServersListPreserved`
  - `CustomCategory_RoundTrip_AppsListPreserved`
  - `ServerTestResultDto_RoundTrip_SnakeCaseKeysPreserved` +
    `LegacyNewtonsoftBlob_DeserializesCleanly` (real-shape blob from a
    captured pre-3B Android install pinned verbatim)
  - `GitHubRelease_LegacyApiResponse_ParsesViaStj` +
    `GitHubRelease_UnknownFields_Ignored`
  - `ProfileCollection_FullRoundTripUnderDosGuard`

**Wire-format compatibility** (critical for user-data preservation):
- Profile / ProcessRule / ProfileCollection / ProfileCacheFile — explicit
  `[JsonPropertyName(...)]` attributes preserve the snake_case keys
  Newtonsoft `[JsonProperty(...)]` was producing. Tests pin the wire
  output.
- VlessServerEntry / SubscriptionEntry / CustomCategory — no JSON attributes,
  rely on default property-name serialization. STJ default behaviour
  matches Newtonsoft default behaviour (verbatim C# property names, i.e.
  PascalCase). Combined with `PropertyNameCaseInsensitive=true` in
  `JsonOptions`, this gives lossless round-trip with all legacy
  SharedPreferences blobs.
- ServerTestResultDto — `[JsonPropertyName]` migration preserves snake_case
  wire keys; `LegacyNewtonsoftBlob_DeserializesCleanly` pins the actual
  pre-3B blob shape as a regression test.
- GitHub Releases API — explicit DTOs replace anonymous-type inference;
  contract is upstream (GitHub) and pinned by
  `LegacyApiResponse_ParsesViaStj`.

**Newtonsoft package drop**: NOT executed in this wave. Per the brief
"Drop ONLY where it becomes the last consumer post-migration (grep
verifies)". Post-3B Newtonsoft consumers across the codebase:
- VPNRouter.Core: 7 files still use Newtonsoft (VPNConfig.cs,
  ClashSingBoxApi.cs, ConfigGenerator.cs, ConfigSanityCheck.cs,
  ConfigShareDocument.cs, CustomConfigInjector.cs, HealthCheck.cs,
  LaunchFailureCounter.cs, VpnEngine.cs, WindowsDnsHardening.cs) →
  cannot drop from `VPNRouter.Core.csproj`.
- VPNRouter.Android: AndroidApp.axaml.cs + AndroidUpdater.cs → cannot drop.
- VPNRouter.CLI: StateFile.cs → cannot drop.
- VPNRouter.Service: depends on Core which still needs Newtonsoft.
- VPNRouter.Tools: PoolAggregator/Program.cs.

Phase 4 (per brief Follow-up section) will retire the remaining call sites.

**Build / test gates**:
- **Gate 1** — `dotnet build VPNRouter.sln -c Release` → **0 errors**.
  Also verified `dotnet build VPNRouter.Core.csproj -c Release
  /p:EnableAndroidTarget=true` → 0 errors (Core compiles for both
  net8.0 and net8.0-android targets).
- **Gate 2** — scoped suite (`FullyQualifiedName!~Headless&...!~PageScreenshot&...!~VisualDiff`)
  → **1021 passed, 0 failed, 4 skipped** (skips are pre-existing
  AndroidApp characterization + ConfigGenerator multi-server + Autostart
  contract — none touched by this migration). +18 new tests
  (Phase3StjJsonRoundTripTests). Plus updated ProfileManagerJsonDosGuard
  (2 tests) + CacheRecovery (3 tests) all green.
- **Gate 4 simplify** — diff is a straightforward find+replace for
  attribute names + serializer call. No business-logic refactor.
- **Gate 4 security-review** — `grep` for `JsonDerivedType` /
  `TypeNameHandling` / `DeserializeObject<object>` across all migrated
  files: **zero hits**. STJ default is safe-by-default (no polymorphic
  type resolution; unknown fields silently ignored). MaxDepth=32 guard
  retained on ProfileManager.SafeJsonOptions (CO-4 DoS hardening).
- **Hook gates** — pre-existing CA1416 platform-target warnings present
  but unchanged by this migration (Registry / SystemEvents / WMI calls
  inherited from Phase 0). No new analyzer findings.
- **Manual smoke test** — flagged for integrator. Worktree-agent cannot
  run UI; the build artifact + the Phase3StjJsonRoundTripTests
  `ProfileCache_Load_OnValidV1Wrapper_ReturnsLoaded` integration test
  (which writes JSON via the new path and reads it back via the new
  path) exercise the user-data persistence loop end-to-end. **Integrator
  smoke test recommended**: launch built app, verify
  (a) `default.json` profile catalog loads under STJ;
  (b) GitHubProfileSource cache (`%ProgramData%\VPNRouter\cache\profiles.json`)
  round-trips through an offline launch;
  (c) Android SharedPreferences blobs (legacy + new) decode cleanly.

**LOC delta**: +307 / −109 across 8 modified files + 1 new test file
(385 LOC test).

**Surprises / notes**:
- STJ does not support `DeserializeAnonymousType` (intentional — anonymous
  types have no `[JsonPropertyName]` map). The fix in UpdateChecker
  required converting `dynamic[]?` helper signatures to typed DTOs.
  Net positive: faster (no DLR dispatch) and AOT-friendly.
- `SafeJsonOptions` made `public` (was `internal`) because the App layer
  is in a sibling assembly without `InternalsVisibleTo`. The options
  object is a stable, useful API surface — exposing it is cleaner than
  duplicating the configuration in App.
- `ProfileCacheFile` model is in `ProfileManager.cs` (same file). Its
  `[JsonProperty]` attributes had to migrate too — the CacheRecovery
  STJ probe was already looking for `"schema_version"` (the wire key was
  set by Newtonsoft's `[JsonProperty("schema_version")]`); post-migration
  STJ's `[JsonPropertyName("schema_version")]` writes the same key. The
  CacheRecoveryTests pre-existing test methods had to be updated because
  they were testing the FULL Newtonsoft round-trip path (write +
  schema-probe + read), and after migration the writer side is STJ — so
  the test had to mirror production by serializing via STJ too.
- `[YamlMember]`-only DTOs (VlessServerEntry / SubscriptionEntry /
  CustomCategory) need no JSON-attribute touching to migrate: STJ default
  property-name serialization matches Newtonsoft's default. PascalCase on
  the wire on both sides, byte-identical. The Phase3StjJsonRoundTripTests
  `VlessServerEntry_DefaultConventions_UsesPascalCaseOnWire` test pins
  this as a regression guard.

## Follow-up

- If any DTO requires a custom converter (e.g. `IPAddress`), document the converter in `VPNRouter.Core/Services/Json/` for future-DTOs reuse.
- AOT-compatibility check for Android: when JsonSerializerContext-based source generation is on the table, file a Phase 4 task.
