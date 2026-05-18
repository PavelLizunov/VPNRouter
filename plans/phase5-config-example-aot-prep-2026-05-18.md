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

**Done** (Wave 25 agent, 2026-05-18).

### Files staged

Grouped by sub-task for atomic commits (integrator can split if desired):

**5-AOT-1 — config.example.yaml REPLACE_ME + CI grep-gate carve-out removal** (3 files):

- `config.example.yaml` (+11/-4 LOC) — three literal v2.32.3 placeholder
  values swapped for `REPLACE_ME_*` tokens. `server: 194.87.222.111` →
  `REPLACE_ME_SERVER_HOST`, `uuid: 2d54442d-...` → `REPLACE_ME_UUID`,
  `public_key: DnT9hI...` → `REPLACE_ME_REALITY_PUBLIC_KEY`,
  `short_id: 78ca7952` → `REPLACE_ME_SHORT_ID`. Top-of-file IMPORTANT
  comment added explaining the placeholder defense will reject any
  verbatim copy. (The brief mentioned a `vless://` URL example — none
  exists in `config.example.yaml`, so nothing to update there.)
- `.github/workflows/grep-placeholder-fingerprints.yml` (+7/-5 LOC) —
  `config.example.yaml` removed from both the human-readable allow-list
  comment block and the `ALLOWED` extended-regex. A note added to the
  comment block explaining the Wave 25 cleanup. Workflow trigger /
  job logic unchanged.
- `plans/phase3-3D-placeholder-defense-consolidation-2026-05-18.md` (+1/-1) —
  the Follow-up bullet that called for this exact cleanup is now
  struck-through with a DONE marker pointing back at this brief.

**5-AOT-2 — JsonSerializerContext source-gen for AOT** (7 files):

- `VPNRouter.Core/Json/AppJsonContext.cs` *(new, 107 LOC)* — the
  source-generator-driven context registering 13
  `[JsonSerializable]` entries (≥10 verification gate target):
  `ConfigShareDocument`, `GitHubAsset`, `GitHubRelease`,
  `GitHubRelease[]`, `List<SubscriptionEntry>`,
  `List<VlessServerEntry>`, `ProcessRule`, `Profile`,
  `ProfileCacheFile`, `ProfileCollection`, `SingBoxConfig`,
  `SubscriptionEntry`, `VlessServerEntry`. Alphabetical order
  (deterministic build output). `#nullable enable`,
  `internal sealed partial`. Source-gen options on the context match
  the union of hot-path JsonSerializerOptions instances
  (PropertyNameCaseInsensitive + WhenWritingNull).
- `VPNRouter.Core/Services/ProfileManager.cs` (+5/-0 LOC) — wired
  `SafeJsonOptions.TypeInfoResolver` to
  `JsonTypeInfoResolver.Combine(AppJsonContext.Default,
  new DefaultJsonTypeInfoResolver())`.
- `VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs`
  (+10/-0 LOC) — same composition wire for `GitHubReleaseJsonOptions`.
- `VPNRouter.Core/Services/ConfigGenerator.cs` (+9/-0 LOC) — same for
  `SingBoxOptions` (covers the sing-box wire-format generator + the
  Phase4StjRoundTripTests SingBoxConfig round-trips).
- `VPNRouter.Core/Services/ConfigShareDocument.cs` (+10/-0 LOC) — same
  for `DocumentOptions`. Sub-types (ExportedFromInfo / ExportedSettings /
  PerAppFilterExport) ride the recursive reachability of the
  generator-emitted `ConfigShareDocument` resolver.
- `VPNRouter.Core/Services/UpdateChecker.cs` (+10/-0 LOC) — explanatory
  comment added above `GitHubApiJsonOptions` noting why this options
  instance is intentionally NOT wired (its nested private
  `GitHubRelease`/`GitHubAsset` types collide with the
  UpdateSources-namespace internal types AppJsonContext registers;
  Phase 6 retires the legacy CheckForUpdateAsync path with these
  private DTOs).
