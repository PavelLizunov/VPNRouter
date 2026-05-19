# Phase 6 — Android JsonSerializerContext + MEMORY.md refresh

**Owner**: Wave 28 agent
**Roadmap ref**: Phase 5 rollup follow-up #4 + #6
**Effort**: 0.5 day
**Risk**: LOW (additive; mirrors Wave 25 Core pattern)

## Why

Phase 5 Wave 25 wired `JsonSerializerContext` for 13 Core DTOs but
deferred `ServerTestResultDto` (lives in `VPNRouter.Android`, not Core
— sibling context needed). Phase 6 adds the Android-side context so
NativeAOT (Phase 6 Wave 30) covers Android too.

Plus a separate small task: `MEMORY.md` Android section has stale
toolchain versions (says "Phase 0 COMPLETE" with .NET 8 + android-34;
should reflect Phase 5 = .NET 10 + android-36 + Avalonia 12).

## What

### 6-AJ-1: New `VPNRouter.Android/Json/AndroidJsonContext.cs`

Mirror `VPNRouter.Core/Json/AppJsonContext.cs` but for Android-side DTOs:

```csharp
[JsonSerializable(typeof(ServerTestResultDto))]
// Add any Android-only DTOs grep-discovered
internal sealed partial class AndroidJsonContext : JsonSerializerContext { }
```

Wire `TypeInfoResolver = JsonTypeInfoResolver.Combine(
    AndroidJsonContext.Default,
    AppJsonContext.Default,    // Core types still available
    new DefaultJsonTypeInfoResolver())`
into `AndroidStorage.JsonOptions` (replacing the current
`Combine(AppJsonContext.Default, ...)` from Wave 25).

### 6-AJ-2: Grep + register all Android DTOs

```bash
grep -rnE "JsonSerializer\.(Serialize|Deserialize)" VPNRouter.Android --include="*.cs"
```

Each DTO type used in any of those calls needs a `[JsonSerializable]`
entry in `AndroidJsonContext`. Don't miss any (AOT mode pins behavior).

### 6-AJ-3: MEMORY.md Android section refresh

Update `~/.claude/projects/.../memory/MEMORY.md` (or the project-level
section that mirrors it) — the "VM dev environment" + "Android port"
sections need:
- `.NET 10 SDK 10.0.300` (was: 8.0.x)
- `android-36` SDK installed
- `Avalonia 12.0.3` on Android (was: pinned 11.3.12)
- Phase 5 Wave 23 = `ph4-android-net10` DONE
- libbox.aar still gitignored, Phase 6 CI workflow provisioning

## How

**Step 1** — Grep all Android-side JsonSerializer calls to enumerate DTOs.

**Step 2** — Create `VPNRouter.Android/Json/AndroidJsonContext.cs`
with all enumerated types `[JsonSerializable]`-registered.

**Step 3** — Update `AndroidStorage.JsonOptions` to chain
`AndroidJsonContext.Default` first in the `Combine` call.

**Step 4** — Build Android target (`/p:EnableAndroidTarget=true`).
0 errors expected.

**Step 5** — Run scoped suite. STJ round-trip tests should still pass.

**Step 6** — Locate + edit MEMORY.md Android section. Update versions.

## Verification gate

- [ ] `VPNRouter.Android/Json/AndroidJsonContext.cs` created with ≥1 DTO
- [ ] All Android-side JsonSerializer DTOs registered (grep-verified)
- [ ] `AndroidStorage.JsonOptions` wires AndroidJsonContext first
- [ ] Build 0 errors (Android target)
- [ ] Scoped suite green
- [ ] MEMORY.md Android section reflects Phase 5 toolchain
- [ ] Hook gates pass

## Outcome

**Wave 28 status**: COMPLETE — files staged, not committed (integrator commits).

### 6-AJ-1: AndroidJsonContext.cs created + wired

`VPNRouter.Android/Json/AndroidJsonContext.cs` (NEW, ~100 LOC) — `internal sealed partial`
JsonSerializerContext following AppJsonContext.cs structure exactly (alphabetical
`[JsonSerializable]` entries, `#nullable enable`, same `[JsonSourceGenerationOptions]`
mirror — `DefaultIgnoreCondition = WhenWritingNull` + `PropertyNameCaseInsensitive = true`).

Grep walked every Android-side `JsonSerializer.{Serialize|Deserialize}` call (15 hits
total, all in `AndroidStorage.cs`). Five unique DTO surface shapes deferred from
Wave 25 are now registered:

| Type | Why not in Core context |
|---|---|
| `CustomCategory` | Core-resident model; Wave 25 declined to register the List<T> wrapper to keep AOT-pinned surface tight for ProfileManager/ConfigGenerator/etc. |
| `Dictionary<string, AndroidStorage.ServerTestResultDto>` | Android-only nested shape; can't be referenced from Core. |
| `List<CustomCategory>` | List wrapper for the SharedPreferences blob. |
| `List<string>` | Per-app-packages flat list (Android package IDs). |
| `AndroidStorage.ServerTestResultDto` | Android-only inner DTO (status/latency/last-tested/error). |

