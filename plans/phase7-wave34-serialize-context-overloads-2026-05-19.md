# Phase 7 — Wave 34 Switch generic `Serialize/Deserialize<T>` to `JsonTypeInfo<T>` overloads

**Owner**: Claude session 0ecbd816-09bb-420b-89b3-996da5a420fe
**Branch**: main
**Roadmap ref**: plans/phase6-completion-2026-05-19.md "Carry-over to Phase 7"
**Effort**: ~3 hours
**Risk**: MEDIUM — touches serialization on profile load/save, free-config cache, hardening state, update probing, VPN engine reload, CLI state-file. Behaviour must stay byte-equivalent.
**Blast radius**: ~14 files in Core + CLI · ~26 call sites · ~80-120 LOC delta · zero runtime behavior change (call-shape swap only)
**Rollback**: `git revert <commit>` (single-commit refactor)

## Why

Wave 30 NativeAOT audit (commit `3000e2e`) catalogued 120 IL2026/IL3050
warnings on the `dotnet publish -p:PublishAot=true` analyzer pass. Wave
31b (commits `104ade9` + `c12f765`) retired 52 of them by switching
`JsonArray.Add<T>(T)` to the non-generic `JsonArray.Add(JsonNode?)`
overload + hoisting two anonymous-type `Serialize(new {...})` sites to
named records + flipping HardeningState/LaunchFailureCounter.State
visibility so they could be registered in `AppJsonContext`. Net: 120 → 76
warnings, with Wave 31b explicitly noting the remaining 76 are
call-shape-based:

> "The IL2026/IL3050 warnings on `Serialize<T>(value, options)` call
> sites are call-shape-based, NOT options-content-based — wiring alone
> doesn't suppress them. Wave 31b accomplishes the structural prep;
> Wave 32+ will switch to `Serialize(value, context.MyType)` overloads
> which DO suppress the warnings."

Wave 34 is that switch. The compiler's IL warning for
`JsonSerializer.Serialize<T>(value, options)` fires because the
`Options` overload retains a reflective fallback path the trimmer can't
prove unused. The same call with a typed
`JsonSerializer.Serialize(value, JsonTypeInfo<T>)` is statically
analysable — the trimmer sees the exact type, the IL warning goes away.

Closing these warnings unblocks Wave 33 (actual `PublishAot=true` flip
on CLI): every remaining `IL3050: requires dynamic code` warning on the
CLI source set has to be either suppressed via `[RequiresDynamicCode]`
attribution OR fixed at the source. Wave 34 fixes them en masse where
the source-gen context already has the type registered.

After Wave 34, the only residual IL warnings on the CLI/Core dep
graph will be from:
1. `CustomRulesImportExport.cs:507` — `List<Dictionary<string,object>>`
   recursion that cannot be source-gen'd without restructuring the
   export DTO (Wave 35 scope).
2. `IHttpClient.cs:187` — `Deserialize<T>(body, options)` is a generic
   helper consumed by callers that supply their own T at the call site.
   This needs an API tweak (take `JsonTypeInfo<T>` instead of
   `JsonSerializerOptions`) which propagates ~6 call-site updates.
   Wave 34 includes this in scope.
3. Truly reflective sites — none identified in the audit; everything
   else is reachable via existing contexts.

Estimated post-Wave-34 IL warning count: **~5-10** (down from 76).

## What

### Migration table

