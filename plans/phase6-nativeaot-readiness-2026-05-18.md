# Phase 6 — Wave 30 NativeAOT readiness audit + first publish attempt

**Authored**: 2026-05-18
**Phase**: 6 of v3.0 refactor (`plans/v3.0-refactor-roadmap.md`)
**Scope**: VPNRouter.CLI only. App + Service + Android are out of scope.
**Risk**: Medium (research/audit work, no behaviour change).
**Predecessors**: Wave 25 (`AppJsonContext`), Wave 27 (SettingsLoader internal),
Wave 28 (`AndroidJsonContext`).

---

## Why

Phase 4-5 retired Newtonsoft.Json fully and added the System.Text.Json source
generator (`AppJsonContext`) to make the wire-format AOT-safe. The natural
next step is **proving** that VPNRouter.CLI can `PublishAot=true` end to end:
this unlocks much smaller binaries, faster cold-start, and validates the
Phase 4-5 groundwork on a real build.

CLI is the easiest of the three targets (CLI / Service / App) because:

- It has the **smallest surface** (Spectre.Console + Serilog + Core).
- It does NOT depend on Avalonia, WindowsServiceHost, or WPF code paths.
- It already uses System.Text.Json source-gen on all of its hot-paths.

If CLI proves AOT-clean (or surfaces a tractable blocker list), Service is
the next domino — they share the Core dependency tree, so the same audit
applies.

## What

Three deliverables:

1. **Audit** the CLI dependency tree for AOT-incompatible code paths:
   - YamlDotNet reflective deserialisation
   - Spectre.Console.Cli reflection-based command registration
   - Any `Activator.CreateInstance(Type)`, `MakeGenericType`, `MakeGenericMethod`,
     `MethodInfo.Invoke`, `dynamic` keyword in CLI/Core dep tree
   - `JsonSerializer` call sites that bypass the source-gen context
2. **Attempt** `dotnet publish VPNRouter.CLI/VPNRouter.CLI.csproj -c Release
   -r win-x64 -p:PublishAot=true` and capture the trim/AOT analyzer warnings.
3. **Report** blockers in priority order, with concrete fix strategies.

## How

Step 1 — grep audit (already mostly verified during Phase 5 brief
groundwork). Confirm zero hits for the high-risk patterns.

Step 2 — add the AOT publish properties to a side branch / temp build script
(or pass them in the publish command). Do NOT modify `VPNRouter.CLI.csproj`
in the worktree if the attempt fails — keep the csproj clean for the regular
release flow. If it succeeds, leave a `<IsAotCompatible>true</IsAotCompatible>`
marker as a placeholder for the next agent to flip on.

Step 3 — write findings to this brief's Outcome section.

## Acceptance

- [ ] Brief exists with Outcome section filled in
- [ ] `dotnet build VPNRouter.sln -c Release` still 0 errors
- [ ] `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj` regression bar passes
- [ ] Any small AOT-friendly fixes documented in Outcome
- [ ] `plans/phase6-nativeaot-publish-attempt.log` captured (gitignored via `*.log`)
- [ ] Wave 31 plan sketch included so the next agent can pick up

## Verification gate

Per `plans/v3.0-execution-methodology.md`:

1. Build green — `dotnet build VPNRouter.sln -c Release`
2. Tests green — regression filter
3. Test build green — `dotnet build VPNRouter.Tests -c Release`
4. Code review pass — no public API churn, no new dependencies
5. Roadmap entry updated — Phase 6 NativeAOT row gets a "research" check
6. Outcome filled in below

---

## Outcome

### Tooling state

