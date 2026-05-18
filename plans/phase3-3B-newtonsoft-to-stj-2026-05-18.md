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
*(filled by agent)*

## Follow-up

- If any DTO requires a custom converter (e.g. `IPAddress`), document the converter in `VPNRouter.Core/Services/Json/` for future-DTOs reuse.
- AOT-compatibility check for Android: when JsonSerializerContext-based source generation is on the table, file a Phase 4 task.
