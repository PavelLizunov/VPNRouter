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
*(filled by agent)*

## Follow-up

- Phase 7: when NativeAOT is enabled, register more DTOs as the
  trim/AOT audit uncovers reflective serialization paths.