`AndroidStorage.JsonOptions.TypeInfoResolver` chain is now
`Combine(AndroidJsonContext.Default, AppJsonContext.Default, new DefaultJsonTypeInfoResolver())`
— Android-specific resolved first, Core context second, reflective fallback last.

Source generator confirmed active by `EmitCompilerGeneratedFiles=true` build —
12 `AndroidJsonContext.*.g.cs` files emitted (`Boolean`, `CustomCategory`,
`DateTimeOffset`, `DictionaryStringServerTestResultDto`, `Int32`,
`ListCustomCategory`, `ListString`, `ServerTestResultDto`, `String` + the
framework `g`, `GetJsonTypeInfo`, `PropertyNames` partials).

### 6-AJ-2: MEMORY.md refresh

Updated two memory files for the Phase 5 toolchain bump:

1. `~/.claude/projects/C--Project-VPNRouter/memory/vpnrouter-android-port.md`
   (topic file referenced by MEMORY.md index) — bumped Architecture section:
   `net8.0-android` → `net10.0-android36.0`, Avalonia `11.3.12` → `12.0.3`,
   added Toolchain block (.NET SDK 10.0.300, Microsoft.Android.Sdk.Windows
   36.1.53, env vars). Added a new "Phase 5/6 timeline" section that pins
   Wave 23 (`ph4-android-net10` commit c33e372), Wave 25 (AppJsonContext
   commit d9b0788), Wave 28 (this brief, AndroidJsonContext + MEMORY.md),
   and planned Waves 26 (CI Android workflow with libbox.aar provisioning)
   + 30 (`<PublishAot>true</PublishAot>`).

2. `~/.claude/projects/C--Project-VPNRouter/memory/MEMORY.md` — replaced
   the stale "Phase 0 COMPLETE" line (2026-04-29 net8.0/SDK 34/Avalonia 11.3)
   with a "Phase 5 SHIPPED" line that lists the current Phase 5 toolchain
   (net10/SDK 36/Avalonia 12.0.3) and links forward to Phase 6 Waves 26
   (CI Android) + 28 (this wave). Cross-refs to the topic file + relevant
   plan briefs.

`.claude_handoff.md` (gitignored, at main repo root) not edited — runtime memory
layer is the auto-managed MEMORY.md + topic files (the rule #10 controlled file).

### Verification gate

- [x] `VPNRouter.Android/Json/AndroidJsonContext.cs` created with 5 DTOs registered (≥1 gate)
- [x] All Android-side JsonSerializer DTOs registered (grep-verified — 15 hits enumerated, 5 unique shapes registered)
- [x] `AndroidStorage.JsonOptions` wires AndroidJsonContext first in the Combine chain
- [x] Build 0 errors — C# CoreCompile target on Android csproj passes 0 errors / 93 pre-existing CA1416 warnings (Windows-API-on-Android, unrelated). Java compile fails as expected — libbox.aar still gitignored (Phase 6 Wave 26 follow-up).
- [x] Full solution `dotnet build VPNRouter.sln -c Release` — 0 errors / 192 pre-existing warnings.
- [x] Scoped suite green — 20/20 v2.28.x regression tests pass; 53/53 STJ-related tests pass.
- [x] MEMORY.md Android section reflects Phase 5 toolchain (.NET 10, android-36, Avalonia 12.0.3, Phase 5 Wave 23 DONE, libbox.aar gitignored / Phase 6 CI planned).
- [x] Hook gates pass — no hook errors during staging.

### Staged (not committed)

```
M  VPNRouter.Android/AndroidStorage.cs
A  VPNRouter.Android/Json/AndroidJsonContext.cs
```

Untracked memory file edits (outside repo tree, won't be committed):
- `~/.claude/projects/C--Project-VPNRouter/memory/vpnrouter-android-port.md`
- `~/.claude/projects/C--Project-VPNRouter/memory/MEMORY.md`

### Surprises / notes

1. `CustomCategory` lives in `VPNRouter.Core.Models` (not Android), but Wave 25
   explicitly skipped registering its List<T> wrapper. Wave 28 registers it here
   in AndroidJsonContext because AndroidStorage is the only call site that
   serializes a `List<CustomCategory>` — keeps the Core context tight.
2. `List<string>` (per-app-packages) is a primitive-collection wrapper — STJ
   handles `string` natively, but the wrapper deserves a registered
   JsonTypeInfo<List<string>> so AOT mode doesn't fall through to reflective
   walks for the List<T> shell.
3. Android target full APK build still blocked by `libbox.aar` (gitignored, local
   build only). Phase 6 Wave 26 will provision via CI. C# CoreCompile target
   verifies the AndroidJsonContext + AndroidStorage wiring independently.

## Follow-up

- Phase 7: when NativeAOT is enabled, register more DTOs as the
  trim/AOT audit uncovers reflective serialization paths.
