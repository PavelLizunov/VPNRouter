# Hotfix brief — v2.35.0-r5 user rollback (brat 2026-05-19)

## TL;DR

User `brat` updated to v2.35.0-r5, hit two regressions in close succession,
rolled back to v2.32.2 stable. Logs in `Z:/brat/`. Two root causes
identified, third remains a mystery.

| # | Symptom | Root cause | Fix |
|---|---|---|---|
| 1 | F-12 invariant fires on retry-after-conflict | At F-12 time, both subs empty + Vless.Servers empty + Vless.Server empty | Soften F-12 to ALSO allow connection when Vless.Server (legacy) is set OR fall through to engine where VlessServersResolver has a manual-fallback path |
| 2 | TUN warm-up takes 33s vs 2s | `Remove-NetAdapter` cmdlet missing on brat's PowerShell — Wave 38a probes it unconditionally, swallows error but loses 600 ms × multiple call sites × something deeper | Fast-fail Remove-NetAdapter probe once per process; cache "module missing" flag |
| 3 | r5 LoadSettingsIntoUI sees manual=0, v2.32.2 sees manual=1-2 from same YAML | UNKNOWN — round-trip tests pass | Investigate; ship defensive logging in r6 |

## Timeline (single log: vpnrouter20260519.log)

```
11:12:32 [v2.32.2] Sub-tab init: manual=2, configMode=subscribe ← brat baseline
...all-day VPN use with subscribe + 7 ninitux servers...
20:42:23 [v2.32.2] SubRefresh Checking 1 subscription(s)... → 7 servers
21:36:49 [v2.32.2] [ERR] [UpdateVm] Update failed                  ← r4 URL 404 (we deleted r4 after r5 ship)
21:37:02 [v2.32.2] sing-box crashed (exit -1)                     ← v2.32.2 dying during update transition
21:37:03 [helper] update.log: 274 files copied, r5 launching
21:37:04 [r5]      Sub-tab init: manual=0, configMode=generated   ← REGRESSION (was manual=2/subscribe)
21:37:04 [r5]      AM-3 seeded RoutingAppsInclude with 55 entries  ← "first load" — RoutingAppsInclude empty in YAML?
21:37:12 [r5]      User clicks Subscribe tab → IsSubscribeMode=true
21:37:29 [r5]      Subscription Fetched 7 servers from ninitux    ← OK, sub fetch works
21:37:29 [r5]      ToggleConnect.Subscribe aggregated 7 servers
21:37:29 [r5]      Conflict: amneziavpn (PID 15520)
21:37:31 [r5]      User Ignores conflict → retry
21:37:31 [r5]      VlessServersResolver Aggregated 7 servers
21:37:36 [r5]      sing-box started PID 17804
21:37:36 [r5]      DnsHardening applied + DnsLeakLockdown enabled ← Wave 39 firewall lockdown ACTIVE
21:37:53 [r5]      TUN ready after 17100ms (attempt 5)            ← REGRESSION (Wave 38a slow path)
21:37:51-21:38:38: user switches servers → reconnects fail with conflict + slow TUN
21:39:20 [r5]      [WRN] TUN warm-up failed after 33013ms          ← 33s warm-up timeout
21:39:35 [r5]      Subscription Fetching LAN sub (192.168.0.236)  ← user must have added new sub during r5
21:39:35 [r5]      JSON response has no 'config' field             ← LAN sub broken (user's own server)
21:39:35 [r5]      Refresh returned 0 servers, keeping 0 cached
21:39:35-46:        repeated LAN sub fetch attempts (×3), all 0 servers
21:39:46 [r5]      Conflict re-detected → user Ignores again
21:39:47.910 [r5]  [StartupPipeline] Skipping conflict check (user opt-in)
21:39:47.920 [r5]  [DnsFlusher] Windows DNS cache flushed
21:39:47.920 [r5]  [ERR] [StartupPipeline] AppSettings invariant violation pre-generation:
                   ConfigMode=subscribe but no subscription has fetched any servers
                   and no manual VLESS server is configured as a fallback. ← F-12 FIRE
21:40:30 [v2.32.2] Sub-tab init: manual=1, configMode=generated   ← user rolled back
21:42:31 [v2.32.2] Sub-tab init: manual=1, configMode=generated   ← second start, stable
21:42:35 [v2.32.2] [WRN] [VlessServersResolver] config_mode=subscribe but no enabled
                   subscription has servers. Falling back to manually-configured
                   Vless.Servers / Vless.Server.                  ← v2.32.2 has FALLBACK
21:42:38 [v2.32.2] TUN ready after 2323ms (attempt 1)            ← back to 2s
21:42:38 [v2.32.2] Connected (PID 27948)
21:43:06 [v2.32.2] HealthMonitor VPN is up                       ← working
```

