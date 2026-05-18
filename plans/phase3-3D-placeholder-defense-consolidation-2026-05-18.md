# Phase 3 — 3D: F-A..F-E PlaceholderDefense consolidation

**Owner**: Wave 10 parallel agent (3 of 4)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3D
**Effort**: 1 week
**Risk**: MEDIUM (security-critical — placeholder credential defense touches every config path)

## Why

Phase 2 added a 6th defense layer (Wave 7c-1 VlessDeepVerifier) on top of the v2.32.3 5-layer defense (F-A..F-E). The 5 layers + the new 6th share placeholder fingerprint sets via `PlaceholderGuard`, but the layer logic is scattered across 8 files:

- F-A: `VlessServersResolver.Resolve` scope guard
- F-B: Migrator strip path (`SettingsMigrator`)
- F-C: UI badge (App layer)
- F-D: `LeakProtection` scope-aware validation
- F-E: Runtime sanity check / auto-failover (`ConfigSanityCheck`)
- 6th (new): `VlessDeepVerifier.VerifyAsync` fail-fast

This drift surface caused the v2.32.3 ship — the Core list was up-to-date but the Android list lagged. Consolidating into a single `VPNRouter.Core/Services/PlaceholderDefense.cs` with internal sub-classes per layer + a single shared `PlaceholderFingerprint[]` array kills the drift class permanently.

## What

Create `VPNRouter.Core/Services/PlaceholderDefense.cs`:

```csharp
public static class PlaceholderDefense
{
    /// Single source of truth for placeholder fingerprints.
    public static IReadOnlyList<PlaceholderFingerprint> KnownFingerprints { get; }

    /// Inspect a VlessServerEntry for placeholder credentials (used by every layer).
    public static PlaceholderInspectionResult Inspect(VlessServerEntry s);

    // Internal sub-classes for each layer (sealed, internal)
    internal static class LayerA_ResolverScopeGuard { ... }
    internal static class LayerB_MigratorStrip { ... }
    internal static class LayerD_LeakValidation { ... }
    internal static class LayerE_RuntimeSanity { ... }
    internal static class Layer6_DeepVerifyFailFast { ... }
}
```

Layer C (UI badge) stays in App layer but reads `PlaceholderDefense.Inspect(...)`.

Migrate the existing 65 tests under `VPNRouter.Tests/PlaceholderGuardTests.cs` (and any layer-specific tests) to call the new namespace. Renamed references only — test logic unchanged.

## How

**Step 1** — Catalog all current placeholder-defense touches:
```bash
grep -nrE "PlaceholderGuard|DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU" VPNRouter.Core VPNRouter.App VPNRouter.Android --include="*.cs"
```

**Step 2** — Build new `PlaceholderDefense.cs` with the consolidated fingerprint list. Add Android-specific fingerprints if the Android Core's list has anything Core doesn't (check `VPNRouter.Android/`).

**Step 3** — Replace `PlaceholderGuard.Inspect` call sites with `PlaceholderDefense.Inspect`. Keep `PlaceholderGuard` as a thin forwarder (one-line `=> PlaceholderDefense.X(...)`) for back-compat.

**Step 4** — Move each F-A..F-E + 6th layer's logic into the new internal sub-class. The call site (VlessServersResolver, etc.) stays put but now invokes `PlaceholderDefense.LayerA_ResolverScopeGuard.Run(...)`.

**Step 5** — Verify all 65 existing tests + the 4 v2.32.3 placeholder tests + the 4 Wave 7c-1 v2.32.3-6th-layer tests still pass.

**Step 6** — Drift verification: ensure the consolidated fingerprint list is the ONLY definition of the placeholder pubkey (`DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU`). No hardcoded copies elsewhere.

## Verification gate
- [ ] PlaceholderDefense.cs created (~400 LOC, 6 internal sub-classes + shared fingerprint list)
- [ ] PlaceholderGuard kept as forwarder (one-line `=>`)
- [ ] No hardcoded `DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` outside PlaceholderDefense (grep verifies)
- [ ] All 65+ existing placeholder tests pass
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite green
- [ ] **Gate 4 security-review**: critical — placeholder defense is the LAST LINE against the Z:\kanareik incident class. Verify no layer accidentally weakened during consolidation.
- [ ] **Hook gates** pass

## Outcome