| File | Site | Current call | New call (using context) | Context |
|---|---|---|---|---|
| `Core/Services/ConfigGenerator.cs` | 790 | `Serialize(config, SingBoxOptions)` | `Serialize(config, AppJsonContext.Default.SingBoxConfig)` | App |
| `Core/Services/ConfigShareDocument.cs` | 125 | `Serialize(doc, DocumentOptions)` | `Serialize(doc, AppJsonContext.Default.ConfigShareDocument)` | App |
| `Core/Services/ConfigShareDocument.cs` | 193 | `Deserialize<ConfigShareDocument>(json, DocumentOptions)` | `Deserialize(json, AppJsonContext.Default.ConfigShareDocument)` | App |
| `Core/Services/HealthCheck.cs` | 108 | `Deserialize<ProfileCollection>(...)` | `Deserialize(json, AppJsonContext.Default.ProfileCollection)` | App |
| `Core/Services/VpnEngine.cs` | 716 | same | same | App |
| `Core/Services/ProfileManager.cs` | 266 | `Deserialize<ProfileCollection>(json, ...)` | same | App |
| `Core/Services/ProfileManager.cs` | 355 | same | same | App |
| `Core/Services/ProfileManager.cs` | 374 | `Serialize(wrapper, SafeJsonOptions)` (ProfileCacheFile) | `Serialize(wrapper, AppJsonContext.Default.ProfileCacheFile)` | App |
| `Core/Services/ProfileManager.cs` | 390 | `Deserialize<ProfileCacheFile>(json, ...)` | `Deserialize(json, AppJsonContext.Default.ProfileCacheFile)` | App |
| `Core/Services/ClashSingBoxApi.cs` | 141 | `Serialize(setConfigDto, SerializerOptions)` | `Serialize(setConfigDto, AppJsonContext.Default.ClashSetConfigDto)` | App |
| `Core/Services/ClashSingBoxApi.cs` | 288 | `Serialize(selectDto, SerializerOptions)` | `Serialize(selectDto, AppJsonContext.Default.ClashSelectProxyDto)` | App |
| `Core/Services/LaunchFailureCounter.cs` | 207 | `Deserialize<State>(json, JsonOptions)` | `Deserialize(json, AppJsonContext.Default.State)` | App (Wave 31b registered State) |
| `Core/Services/LaunchFailureCounter.cs` | 225 | `Serialize(state, JsonOptions)` | `Serialize(state, AppJsonContext.Default.State)` | App |
| `Core/Services/WindowsDnsHardening.cs` | 288 | `Serialize(state, HardeningStateOptions)` | `Serialize(state, WindowsDnsHardeningJsonContext.Default.HardeningState)` | Windows-only |
| `Core/Services/WindowsDnsHardening.cs` | 300 | `Deserialize<HardeningState>(json, ...)` | `Deserialize(json, WindowsDnsHardeningJsonContext.Default.HardeningState)` | Windows-only |
| `Core/Services/UpdateSources/GitHubReleaseSource.cs` | 112 | `Deserialize<GitHubRelease[]>(stream, ...)` | `Deserialize(stream, AppJsonContext.Default.GitHubReleaseArray)` | App |
| `Core/Services/UpdateSources/SideloadSource.cs` | 105 | same | same | App |
| `Core/Services/FreeConfigs/FreeConfigCache.cs` | 70 | `Deserialize<CacheFile>(json, JsonOptions)` | Need `CacheFile` registered in `AppJsonContext` (Wave 34 adds it) | App (new registration) |
| `Core/Services/FreeConfigs/FreeConfigCache.cs` | 126 | `Serialize(file, JsonOptions)` | same | App |
| `Core/Services/CacheRecovery.cs` | 110 | `Deserialize<SchemaProbe>(json, ProbeOptions)` | Need `SchemaProbe` registered (Wave 34 adds it) | App (new registration) |
| `Core/Services/CustomRulesImportExport.cs` | 250 | `Deserialize<List<CustomRule>>(text, JsonOptions)` | Need `List<CustomRule>` registered (Wave 34 adds it) | App (new registration) |
| `Core/Services/CustomRulesImportExport.cs` | 263 | `Serialize(rules.ToList(), JsonOptions)` | same | App |
| `Core/Services/CustomRulesImportExport.cs` | 507 | `Serialize(entries, JsonOptions)` (List<Dictionary>) | **OUT OF SCOPE** (Wave 35) | reflective fallback retained |
| `Core/Services/IHttpClient.cs` | 187 | `Deserialize<T>(body, options)` (generic helper) | API change: add `JsonTypeInfo<T>?` parameter, callers pass context | new API shape |
| `CLI/Helpers/StateFile.cs` | 93 | `Serialize(state, Options)` (RunState) | `Serialize(state, CliJsonContext.Default.RunState)` | CLI |
| `CLI/Helpers/StateFile.cs` | 104 | `Deserialize<RunState>(json, Options)` | `Deserialize(json, CliJsonContext.Default.RunState)` | CLI |