## Root cause 1 — F-12 too aggressive when manual fallback exists

`LeakProtection.ValidateAppSettings` fires F-12 invariant when:

```csharp
isSubscribe
  && enabledSubsWithServers.Count == 0
  && manualServerCount == 0
  && !hasLegacyVlessServer
```

`VlessServersResolver.Resolve` (in `StartupPipeline.ExecuteAsync` line 518)
has a softer behaviour: it emits a WARNING and falls back to the manual
Vless.Servers / Vless.Server list:

```
21:42:35.473 [WRN] [VlessServersResolver] config_mode=subscribe but no enabled
subscription has servers. Falling back to manually-configured Vless.Servers / Vless.Server.
```

The F-12 invariant in r5 runs **BEFORE** the resolver, so it short-circuits
that fallback. The result: in v2.32.2 the user gets a warning + connects,
in r5 they get an "invariant violation pre-generation" error.

The user's situation at F-12 fire time was specifically the case the resolver
WAS designed to handle. F-12 is a defense-in-depth net for a silent-leak
class, but it shouldn't preempt the resolver's own fallback.

### Fix candidates (pick one for r6)

**Option A (chosen): defer to VlessServersResolver.**
Remove the `manualServerCount` + `hasLegacyVlessServer` short-circuits from
F-12 — let the resolver handle the empty-subs case. The resolver already
emits a clear warning and either falls back (manual configured) or returns
empty (true error). The "is empty" case still throws via
`StartupPipeline.ExecuteAsync` line 519 (`if (allServers.Count == 0)`), so
silent-leak protection is preserved.

**Option B: widen F-12's fallback gate.**
Allow F-12 to pass when `Vless.Servers.Count > 0 OR Vless.Server set OR ALL
enabled subs have servers`. Same end state but split between two layers.

Pick A: simpler, single point of validation, matches the v2.32.2 behaviour
the user explicitly relied on.

## Root cause 2 — TUN cleanup blocking warm-up by 33s

brat's PowerShell environment is missing the NetAdapter module:

```
[WRN] [TunDiag] StartupPipeline: Remove-NetAdapter for 'VPNRouter-TUN'
returned exit 1: stdout='' stderr='Remove-NetAdapter : The term
'Remove-NetAdapter' is not recognized as the name of a cmdlet, function,
script file, or operable program.'
```

Wave 38a added `PreStartCleanupAsync` which runs this probe at multiple
sites: `StartupPipeline.ExecuteAsync`, `SingBoxManager.LaunchProcess`,
`SingBoxManager.StopInternal.killed.async`. Each call:

1. `netsh interface show interface` enumeration → 0 stale adapters found
2. **Direct fallback**: `pwsh -Command Remove-NetAdapter -Name VPNRouter-TUN` → returns exit 1 (cmdlet missing)
3. Logs warning + continues

In isolation each Remove-NetAdapter call returns in ~600 ms. brat hits this
3× per connect (3 cleanup sites × 1 call each), but somewhere else in the
warm-up loop (TUN-up retry) there's a 33-second cumulative wait that v2.32.2
didn't have.

### Fix (r6)

Cache the "Remove-NetAdapter cmdlet missing" flag per-process. After the
first failed probe, skip subsequent invocations and emit a single info-line
diagnostic explaining the environment limitation. This converts 3+ × 600 ms
into 1 × 600 ms across a process lifetime.

Long-term: implement a netsh-only TUN removal path
(`netsh interface set interface name="VPNRouter-TUN" admin=disable` + manual
unbind, or `pnputil /remove-device`) as the primary mechanism, with
`Remove-NetAdapter` as the optional fast-path only when available.

## Root cause 3 (UNRESOLVED) — r5 sees manual=0, v2.32.2 sees manual=1-2

