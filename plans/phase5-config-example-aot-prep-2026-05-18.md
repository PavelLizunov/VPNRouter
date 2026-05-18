# Phase 5 — config.example.yaml REPLACE_ME + JsonSerializerContext AOT prep

**Owner**: Wave 25 agent
**Roadmap ref**: Phase 3D follow-up + Phase 4/5 AOT prep
**Effort**: 1 day
**Risk**: LOW-MEDIUM (config.example trivial; AOT touches DTO serialization)

## Why

Two unrelated Phase 4 follow-ups that fit together as a single wave:

### 5-AOT-1: config.example.yaml REPLACE_ME tokens

Phase 3D Follow-up flagged the root `config.example.yaml` as a UX risk:
users copying it would re-introduce the v2.32.3 placeholder fingerprints
into their own `config.yaml`. Wave 17's CI grep-gate carved out an
exception for this file but it should ideally not need the carve-out.

Replace literal `DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` pubkey +
`78ca7952` short_id + `195.135.255.216` server with `REPLACE_ME_*`
tokens. Then remove the workflow's `config.example.yaml` carve-out.

### 5-AOT-2: JsonSerializerContext source-gen for AOT

Phase 4 retired Newtonsoft.Json (4 csprojs dropped the package). System.Text.Json
is AOT-friendly but only when used with `JsonSerializerContext`-based
source generation. Without it, AOT compilation falls back to reflection
which fails at runtime.

This wave wires `JsonSerializerContext` for the top 5 highest-traffic
DTO families:
- `Profile` + `ProcessRule` + `ProfileCollection` + `ProfileCacheFile`
- `VlessServerEntry` + `SubscriptionEntry`
- `GitHubRelease` + `GitHubAsset` (shared across IUpdateSource impls)
- `ServerTestResultDto`
- `SingBoxConfig` (sing-box wire format)

Win: unblocks Android NativeAOT (4× startup improvement per Avalonia 12
blog). Phase 6 candidate: switch AndroidApp to actual AOT build.

## What

### 5-AOT-1 (small):
- Edit `config.example.yaml` (root of repo):
  - `public_key: DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` → `public_key: REPLACE_ME_REALITY_PUBLIC_KEY`
  - `short_id: 78ca7952...` → `short_id: REPLACE_ME_SHORT_ID`
  - `server: 195.135.255.216` → `server: REPLACE_ME_SERVER_HOST`
- Update the `vless://` example URL to `vless://REPLACE_ME_UUID@REPLACE_ME_SERVER:443?...`
- Add a `# IMPORTANT: replace REPLACE_ME_* tokens before using` comment block at the top
- Remove `config.example.yaml` from the workflow grep-gate allowed-paths

### 5-AOT-2 (larger):
Create `VPNRouter.Core/Json/AppJsonContext.cs`:

```csharp
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ProfileCollection))]
[JsonSerializable(typeof(ProfileCacheFile))]
[JsonSerializable(typeof(ProcessRule))]
[JsonSerializable(typeof(VlessServerEntry))]
[JsonSerializable(typeof(SubscriptionEntry))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(ServerTestResultDto))]
[JsonSerializable(typeof(SingBoxConfig))]
internal partial class AppJsonContext : JsonSerializerContext { }
```

Wire it into the existing `JsonSerializerOptions` instances
(`ProfileManager.SafeJsonOptions`, etc.):

```csharp
TypeInfoResolver = AppJsonContext.Default
```

For DTOs that are only used internal-to-class, mark them with
`[JsonSerializable]` attributes in the context too (e.g.
`GitHubRelease` was made `private` in UpdateChecker — needs to be
`internal` to be visible to AppJsonContext).

## How

**5-AOT-1**:
1. Edit `config.example.yaml` — replace 3 literal values with tokens
2. Edit `.github/workflows/grep-placeholder-fingerprints.yml` — remove
   the `config.example.yaml` carve-out from the allowed-paths regex
3. Verify local grep passes (current HEAD now has no `DnT9hI...` outside
   the 4 originally-allowed paths)
4. Update `plans/phase3-3D-placeholder-defense-consolidation-2026-05-18.md`
   Follow-up to mark this DONE

**5-AOT-2**:
1. Create `VPNRouter.Core/Json/AppJsonContext.cs` with `[JsonSerializable]`
   attributes for the 10 listed DTOs
2. Wire `TypeInfoResolver = AppJsonContext.Default` into existing
   `JsonSerializerOptions` instances
3. Verify build 0 errors (source generator runs at compile time)
4. Run full scoped suite — STJ round-trip tests should pass (the
   reflective DefaultJsonTypeInfoResolver + AppJsonContext are
   composed; either works)
5. AOT-specific test (Phase 6 candidate): run `dotnet publish -c Release
   -r win-x64 -p:PublishAot=true` on a subset — should succeed without
   reflection-warning trimming errors

## Verification gate

- [ ] `config.example.yaml` REPLACE_ME tokens applied
- [ ] CI grep-gate carve-out for `config.example.yaml` removed
- [ ] CI workflow passes on current HEAD (verify local grep)
- [ ] `AppJsonContext.cs` created with 10+ DTO `[JsonSerializable]` attributes
- [ ] `TypeInfoResolver` wired into `SafeJsonOptions` + relevant options
- [ ] Build 0 errors (source generator output compiled)
- [ ] Scoped suite green (no functional regression)
- [ ] Phase4StjRoundTripTests + Phase3StjJsonRoundTripTests still pass
- [ ] Hook gates pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 6: actually enable `<PublishAot>true</PublishAot>` for Android
  release builds (NativeAOT 4× startup win). Requires:
  - `<IsAotCompatible>true</IsAotCompatible>` on Core + Android csprojs
  - Audit + fix every `<TrimmingSuppression>` warning
  - Ensure no `Activator.CreateInstance` calls + no reflection on
    non-context-registered types
- Phase 6: extend AppJsonContext to all remaining DTOs as broader DTO
  audit completes.