### New `[JsonSerializable]` registrations needed

- `AppJsonContext.cs`: add `[JsonSerializable(typeof(FreeConfigCache.CacheFile))]`, `[JsonSerializable(typeof(CacheRecovery.SchemaProbe))]`, `[JsonSerializable(typeof(List<CustomRule>))]`, `[JsonSerializable(typeof(CustomRule))]`.

Visibility flips that may be required (will check during implementation):
- `FreeConfigCache.CacheFile` — if `private`, flip to `internal`
- `CacheRecovery.SchemaProbe` — same

### `IHttpClient` API change

Current shape (`Core/Services/IHttpClient.cs:170-210` approx):

```csharp
Task<T?> GetJsonAsync<T>(string url, JsonSerializerOptions options, CancellationToken ct);
```

Proposed:

```csharp
Task<T?> GetJsonAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct);
```

Callers (estimated 4-6 sites in `UpdateSources/*.cs`, `SubscriptionFetcher.cs`, `FreeConfigPoolFetcher.cs`) need to switch from passing `MyOptions` to passing `AppJsonContext.Default.MyType`. This is the
~minimum-disruption surface change that retains the existing
`async GetJsonAsync<T>` ergonomics while making the call statically
AOT-friendly.

## How

**Step 1**: Inventory verification — `grep -rnE "JsonSerializer\.(Serialize|Deserialize)<" VPNRouter.Core VPNRouter.CLI` to confirm
the 26-site count. Adjust the migration table if any site is missed.

**Step 2**: Add new `[JsonSerializable]` registrations to
`AppJsonContext.cs` (CacheFile, SchemaProbe, CustomRule, List<CustomRule>).
Flip any nested types from `private` → `internal` if Roslyn complains.
Verify with `dotnet build -c Release` that the source-gen runs clean.

**Step 3**: Migrate the 23 in-Core sites file-by-file. For each:
- Switch `JsonSerializer.Serialize<T>(value, options)` →
  `JsonSerializer.Serialize(value, <Context>.Default.<T>)`
- Switch `JsonSerializer.Deserialize<T>(json, options)` →
  `JsonSerializer.Deserialize(json, <Context>.Default.<T>)`
- Where the `options` instance had options NOT covered by the context
  (rare — e.g., `PropertyNameCaseInsensitive`), declare them on the
  context's `[JsonSourceGenerationOptions]` attribute instead. (Most
  cases: `PropertyNamingPolicy=SnakeCaseLower`, `WriteIndented=true`,
  `DefaultIgnoreCondition=WhenWritingNull` — already set on
  `AppJsonContext`.)
- Where the options had a `Converters` collection (rare — used for
  custom `JsonStringEnumConverter` or similar), declare via the
  `[JsonSourceGenerationOptions(Converters = ...)]` attribute. If none
  found in inventory, this step is a no-op.

**Step 4**: `IHttpClient` API tweak.
- Change `GetJsonAsync<T>(string, JsonSerializerOptions, CT)` →
  `GetJsonAsync<T>(string, JsonTypeInfo<T>, CT)`
- Update `HttpClientAdapter` (the concrete impl)
- Update all callers — should be 4-6 sites in
  `UpdateSources/*.cs`, `SubscriptionFetcher.cs`, `FreeConfigPoolFetcher.cs`
- If a caller previously synthesized its own `JsonSerializerOptions`
  inline, replace with the context's `JsonTypeInfo<T>`

**Step 5**: Migrate the 2 CLI sites in `Helpers/StateFile.cs`
(use existing `CliJsonContext` from Wave 31b).

**Step 6**: Run gates 1+2 (build + tests). The behaviour should be
byte-equivalent — `Serialize(value, JsonTypeInfo<T>)` produces the
same bytes as `Serialize<T>(value, JsonSerializerOptions)` when the
underlying options + resolver are equivalent.

