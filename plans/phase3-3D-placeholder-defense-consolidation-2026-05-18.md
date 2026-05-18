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
*(filled by agent)*

## Follow-up

- If 3F lands (Android IUpdateSource), the Android-side fingerprint list (currently in Android-only `BuiltInAndroidProfiles` or similar) should also reference PlaceholderDefense.KnownFingerprints.
- Phase 4: add a CI workflow that grep-scans the repo for hardcoded placeholder pubkey strings, fails if any outside PlaceholderDefense.cs.
