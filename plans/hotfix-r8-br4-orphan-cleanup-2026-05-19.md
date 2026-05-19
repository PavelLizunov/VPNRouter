# v2.35.0-r8 — BR-4: orphan cleanup preserves user's active server

## Brief

Following the 10-iteration deep-dive into brat's manual-server-wipe
regression (see `plans/hotfix-brat-r5-rollback-2026-05-19.md`), reproduce
the bug locally with a synthetic YAML fixture and ship a targeted fix.

## Root cause

`SettingsMigrator.Migrate_2_to_3.CleanupOrphanVlessServers` was too
aggressive. It assumed every `vless.servers[]` entry outside the enabled-
subscription server set was a stale auto-migrated duplicate (the
stas-class shadow-override bug). In practice users also add real manual
servers via the Servers tab, and those entries live in the same list.
If the user had:

- An enabled subscription with servers
- A manual VLESS entry in `vless.servers[]`
- That manual entry selected as `vless.active_server`

then any migration walk that passes through 2→3 would silently wipe the
manual entry. brat's r5 startup hit this path (mechanism for the schema
trigger still unconfirmed — covered by BR-3's new diagnostic line for
the next user report).

## Reproduction (Iter 1-3)

Added `VPNRouter.Tests/BratYamlReproTests.cs` — synthetic YAML mirroring
brat's v2.32.2 state (configMode=subscribe + 1 subscription with 2
servers + 1 manual `Vless.Server` selected as active). Tests confirm:

| schema_version | Strips manual? |
|---|---|
| 0 | YES (full migration walk runs 2→3) |
| 1, 2 | YES |
| 3, 4, 5 | NO |
| missing | NO (C# default field-init = CurrentSchemaVersion) |
| '' (empty) | YamlDotNet zero-inits → triggers + falls to defaults |
| null | Same as '' |

Static-deserializer behaviour was specifically introspected
(`Iter3_StaticDeserializer_SchemaVersionRawBehaviour`) and confirmed
benign for the missing-field case.

## Fix (Iter 4-7)

`VPNRouter.Core/Services/SettingsMigrator.cs:CleanupOrphanVlessServers`.
Predicate adds an explicit `isActive` test alongside the subscription-
key match:

```csharp
var matchesSub = subKeys.Contains(MakeServerKey(srv));
var isActive  = !string.IsNullOrEmpty(activeServerName)
              && string.Equals(srv.Name, activeServerName,
                               StringComparison.OrdinalIgnoreCase);
if (matchesSub || isActive)
    keep.Add(srv);
else
    removed.Add(srv);
```

stas-class placeholder credentials are still cleaned up — by the
sibling `SettingsMigrator.PruneKnownPlaceholders` pass that runs
unconditionally after migration. That pass matches on Reality pubkey
fingerprint (precise) instead of name-mismatch (over-broad), so it
correctly strips stas's placeholder entries even when they're flagged
as `active_server`.

## Verification

- `dotnet build -c Release` — 0 errors
- `dotnet test` — **1174/1178 pass / 0 fail / 4 skip** (full Core +
  App suite minus the slow headless GUI category)
- New tests (Iter 1-10):
  - `BratYamlReproTests.Iter1_BratV232YamlState_LoadsWithoutSilentWipe`
  - `BratYamlReproTests.Iter1d_SchemaVersionZero_TriggersFullMigrationChain_BR4Fix`
  - `BratYamlReproTests.Iter2_AllSchemaVersions_BR4Fix_PreservesActiveServer`
  - `BratYamlReproTests.Iter3_StaticDeserializer_SchemaVersionRawBehaviour`
  - `BratYamlReproTests.Iter4_BR4Fix_PreservesActiveServerOnly_RemovesOthers`
- Updated existing pins to reflect new semantics:
  - `SettingsMigratorLegacyVlessServersCleanupTests.Cleanup_StasFixture_*`
  - `…Migrate_FromV2_PerformsCleanup_PreservesActive_AdvancesToV3`
  - `…Cleanup_IsIdempotent_*`

## Carry-over

Ships on top of r7 (BR-1 F-12 softening + BR-2 NetAdapter cache + BR-3
load-state diagnostic). brat user's exact migration path is still
unverified post-rollback (he's on v2.32.2 stable, no fresh data to
inspect) — BR-3 will surface it next time a similar shape lands.

## Risk

LOW. The change is one line of orphan-detection predicate plus a more
defensive comment block. PruneKnownPlaceholders still catches the
stas-class regressions on a precise fingerprint. All 21
migrator+repro tests pass.

## What user does

**Nothing required.** Update normally. Subsequent connect-via-Ignore
flow with manual VLESS fallback works the same as v2.32.2.