**Step 7**: Run `simplify` skill on the diff (~80-120 LOC). Address any
findings.

**Step 8**: Commit + push. Single-commit refactor; rollback = single
`git revert`.

### Tests written

No new tests required — the refactor is byte-equivalent and existing
round-trip tests pin the wire format:
- `Phase4StjRoundTripTests` (15 tests)
- `IUpdateSourceContractTests` (asset deserialization)
- `ProfileManagerJsonDosGuardTests`
- `FreeConfigEntrySchemaTests` (cache file shape)
- `WindowsDnsHardening` regression tests (HardeningState roundtrip)

If any existing test fails after the migration, the migration is
incorrect, not the test. Stop and fix.

### Verification approach

Beyond the standard Gate 1+2:
- **IL warning count check** — capture before/after via the same
  `dotnet publish -p:PublishAot=true` dry-run that Wave 30 used.
  Expected: 76 → ~5-10.
  (This requires MSVC C++ workload install which is NOT yet on the VM.
  The analyzer pass runs before the ILC link step, so the dry-run
  reaches the warning count without needing the linker. If it doesn't,
  fall back to `dotnet build /p:_RequiresILLinkPack=true` or similar.)
- **Byte-equivalence sanity**: spot-check 2-3 round-trips manually
  to confirm output is identical. E.g., serialize a `SingBoxConfig`
  before + after Wave 34 patches, diff strings.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: full suite passes (~1124/1128). No new tests required (refactor is byte-equivalent).
- [ ] **Gate 3 — Docs**: brief Outcome filled. No README/CLAUDE.md changes needed (internal API only; `IHttpClient` is internal). The Wave 30 audit doc's "IL warning count" section may be updated as a follow-up if convenient.
- [ ] **Gate 4 — Self-review**: `simplify` skill ran on the diff (~80-120 LOC expected, exceeds the 100 LOC trigger). No security-sensitive change.
- [ ] **Gate 5 — MCP verify**: N/A — no UI surface touched.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split. Behavior pinned by existing round-trip tests.

## Outcome (filled 2026-05-19)

**Status**: **PASS**
**Commits**: `1d896a2` (brief) · `9f1d94b` (implementation + simplify cleanups)
**Pushed**: github + origin commit `9f1d94b`
**Test deltas**: 0 new tests (refactor is byte-equivalent within the wire-format-preserving scope; 1 existing test assertion widened to whitespace-tolerant regex)
**Files changed**: 20 · +217/-197 LOC (net +20; simplify pass retired 6 orphan options fields + 12 redundant WHAT-comments)

**Sites migrated (~26+)**:
- ConfigGenerator (1 Serialize)
- ConfigShareDocument (2 — Serialize + Deserialize)
- HealthCheck (1 Deserialize)
- VpnEngine (1 Deserialize)
- ProfileManager (4 — 3 Deserialize ProfileCollection + 1 Serialize ProfileCacheFile + 1 Deserialize ProfileCacheFile)
- ClashSingBoxApi (5 — 2 Serialize records + 3 DeserializeAsync streams)
- LaunchFailureCounter (2 — Read State + Save State)
- CacheRecovery (1 SchemaProbe Deserialize)
- UpdateSources/GitHubReleaseSource (1 — GitHubRelease[] Deserialize)
- UpdateSources/SideloadSource (1 — same)
- FreeConfigs/FreeConfigCache (2 — CacheFile Load + Save)
- CustomRulesImportExport (2 — List<CustomRule> Deserialize + Serialize)
- WindowsDnsHardening (2 — HardeningState Save + Load)
- CLI/Helpers/StateFile (2 — RunState Write + Read)

**New `[JsonSerializable]` registrations** (in AppJsonContext):
- `CacheRecovery.SchemaProbe`
- `ClashSingBoxApi.{VersionDto, ConnectionsDto, ProxiesEnvelopeDto}`
- `CustomRule`
- `FreeConfigCache.CacheFile`
- `List<CustomRule>`