- `VPNRouter.Android/AndroidStorage.cs` (+15/-0 LOC) — wired
  `JsonOptions.TypeInfoResolver` same way. SubscriptionEntry +
  VlessServerEntry + their List<T> wrappers route through the source-gen
  resolver; CustomCategory + ServerTestResultDto (Android-only DTOs not
  registered in Core's AppJsonContext) fall through to the reflective
  resolver. Phase 6 candidate: sibling Android-side `AndroidJsonContext`
  for the Android-only DTOs.

### LOC delta

```
 .github/workflows/grep-placeholder-fingerprints.yml         | 12 +++++++-----
 VPNRouter.Android/AndroidStorage.cs                         | 17 +++++++++++++++++
 VPNRouter.Core/Json/AppJsonContext.cs                       | 107 +++++++++++++++++++ (new)
 VPNRouter.Core/Services/ConfigGenerator.cs                  | 11 +++++++++++
 VPNRouter.Core/Services/ConfigShareDocument.cs              | 13 +++++++++++++
 VPNRouter.Core/Services/ProfileManager.cs                   | 14 ++++++++++++++
 VPNRouter.Core/Services/UpdateChecker.cs                    | 12 ++++++++++++
 VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs| 12 ++++++++++++
 config.example.yaml                                         | 15 +++++++++++----
 plans/phase3-3D-placeholder-defense-consolidation-2026-05-18.md |  2 +-
 10 files changed, 205 insertions(+), 10 deletions(-)
```

Net repo LOC: +195 (the bulk is the new AppJsonContext.cs file + its
deliberate documentation block).

### Build + scoped suite results

- `dotnet build VPNRouter.sln -c Release` — **0 errors**, 191
  pre-existing warnings (xUnit1051 CancellationToken hints + CA1416
  platform-guard hints unchanged from HEAD).
- Source generator emit verified via
  `dotnet build VPNRouter.Core/VPNRouter.Core.csproj -c Release \
   -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=GenTmp`:
  emits ~60+ `AppJsonContext.{Type}.g.cs` files under
  `System.Text.Json.SourceGeneration.JsonSourceGenerator/` covering
  every registered root + their recursively reachable nested DTOs
  (ConfigShareDocument, DnsRule, DnsServer, ExportedFromInfo,
  ExportedSettings, GitHubAsset, GitHubRelease, Hysteria2Obfs,
  ListDnsRule, ListVlessServerEntry, etc.). (GenTmp directory was
  cleaned after verification — not staged.)
- Phase3StjJsonRoundTripTests + Phase4StjRoundTripTests:
  **31/31 passed**, 0 failed, 0 skipped.
- Brief-named regression suite
  (VlessServersResolverTests + ConfigGeneratorEmptyServersGuardTests +
  FreeConfigAggregatorPreserveTests): **20/20 passed**.
- Sing-box check integration tests (`~SingBoxCheck|~PassesSingBoxCheck`):
  **3/3 passed**.
- Full scoped suite
  (`!~Headless&!~PageScreenshot&!~VisualDiff`): **1121 passed, 4
  pre-existing skips (AndroidApp dump fact, multi-server config,
  autostart contract on Linux runner), 0 failed**.

### Verification gate checkboxes

- [x] `config.example.yaml` REPLACE_ME tokens applied (server / uuid /
      public_key / short_id; plus top-of-file IMPORTANT comment).
- [x] CI grep-gate carve-out for `config.example.yaml` removed
      (allow-list shrunk from 5 entries to 4).
- [x] CI workflow passes on current HEAD (local grep simulation:
      every `DnT9hI...` / `78ca7952` / `195.135.255.216` hit stays
      within the new allow-list — PlaceholderDefense.cs /
      VPNRouter.Tests/*.cs / plans/*.{md,yaml,yml,json} /
      .github/workflows/*.yml). config.example.yaml has zero
      placeholder fingerprints.
- [x] `AppJsonContext.cs` created with 13
      `[JsonSerializable]` attributes — exceeds "10+" gate target.
- [x] `TypeInfoResolver` wired into 5 production
      `JsonSerializerOptions` instances
      (ProfileManager.SafeJsonOptions, ConfigGenerator.SingBoxOptions,
      ConfigShareDocument.DocumentOptions,
      GitHubReleaseSource.GitHubReleaseJsonOptions,
      AndroidStorage.JsonOptions). UpdateChecker.GitHubApiJsonOptions
      explicitly left wired only to the reflective fallback per the
      Phase-6-retirement comment.
- [x] Build 0 errors (source generator output compiled).
- [x] Scoped suite green (1121 passed, 4 pre-existing skips, 0 fails).
- [x] Phase4StjRoundTripTests + Phase3StjJsonRoundTripTests still pass
      (31/31).
- [ ] Hook gates — integrator runs (worktree, not committing here).

### Surprises / notes

1. **`ServerTestResultDto` — assembly-direction blocker**. The brief
   listed it as one of the 10 candidates. It's a nested class in
   `VPNRouter.Android.AndroidStorage` (Android assembly references
   Core, not vice-versa), so Core's AppJsonContext cannot reference it
   without creating a cyclic dependency. Substituted
   `ConfigShareDocument` in its place — also high-traffic on both
   Android (Bug-AND-023 QR scan flow) and desktop (export/import) and
   properly Core-resident. Plus added the `List<SubscriptionEntry>` /
   `List<VlessServerEntry>` shapes the Android SharedPreferences
   read/write paths use, bringing the registered surface to 13
   entries. Phase 6 follow-up: sibling Android-side context for the
   Android-only DTOs (ServerTestResultDto, CustomCategory).

2. **`UpdateChecker.GitHubApiJsonOptions` name collision**. The
   private nested `GitHubRelease` / `GitHubAsset` at the bottom of
   `UpdateChecker.cs` are distinct from the `internal sealed` ones in
   `UpdateSources/GitHubReleaseSource.cs` that AppJsonContext
   registers. Bumping the private types to `internal` would create a
   duplicate-name clash with the UpdateSources versions. The brief's
   "may need to bump visibility" note was conditional, and pursuing it
   would have added breakage for net-zero AOT win (the legacy
   `CheckForUpdateAsync` path is Phase-6 retirement candidate — the
   `[Obsolete]` attribute on it already points at the
   `IUpdateSource.CheckAsync` replacement). Decision: leave the
   private types alone, add an explanatory comment above
   `GitHubApiJsonOptions` documenting why this single options instance
   stays on the reflective fallback. The new
   `GitHubReleaseSource.GitHubReleaseJsonOptions` IS wired and is the
   path every Phase 4 caller (UpdateNotificationViewModel desktop
   toast + TestUpdateCommand CI + Android AndroidUpdater) goes
   through — so the AOT improvement covers the production path.

3. **`EmitCompilerGeneratedFiles` clean-build conflict**. When first
   testing with `-p:EmitCompilerGeneratedFiles=true`, a clean build
   produced 426 errors of the "Type already defines a member" class.
   Diagnosis: the same `.g.cs` files end up included in the
   compilation TWICE — once via the in-memory generator output, once
   via the on-disk emit. Workaround: only use the flag for one-shot
   verification, never include it in the production build path.
   Cleaned the GenTmp directory after verification; nothing emitted-
   to-disk is staged.

4. **Pre-existing test skips unchanged**. The full scoped suite
   showed 4 skips — identical set as the Phase 3D outcome report:
   `AndroidAppDumpMembersFact` (Android-target reflection), two
   `MultiServer_*` ConfigGenerator tests (multi-server config not yet
   wired), one autostart contract test (Linux runner only). No new
   skips introduced by this wave.

5. **`internal sealed partial` on AppJsonContext**. C# source
   generators require the user-side declaration to be `partial` so the
   generator can emit the rest of the type. `sealed` is permitted (and
   required for safety — the generator emits direct method bodies that
   wouldn't make sense in a derived class). `internal` matches the
   internal-by-default posture of every other type in Core's
   `VPNRouter.Core.Json` namespace; the consumer surface
   (`AppJsonContext.Default`) is what callers reach, and it's a
   generator-emitted static property the public access model handles
   correctly via `InternalsVisibleTo` to VPNRouter.Tests.


## Follow-up

- Phase 6: actually enable `<PublishAot>true</PublishAot>` for Android
  release builds (NativeAOT 4× startup win). Requires:
  - `<IsAotCompatible>true</IsAotCompatible>` on Core + Android csprojs
  - Audit + fix every `<TrimmingSuppression>` warning
  - Ensure no `Activator.CreateInstance` calls + no reflection on
    non-context-registered types
- Phase 6: extend AppJsonContext to all remaining DTOs as broader DTO
  audit completes.