- .NET SDKs installed: 8.0.419 + 10.0.300
- No `global.json` → publish uses 10.0.300 by default, but actual
  AOT compilation uses `Microsoft.DotNet.ILCompiler` 8.0.27 (matching
  the project's `net8.0-windows` target framework).
- CLI csproj targets `net8.0-windows`. The .NET 8 ILC works fine for
  this target.

### Publish attempt — what actually happened

Command run (from worktree root):

```
dotnet publish VPNRouter.CLI/VPNRouter.CLI.csproj -c Release \
  -r win-x64 -p:PublishAot=true --self-contained true
```

Full transcript in `plans/phase6-nativeaot-publish-attempt.log`
(166 lines, gitignored via `*.log` rule).

**Pipeline progress**:

1. Restore — succeeded; NuGet pulled `Microsoft.DotNet.ILCompiler 8.0.27`
   + `runtime.win-x64.Microsoft.DotNet.ILCompiler 8.0.27`.
2. CSC compile — succeeded for Core + Service + CLI; produced `.dll` for
   each.
3. **AOT analyser pass** — emitted **120 IL warnings total** (59 IL2026
   trim warnings + 61 IL3050 dynamic-code warnings). Zero ILxxxx
   *errors*. Distribution by file:

| File | IL warnings | Pattern |
|---|---:|---|
| `Services/CustomConfigInjector.cs` | 24 | `JsonArray.Add<T>(T)` with non-primitive `JsonNode` instances |
| `Services/VlessDeepVerifier.cs` | 16 | Same |
| `FreeConfigs/FreeConfigDeepVerifier.cs` | 12 | Same |
| `Services/ProfileManager.cs` | 10 | `JsonSerializer.Deserialize<T>(string, options)` — `options` HAS the AppJsonContext, but the generic-method-without-typeinfo overload is flagged |
| `Services/ClashSingBoxApi.cs` | 10 | `JsonSerializer.Serialize(new { … })` anonymous types + `DeserializeAsync<T>(Stream, options)` |
| `Services/CustomRulesImportExport.cs` | 6 | Same as ProfileManager + one duplicate-options-inline call |
| `Services/ConfigShareDocument.cs` | 6 | Same |
| `UpdateSources/GitHubReleaseSource.cs` | 4 | Same |
| `Services/WindowsDnsHardening.cs` | 4 | `JsonSerializer.Serialize/Deserialize<HardeningState>` — options instance has no resolver wired |
| `Services/LaunchFailureCounter.cs` | 4 | Same — `State` private class, no resolver |
| `Services/ConfigGenerator.cs` | 4 | Same as ProfileManager |
| `Helpers/StateFile.cs` | 4 | CLI-side `RunState` — options has no resolver |
| `FreeConfigs/FreeConfigCache.cs` | 4 | Same |
| `UpdateSources/SideloadSource.cs` | 2 | Same |
| `Services/VpnEngine.cs` | 2 | Same |
| `Services/SettingsLoader.cs` | 2 | **YamlDotNet** `DeserializerBuilder/SerializerBuilder` ctors |
| `Services/IHttpClient.cs` | 2 | Generic `Deserialize<T>(byte[], options)` helper |
| `Services/HealthCheck.cs` | 2 | Same |
| `Services/CacheRecovery.cs` | 2 | Same |

4. **ILC link step** — failed with a single error, **before any
   ILxxxx error could surface**:

```
error : Platform linker not found. Ensure you have all the required
prerequisites documented at https://aka.ms/nativeaot-prerequisites,
in particular the Desktop Development for C++ workload in Visual Studio.
For ARM64 development also install C++ ARM64 build tools.
```

The native-code linker (link.exe from MSVC build tools) is **not
installed on this build host**. ILC produced the `.obj` file and would
have linked it into a `.exe`, but stops here.

Exit code: non-zero. **No `vpnrouter.exe` native binary produced — but
not because of any actual code-level AOT issue.** The compile + AOT
analyser passes both completed without errors.

### Audit findings (corrected from pre-attempt predictions)

#### Surprise 1: YamlDotNet 15.1.2 — vendor HAS a static analyzer

Pre-attempt prediction: "no source generator, full blocker, requires
hand-rolled YAML reader."

**Actual finding**: the IL3050 warning message from YamlDotNet itself
spells out the answer:

> Using member 'YamlDotNet.Serialization.DeserializerBuilder.DeserializerBuilder()'
> which has 'RequiresDynamicCodeAttribute' can break functionality when
> AOT compiling. This builder configures the deserializer to use
> reflection which is not compatible with ahead-of-time compilation or
> assembly trimming. **You need to use the code generator/analyzer to
> generate static code and use the 'StaticDeserializerBuilder' object
> instead of this one.**

YamlDotNet 15.x ships an analyzer package
(`YamlDotNet.Analyzers.StaticGenerator`) that emits a
`YamlStaticContext` partial class — the YAML equivalent of
`JsonSerializerContext`. Wave 31 work then becomes a 2-line change at
each `SettingsLoader` site:

```csharp
var deserializer = new StaticDeserializerBuilder(new YamlStaticContext())
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
```

Plus adding the analyzer package + a `partial class YamlStaticContext`
declaration with `[YamlSerializable(typeof(AppSettings))]` attributes
(mirroring the AppJsonContext pattern). Total Wave 31 effort:
**~4 hours**, not 3 days.

This pivot completely changes the time estimate below.

#### Surprise 2: Spectre.Console.Cli 0.49.1 — zero IL warnings

Pre-attempt prediction: "reflective command activation, full blocker."

**Actual finding**: the AOT analyzer emitted **zero warnings against
Spectre.Console.Cli**. Two possible explanations:

1. Spectre.Console.Cli's assembly doesn't set `<IsTrimmable>true</IsTrimmable>`,
   so the trimmer preserves the entire assembly. Reflection inside a
   non-trimmed assembly still works at runtime — at the cost of a
   ~100-200 KB binary-size hit.
2. Spectre.Console.Cli authors may have annotated their public surface
   with `[RequiresUnreferencedCode]` already, but the call-site
   warnings would then appear on `config.AddCommand<T>` — and they
   don't.

Either way: **AOT compile passes without flagging Spectre.Console.Cli
at all.** Whether it runs correctly at runtime is a separate
question — would need a successful link + smoke test to verify, which
this attempt couldn't reach.

Wave 31 risk: even though the compile is clean, the first AOT runtime
hit may surface as a `MissingMetadataException` when Spectre tries to
`Activator.CreateInstance(typeof(StartCommand))`. If it does, options
are:

a. Wait for Spectre.Console.Cli 0.50.x with explicit AOT support.
b. Switch to System.CommandLine.
c. Pre-register each command type in a DI-style command factory that
   uses constructor calls instead of reflection (would need API
   support from Spectre).

**Verdict**: defer — try the AOT build on a working build host first;
if Spectre runs, we're done; if it crashes, deal with it then.

#### Confirmed: 4 small `JsonSerializer` call-site cleanups for Wave 31

(Same as pre-attempt prediction):

1. `WindowsDnsHardening.cs:265-269` — `HardeningStateOptions` lacks
   `TypeInfoResolver`. Register `HardeningState` in AppJsonContext
   (will need to flip `private` → `internal` on `HardeningState` and
   `HardeningServerEntry` records on lines 298-326).
2. `LaunchFailureCounter.cs:234-239` — `JsonOptions` lacks
   `TypeInfoResolver`. Register `State` in AppJsonContext (same
   visibility flip needed for `private class State`).
3. `ClashSingBoxApi.cs:132 + 276` — `JsonSerializer.Serialize(new { … })`
   anonymous types. Hoist to named records:
   `internal sealed record ClashSetConfigDto(string Path);`
   `internal sealed record ClashSelectProxyDto(string Name);`
   Register in AppJsonContext.
4. `CustomRulesImportExport.cs:474` — duplicate `new JsonSerializerOptions
   { WriteIndented = true }` instead of reusing the file's `JsonOptions`
   field. Cleanup: replace inline options with `JsonOptions` field
   reference (NB: `JsonOptions` has `PropertyNamingPolicy =
   SnakeCaseLower`; verify export wire-format is identical OR add a
   sibling `ExportOptions` field without the naming policy).
5. `Helpers/StateFile.cs:55-59` (CLI) — `StateFile.Options` lacks
   `TypeInfoResolver`. Register `RunState` in AppJsonContext (already
   `public class` — direct add, no visibility change needed).

Each is 5-15 LOC. Total for these 5: **~1 hour** including tests.

#### Confirmed: `JsonArray.Add<T>(T)` mass-replacement (52 warnings, 3 files)

Pattern in `CustomConfigInjector` (24 hits), `VlessDeepVerifier` (16),
`FreeConfigDeepVerifier` (12). All are calling
`JsonArray.Add(string)` or `JsonArray.Add(JsonObject)` where the
non-generic overload `JsonArray.Add(JsonNode?)` works without trim
warnings. Mechanical fix:

```csharp
// before
childTagsArray.Add(value);

// after
childTagsArray.Add((JsonNode?)JsonValue.Create(value));
```

Or simpler, use the non-generic indexer:

```csharp
childTagsArray.Add(JsonValue.Create(value));
```

(`JsonValue.Create(string)` returns `JsonValue?` which the
`Add(JsonNode?)` overload accepts.)

**Wave 31 effort**: ~30 minutes including regression tests. Each
warning-line is independently fixable, no semantic change.

#### Other audit hits (informational, mostly clean)

- **No `Activator.CreateInstance(Type)`** in CLI / Core dep tree.
- **No `MakeGenericType` / `MakeGenericMethod`** anywhere.
- **No `Reflection.Emit`** anywhere.
- **No `dynamic` keyword** in CLI / Core.
- **No `Type.GetType(string)`** in CLI / Core.
- **Serilog 3.1.1** — emitted zero IL warnings on our static
  `WriteTo.Console() + WriteTo.File()` wiring. Good.
- **Microsoft.Extensions.Hosting** (Service) — emitted zero IL
  warnings either. The reflection-heavy pieces (config binding,
  options pattern) we don't use — Service uses `BackgroundService`
  directly which is AOT-clean.

### Small fixes applied in Wave 30

**None.** Per the brief's "small + obvious + safe" criteria, all 5 of
the easy-fix candidates above require either (a) visibility flips on
existing private types, (b) hoisting anonymous-type sites to named
records (which is a small but non-trivial design decision), or (c)
mechanical 50+ line search-and-replace work on `JsonArray.Add<T>`.

None of those is "fix the broken JsonSerializer call" — they're all
small refactors that deserve dedicated reviewable commits in Wave 31.
I left the codebase pristine so Wave 31 can pick them up cleanly with
the full diff visible.

### Build host requirements (operational note for Wave 31)

To complete an AOT link, the build host needs:

- Visual Studio 2022 Build Tools (or Visual Studio 2022) with the
  **"Desktop development with C++"** workload installed. This provides
  `link.exe` + the Windows SDK headers/libs that ILC links against.
- ARM64 native-AOT additionally requires the "MSVC v143 ARM64 build
  tools" individual component.

The current VM has neither. Adding it is a one-time setup step (about
6 GB download / 8 GB install). Could be automated via
`vs_buildtools.exe --install --quiet --norestart --add
Microsoft.VisualStudio.Workload.VCTools` in a CI setup script.

**Until the build host has the workload, the AOT publish will always
fail with `error : Platform linker not found`.** This is environmental,
not a codebase issue.

### Recommendation for Wave 31

Wave 31 timeline pivots significantly downward from my pre-attempt
estimate because YamlDotNet has a working analyzer.

**Wave 31 plan (~1 day total)**:

1. **Wave 31a (~30 min)** — install MSVC C++ build tools on the
   build host (or add CI provisioning).
2. **Wave 31b (~4 hours)** — add `YamlDotNet.Analyzers.StaticGenerator`
   NuGet, declare a `partial class YamlStaticContext` with
   `[YamlSerializable(typeof(AppSettings))]` + nested types, swap
   `DeserializerBuilder` → `StaticDeserializerBuilder` in
   `SettingsLoader.LoadCore` + `SettingsLoader.Save`. Add 2-3 round-trip
   tests pinning wire format.
3. **Wave 31c (~1 hour)** — apply the 5 small JsonSerializer cleanups
   (HardeningState, LaunchFailureCounter.State, ClashSingBoxApi
   anonymous records, CustomRulesImportExport duplicate options,
   StateFile.Options resolver wiring).
4. **Wave 31d (~30 min)** — mass-replace `JsonArray.Add<T>` with
   `JsonNode`-typed overload (52 hits, 3 files).
5. **Wave 31e (~1 hour)** — re-run the publish, verify ILC completes
   AND the resulting `vpnrouter.exe` works on a clean Windows machine.
   Test all CLI commands (`start --dry-run`, `stop`, `status`,
   `profiles list`, `doctor`).
6. **Wave 31f (~1 hour)** — if Spectre.Console.Cli fails at runtime
   (reflective `Activator.CreateInstance` of command types), pivot to
   System.CommandLine. **If it works** (likely given zero analyzer
   warnings), flip `<PublishAot>true</PublishAot>` in CLI csproj and
   add a `cli-aot` CI smoke job.

### Time estimate for full NativeAOT delivery

| Project | Estimate | Notes |
|---|---:|---|
| VPNRouter.CLI | **~1 day** (Wave 31 above) | YamlDotNet has analyzer; Spectre may already work; only easy cleanups remain |
| VPNRouter.Service | **~2-4 hours** | Shares Core surface with CLI; once CLI is AOT-clean, Service mostly inherits it. Adds `Microsoft.Extensions.Hosting.WindowsServices` reflective probe — needs validation but emitted no warnings. |
| VPNRouter.App (Avalonia) | **multi-week, v4.0** | Avalonia 12 has experimental AOT but axaml binding inference and DataTemplate selectors use reflection extensively. Out of scope here. |

### Wave 30 commit summary

Two commits planned for this worktree:

1. `docs(plan)`: this brief (this commit, hash to fill in below).
2. `docs(plan)`: same brief with this Outcome section populated +
   the publish-attempt log file (next commit, hash to fill in below).

Final commit hashes on `worktree-agent-ad93d86d841b57ca1` branch:

- `00fb214` — initial brief (pre-Outcome).
- `397682a` — Outcome section populated + publish-attempt log.

No `VPNRouter.*` source files were modified.