**Done** (Wave 10 agent #3, 2026-05-18).

### Files staged (6)

- **`VPNRouter.Core/Services/PlaceholderDefense.cs`** *(new, 528 LOC)* — consolidated single-source-of-truth.
  Contents:
  - `PlaceholderFingerprint` record (per-fingerprint provenance, pubkey/short_id/server fields).
  - `PlaceholderDefense` static class with `KnownFingerprints` (IReadOnlyList) + derived `KnownPubkeys`/`KnownShortIds`/`KnownServers` (back-compat projections).
  - Public Inspect API: `Inspect(VlessServerEntry?)`, `Inspect(string?, string?, string?)`, `IsPlaceholder(...)`, `InspectUri(...)`.
  - Five internal `sealed`-equivalent `internal static class` sub-classes per layer:
    - `LayerA_ResolverScopeGuard.IsPlaceholderEntry` (F-A — VlessServersResolver scope guard).
    - `LayerB_MigratorStrip.TruncateForLog` (F-B — SettingsMigrator log helper).
    - `LayerD_LeakValidation.IsPlaceholderEntry` (F-D — LeakProtection union filter, currently mirrors LayerA but kept separate so future divergence is structural).
    - `LayerE_RuntimeSanity.FindFirstProxyOutbound` + `InspectOutbound` (F-E — ConfigSanityCheck runtime + CustomConfigInjector's paste gate).
    - `Layer6_DeepVerify.InspectForDeepVerify` (Wave 7c-1 — VlessDeepVerifier fail-fast).
  - `PlaceholderConfigException` moved into this file (was in PlaceholderGuard.cs).
  - `#nullable enable` at top per quality bar.

- **`VPNRouter.Core/Services/PlaceholderGuard.cs`** *(reduced 185→62 LOC)* — thin back-compat forwarder. Every member is a one-line pass-through to `PlaceholderDefense`. The ~13 existing call sites (parsers, subscription fetcher, custom-config injector, settings migrator, Android storage, deep verifier, etc.) compile unchanged.

- **`VPNRouter.Core/Services/ConfigSanityCheck.cs`** *(simplified 103 lines changed)* — the three fingerprint hash-sets (`KnownPlaceholderPubkeys`/`ShortIds`/`Servers`) are now back-compat forwarders to `PlaceholderDefense.KnownPubkeys/ShortIds/Servers`. `FindFirstProxyOutbound` and `InspectOutbound` forward to `LayerE_RuntimeSanity`. `CheckBeforeStart` logic unchanged.

- **`VPNRouter.Core/Services/VlessServersResolver.cs`** *(simplified ~25 LOC)* — `IsPlaceholderEntry` now forwards to `PlaceholderDefense.LayerA_ResolverScopeGuard.IsPlaceholderEntry`. Resolver logic unchanged.

- **`VPNRouter.Core/Services/AutoFailoverEngine.cs`** *(simplified ~10 LOC)* — `IsCandidateUsable` now does a single `PlaceholderDefense.Inspect(entry) is not null` check instead of three separate hash-set reach-ins. Behavior identical.

- **`VPNRouter.Core/Services/SubscriptionFetcher.cs`** *(comment cleanup ~5 LOC)* — comment with embedded literal pubkey replaced with reference to `PlaceholderDefense.KnownFingerprints`. Functional behavior unchanged (still calls `PlaceholderGuard.IsPlaceholder`).

### LOC delta

```
 VPNRouter.Core/Services/AutoFailoverEngine.cs   |  15 +-
 VPNRouter.Core/Services/ConfigSanityCheck.cs    | 103 +++++-------
 VPNRouter.Core/Services/PlaceholderGuard.cs     | 204 +++++-------------------
 VPNRouter.Core/Services/SubscriptionFetcher.cs  |  16 +-
 VPNRouter.Core/Services/VlessServersResolver.cs |  31 +---
 5 files changed, 100 insertions(+), 269 deletions(-)
```

Plus the new `PlaceholderDefense.cs` at 528 LOC. **Net repo LOC: -269 + 100 + 528 = +359** (mostly documentation in the consolidated file; layer logic is the same size, just relocated).

### Single source of truth — grep proof

Verified across all `*.cs` in `VPNRouter.Core/`, `VPNRouter.App/`, `VPNRouter.CLI/`, `VPNRouter.Service/`, `VPNRouter.Android/`:

```
$ grep -rn "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU" --include="*.cs" {Core,App,CLI,Service,Android}/

VPNRouter.Core/Services/PlaceholderDefense.cs:7    // ... "DnT9hI..." (originally an Android smoke- ← file-level history comment
VPNRouter.Core/Services/PlaceholderDefense.cs:104  // pubkey string "DnT9hI..." — after            ← CI grep-gate guidance comment
VPNRouter.Core/Services/PlaceholderDefense.cs:197              Pubkey = "DnT9hI...",                ← THE ONE ACTIVE DECLARATION
```

The same single-file confinement holds for `"78ca7952"` (only at `PlaceholderDefense.cs:202`) and `"195.135.255.216"` (only at `PlaceholderDefense.cs:207`) in production code. Test files retain their hardcoded constants — deliberately, as they pin against accidental fingerprint changes (see PlaceholderInputGateTests.cs:27-30 comment).

### Test deltas

```
$ dotnet test ... --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"
Passed!  - Failed:     0, Passed:  1005, Skipped:     4, Total:  1009, Duration: 15 s
```

All 1005 scoped tests pass. The 4 skipped are unrelated (AndroidApp dump fact, multi-server config — pre-existing skips).

Targeted re-run on the 6-layer placeholder suite:
```
$ dotnet test ... --filter "FullyQualifiedName~PlaceholderGuardTests|...InputGate...|...MigratorPlaceholder...|...FetcherPlaceholder...|...ResolverScopeGuard...|...SanityCheck...|...DeepVerifier...|...CustomConfigPlaceholder...|...AutoFailoverEngine...|...LeakProtectionScopeAware..."
Passed!  - Failed:     0, Passed:    88, Skipped:     0, Total:    88, Duration: 6 s
```

88/88 placeholder-defense tests green. Includes the original 65+ existing tests + the 4 v2.32.3 placeholder tests + the 4 Wave 7c-1 v2.32.3-6th-layer tests called out in the brief.

### Verification gate checkboxes

- [x] PlaceholderDefense.cs created (528 LOC, target was ~400 — over by ~28% due to ample historical documentation + the `PlaceholderFingerprint` record type, both intentional).
- [x] 6 internal sub-classes + shared fingerprint list (5 sub-classes by layer; the public `Inspect`/`IsPlaceholder`/`InspectUri` triple handles the "input gate" parser/QR/subscription concerns directly. Each F-A/F-B/F-D/F-E/Layer-6 has a dedicated sub-class. F-C is the App-layer UI badge — out of scope for Core consolidation per brief.).
- [x] PlaceholderGuard kept as forwarder (one-line `=>` per member).
- [x] No hardcoded `DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` outside PlaceholderDefense in production code (verified via grep across all five projects).
- [x] All 65+ existing placeholder tests pass (88 passed total, includes the v2.32.3 + Wave 7c-1 sets).
- [x] **Gate 1**: build 0 errors (Release config, full solution).
- [x] **Gate 2**: scoped suite green (1005/1009 passed, 4 unrelated skips).
- [x] **Gate 4 security-review**: no layer weakened — verified manually:
  - **F-A**: pre/post both reject placeholders via Server/Reality.PublicKey/Reality.ShortId match, same null-safety, same `return false` on null entry. Equivalent.
  - **F-B**: SettingsMigrator unchanged — still calls `PlaceholderGuard.Inspect` (now forwards to `PlaceholderDefense.Inspect`).
  - **F-D**: LeakProtection unchanged — still calls `VlessServersResolver.IsPlaceholderEntry` (now forwards to LayerA).
  - **F-E**: ConfigSanityCheck.CheckBeforeStart unchanged in flow; `InspectOutbound` forwards to LayerE_RuntimeSanity with identical proxy-type list (vless/hysteria2/tuic/shadowsocks/trojan) and same Reality field walk. Hash-set back-compat properties still expose the same surface to existing callers (AutoFailoverEngine, etc.) but now source from consolidated list.
  - **Layer-6**: VlessDeepVerifier.VerifyAsync unchanged — still calls `PlaceholderGuard.Inspect(entry)` (now forwards).
  - **AutoFailoverEngine**: simplified to single `PlaceholderDefense.Inspect(entry) is not null` check; functionally equivalent (the three independent hash-set checks pre-3D returned `false` on any match, which is what the consolidated Inspect now does in one call).
- [x] **Hook gates**: pre-commit hooks not invoked (no commit — integrator commits per brief).

### Surprises / notes

1. **Android-specific fingerprints**: brief asked to check `VPNRouter.Android/` for fingerprints not in Core. Searched all `.cs` files there for the placeholder pubkey + short_id + server IP — **zero matches**. The Android side already routed everything through `PlaceholderGuard.IsPlaceholder` (now → PlaceholderDefense). Nothing to add.

2. **`sealed` on internal static classes**: The brief specified "sealed sub-classes (internal)". C# does not allow `sealed` on `static` classes (they're already implicitly sealed since you can't derive from them). The sub-classes are written as `internal static class`, which is the C# equivalent of "internal sealed" for type-level access. Functionally and structurally identical.

3. **`PlaceholderFingerprint` record**: New public type. Adds a small surface to the Core API. Rationale: the brief required a "single `IReadOnlyList<PlaceholderFingerprint>` KnownFingerprints property as the single source of truth". The record carries `Pubkey`/`ShortId`/`Server`/`Origin` so adding a new fingerprint touches exactly one place + carries provenance for future audits.

4. **Layer-C (UI badge)**: stays in App layer per brief. Not consolidated here. The App reads `PlaceholderDefense.Inspect` via the existing `PlaceholderGuard` forwarder.

5. **Test count**: brief said "all 65+ existing placeholder tests pass". The actual count of placeholder-defense-related tests across the relevant test classes (PlaceholderGuard, PlaceholderInputGate, SettingsMigratorPlaceholder, SubscriptionFetcherPlaceholder, VlessServersResolverScopeGuard, ConfigSanityCheck, VlessDeepVerifierBehaviour, CustomConfigPlaceholder, AutoFailoverEngine, LeakProtectionScopeAware) is **88** — exceeds the brief target.

6. **Phase 4 follow-up**: Brief mentions a CI workflow that grep-scans for hardcoded placeholder pubkey strings, failing if any outside PlaceholderDefense.cs. The current consolidation makes such a CI gate viable — the grep would only need to allow `VPNRouter.Core/Services/PlaceholderDefense.cs` in production paths (test files retain hardcoded constants deliberately).

## Follow-up

- If 3F lands (Android IUpdateSource), the Android-side fingerprint list (currently in Android-only `BuiltInAndroidProfiles` or similar) should also reference PlaceholderDefense.KnownFingerprints.
- ~~Phase 4: add a CI workflow that grep-scans the repo for hardcoded placeholder pubkey strings, fails if any outside PlaceholderDefense.cs.~~ **DONE** (Wave 17, 2026-05-18) — `.github/workflows/grep-placeholder-fingerprints.yml` shipped. Also dropped literal `195.135.255.216` from three Core service comments (`ConfigSanityCheck.cs`, `VlessDeepVerifier.cs`, `VlessServersResolver.cs`) — they now reference `PlaceholderDefense.KnownFingerprints` instead, tightening single-source-of-truth. Brief: `plans/phase4-ci-placeholder-gate-2026-05-18.md`.
- ~~**`config.example.yaml` placeholder cleanup**: the root-level documentation example still hardcodes the literal pubkey + short_id at lines 52-53. It's carved out in the CI gate's allow-list with a comment pointing back here. Separate follow-up: replace the literal values with `REPLACE_ME_WITH_YOUR_KEY` / `REPLACE_ME_WITH_YOUR_SHORT_ID` tokens so users who copy the example never accidentally re-introduce the v2.32.3 placeholder triple. Low priority — the gate prevents fresh drift; this is a UX hardening for new users.~~ **DONE** (Wave 25, Phase 5 AOT-1, 2026-05-18). `config.example.yaml` now uses `REPLACE_ME_SERVER_HOST` / `REPLACE_ME_UUID` / `REPLACE_ME_REALITY_PUBLIC_KEY` / `REPLACE_ME_SHORT_ID` tokens + a top-of-file IMPORTANT comment explaining the placeholder defense will reject any verbatim copy. The CI grep-gate carve-out for `config.example.yaml` was removed in the same wave — the file no longer contains any placeholder fingerprint so it doesn't need the exception. Brief: `plans/phase5-config-example-aot-prep-2026-05-18.md`.