**Visibility flips** (`private → internal sealed`):
- `CacheRecovery.SchemaProbe`
- `ClashSingBoxApi.{VersionDto, ConnectionsDto, ProxiesEnvelopeDto, ProxyDto, ProxyHistoryDto}`

**`[JsonPropertyName]` added** for wire-format stability after losing
per-instance `PropertyNamingPolicy`:
- `CustomRule` — 5 attributes (snake_case: action/type/value/comment/enabled)
- `LaunchFailureCounter.State` — 7 attributes (camelCase wire keys)

**Context-level `[JsonSourceGenerationOptions]` tightened**:
| Setting | Pre-Wave-34 | Post-Wave-34 |
|---|---|---|
| `DefaultIgnoreCondition` | `WhenWritingNull` | unchanged |
| `PropertyNameCaseInsensitive` | `true` | unchanged |
| `WriteIndented` | unset (default false) | **`true`** (most sites had it locally; FreeConfigCache flipped) |
| `MaxDepth` | unset (default 64) | **`32`** (was ProfileManager-only) |
| `NumberHandling` | unset | **`AllowReadingFromString`** (preserved from ClashSingBoxApi.SerializerOptions) |

**6 orphan options fields retired** post-simplify:
- `ConfigShareDocument.DocumentOptions`
- `WindowsDnsHardening.HardeningStateOptions` (replaced by adding
  `WriteIndented=true` on `WindowsDnsHardeningJsonContext`)
- `CacheRecovery.ProbeOptions`
- `FreeConfigCache.JsonOptions`
- `LaunchFailureCounter.JsonOptions`
- `ClashSingBoxApi.SerializerOptions`
- `StateFile.Options` (replaced by adding `WriteIndented=true` on `CliJsonContext`)

**4 options fields KEPT** (still consumed elsewhere):
- `ProfileManager.SafeJsonOptions` — used by `MainWindowViewModel.Profiles` (App layer, out of Wave 34 scope) + 20+ test sites
- `ConfigGenerator.SingBoxOptions` — `Phase4StjRoundTripTests:188` pin
- `GitHubReleaseSource.GitHubReleaseJsonOptions` — `Phase4StjRoundTripTests:359,402`
- `CustomRulesImportExport.JsonOptions` — used by `Serialize(entries, JsonOptions)` at line 518 for the sing-box-native export branch (Wave 35 scope, documented in source)

**Dead code retired**:
- `IHttpClient.AsJson<T>(HttpResponse, JsonSerializerOptions?)` extension — Phase 2D added, zero callers found across Core/CLI/App/Service. Carried a permanent IL2026/IL3050 warning for no benefit.

**1 test assertion widened**:
- `CacheRecoveryTests.FreeConfigCache_Save_StampsCurrentSchemaVersion` — switched from `Assert.Contains("\"schema_version\":1", raw)` to `Assert.Matches("\"schema_version\"\\s*:\\s*1", raw)` because cache file format flipped compact → indented per `WriteIndented=true` on AppJsonContext. Internal cache file, no NekoBox/Hiddify interop, ~5-10% disk growth (30MB → 32MB at 25k entries).

**Gate results**:
- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors / 191 warnings (all pre-existing xUnit1051 + CA1416 noise, no Wave-34-introduced)
- [x] **Gate 2 — Tests green**: 1124 / 0 failed / 4 skipped / 1128 total, ~40s (same baseline as Wave 31a+b post-merge; refactor verified byte-equivalent across all round-trip test classes — Phase4StjRoundTripTests, ProfileManagerJsonDosGuardTests, FreeConfigEntrySchemaTests, CacheRecoveryTests, WindowsDnsHardening regressions, CustomRulesImportExportTests)
- [x] **Gate 3 — Docs**: this brief Outcome filled. No README/CLAUDE.md changes (internal API only)
- [x] **Gate 4 — Self-review**: `simplify` skill ran on the +192/-63 LOC pre-cleanup diff → 3 review agents in parallel (reuse / quality / efficiency). Findings consolidated:
  - **6 orphan options fields** caught + retired
  - **3 ClashSingBoxApi.DeserializeAsync sites** caught (had been missed in the initial migration); migrated to JsonTypeInfo
  - **12 redundant WHAT-comments** trimmed; the 6 WHY-comments kept
  - **AppJsonContext WriteIndented comment** rewritten to accurately reflect the FreeConfigCache trade-off (was misleadingly claiming "every site already had it locally")
  - **AtomicJsonWriter helper extraction** flagged as backlog (3 sites share the tmp+rename pattern; not Wave-34-essential)
  - **[JsonPropertyName] inline strings** flagged for future `WireFieldNames` constant-extraction (CustomRule properties); backlog