Same YAML on disk. Same `SettingsLoader.Parse` path. Same generated
`YamlStaticContext.g.cs` (`YamlStaticContextRoundTripTests` pass in CI).

Hypotheses (none confirmed):

1. **YAML deserialization regression in r5 specific to Vless.Servers.**
   Phase 6 Wave 31a swapped to `StaticDeserializerBuilder`. If the analyzer
   regenerates the context due to the new `DnsLeakLockdown` field and the
   regen drops a property handler for `VlessServerEntry`, we'd see this.
   Round-trip tests pass against the test fixture but the test fixture
   may differ structurally from brat's real YAML.

2. **Migration side-effect from a corrupted/missing `schema_version` field.**
   If brat's YAML lost its `schema_version` line (e.g. previous SaveSettings
   crashed mid-write), `SchemaVersion` defaults to `CurrentSchemaVersion=5`.
   No migration runs. But that wouldn't clear Vless.Servers either.

   Alternative: if `schema_version` got reset to 0/null, migrator runs
   v0→v5, exercising `Migrate_2_to_3.CleanupOrphanVlessServers` which DOES
   strip Vless.Servers entries that don't match subscription server keys.
   For brat's manual main-brat server NOT in ninitux's sub list → stripped.
   But brat's log has NO `[SettingsMigrator]` log line, ruling this out.

3. **User explicitly deleted manual servers during r5 use.**
   Unlikely — there's no UI event for it in the log. But brat is a power
   user who might have edited the YAML directly while debugging.

### Diagnostic action (r6)

Add an explicit one-line log at SettingsLoader.LoadCore end:
```
[SettingsLoader] Loaded config.yaml: schema={N}, subs={S} (servers={X}/{Y}/...),
vless.servers={M}, vless.server='{S}', config_mode={C}
```

So the next user report has the post-load state visible without needing
the config.yaml file.

## Action items for r6

| ID | Description | Risk |
|----|-------------|------|
| BR-1 | Soften F-12: remove manual/legacy short-circuits, defer to VlessServersResolver | LOW — silent-leak still caught by resolver+ConfigGenerator empty guards |
| BR-2 | Fast-fail Remove-NetAdapter probe (cache module-missing flag per-process) | LOW — pure perf optimisation, same correctness |
| BR-3 | Add SettingsLoader load-state diagnostic log | NONE — observability only |
| BR-4 | (Investigative) write a test fixture mimicking brat's YAML state to repro the manual=0 bug | NONE — diagnostic |

## Why brat could roll back successfully

The `.bak` file path in `SaveSettings`:

```csharp
if (File.Exists(configPath))
    File.Copy(configPath, configPath + ".bak", overwrite: true);
```

Before each SaveSettings, the current YAML is copied to .bak. So when r5
overwrote brat's YAML (clearing Vless.Servers to []), the .bak retained
the previous state. brat (a known power user) likely restored .bak →
config.yaml manually before launching v2.32.2.

This is how v2.32.2 at 21:42:31 saw manual=1 even though r5 had presumably
written manual=0 minutes earlier.

## Why r5 has DnsLeakLockdown enabled (it should be false for upgrades)

brat's log shows DnsLeakLockdown ENABLED at 21:37:36, even though
`Migrate_4_to_5` is supposed to flip it to false for upgrades. The migration
step ONLY runs if `from < to`, i.e. if YAML schema_version < 5.

Hypothesis: brat's YAML schema_version was missing → defaulted to 5 →
migration didn't run → DnsLeakLockdown default `true` applies. Combined
with hypothesis 2 above for the Vless.Servers issue: maybe brat's YAML lost
schema_version somewhere, defaulted to 5, no migration ran. Also explains
no `[SettingsMigrator]` log entry in r5's log.

But this still doesn't explain why manual=0 in r5 vs manual=1 in v2.32.2
from the same YAML — both would default to 5 and skip migration.

UNLESS the YAML schema_version-missing path actually went through SR-4
unloadable recovery (creating defaults). But that path emits a clear log
line + a `.unloadable-{ts}` backup — neither shows up in brat's log.

The mystery stands. Ship r6 with BR-1+BR-2+BR-3 and hope BR-4 catches it
next round.
