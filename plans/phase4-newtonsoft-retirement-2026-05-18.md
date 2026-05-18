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
*(filled by agent)*

## Follow-up

- Phase 5 AOT prep — STJ JsonSerializerContext-based source generation
  for AOT-friendly serialization (Android NativeAOT win).