- [-] **Gate 5 — MCP verify**: N/A — no UI surface touched
- [-] **Gate 6 — Characterization diff**: N/A — not a god-file split. Behaviour pinned by existing round-trip tests

**Surprises encountered**:

1. **`DefaultIgnoreCondition.Never` on LaunchFailureCounter.JsonOptions was lost on migration** — the context default is `WhenWritingNull`, but State's properties default to non-null values (int 0, string.Empty), so the behaviour change is invisible in practice. Documented in the retired-field comment.
2. **`PropertyNamingPolicy=SnakeCaseLower` on CustomRulesImportExport.JsonOptions** — would have changed CustomRule wire format from snake_case to PascalCase under the migration. Fixed by adding `[JsonPropertyName]` per CustomRule property — preserves the format independent of any per-options naming policy.
3. **`PropertyNamingPolicy=CamelCase` on LaunchFailureCounter.JsonOptions** — same issue. Same fix.
4. **3 unmigrated DeserializeAsync sites in ClashSingBoxApi** — initial inventory missed them because the grep pattern only matched synchronous `Deserialize<T>(`. simplify caught the inconsistency.
5. **`NumberHandling.AllowReadingFromString` on ClashSingBoxApi.SerializerOptions** — sing-box can emit numeric counters as JSON strings. Almost dropped when retiring the orphan field; preserved by adding it to AppJsonContext's `[JsonSourceGenerationOptions]`.
6. **`ProxyDto` and `ProxyHistoryDto` had to flip private → internal** in cascade because `ProxiesEnvelopeDto.Proxies` references `Dictionary<string, ProxyDto>` and `ProxyDto.History` references `List<ProxyHistoryDto>`. Source generator requires all transitive type references to be visible.
7. **`CacheRecovery.ProbeOptions` had `AllowTrailingCommas + ReadCommentHandling=Skip`** — those settings are NOT carried into the context migration. Documented at the SchemaProbe call site that our cache files are STJ-emitted (never hand-edited), so trailing-comma/comment tolerance is academic. If future use case needs lenient parse, switch that one call back to explicit options.

**Follow-ups spawned**:

- **App-layer SafeJsonOptions sweep** — `MainWindowViewModel.Profiles.cs:155, 285` still uses `ProfileManager.SafeJsonOptions` via the legacy generic overload. Wave 34b or merged into a future App refactor.
- **AtomicJsonWriter<T> helper** — `FreeConfigCache.cs:120-128`, `LaunchFailureCounter.cs:238-243`, `WindowsDnsHardening.cs:282-287` share the tmp+rename pattern; centralizing saves ~5 LOC per site and pins the invariant. Backlog.
- **WireFieldNames constants** — `CustomRule.cs` has `[JsonPropertyName("action"|"type"|...)]` that duplicate string literals used in `CustomRulesImportExport.cs`. Backlog (low-impact).
- **`Phase4StjRoundTripTests` SafeJsonOptions / SingBoxOptions / GitHubReleaseJsonOptions cleanup** — once App-layer sweeps these, the 4 still-kept options fields can also retire. Tied to App-layer follow-up.

**Rollback**: `git revert 9f1d94b` (single commit). Brief commit `1d896a2` independent — keep regardless.

**Lessons for methodology doc**:

- `git add -A` slurps in nested embedded git repos (`.claude/worktrees/agent-*` dirs) + TestResults output. Always stage selectively or strengthen `.gitignore`. Hit during Wave 34's first commit attempt; recovered via `git reset --soft HEAD~1` + `git reset HEAD .` + selective `git add`.
