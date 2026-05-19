# v2.35.0-r6 — brat hotfix follow-up

## Brief

User `brat` rolled back from v2.35.0-r5 to v2.32.2 stable. Logs in
`Z:/brat/`. Three regressions identified — see
`plans/hotfix-brat-r5-rollback-2026-05-19.md` for the full timeline and
root-cause analysis.

r6 ships defensive fixes for the two clearly-actionable items (BR-1, BR-2)
and observability for the third (BR-3).

## Why

v2.35.0-r5's `LeakProtection.ValidateAppSettings` F-12 invariant fired at
the wrong time, blocking brat from connecting after he had already
worked through an AmneziaVPN conflict and was retrying via the
"Ignore conflict, retry" path. v2.32.2's `VlessServersResolver` would
have logged a warning and fallen back to the manual VLESS entry — F-12
short-circuited that fallback in r5.

Separately, Wave 38a's `Remove-NetAdapter` PowerShell probe stalls
~600 ms per cleanup site on machines where the cmdlet is missing (no
NetAdapter module installed). brat hit this 3+ times per connect
attempt — contributing to the 33-second TUN warm-up timeout vs
v2.32.2's 2 seconds.

## What

### BR-1 — soften F-12 (LOW risk)

`VPNRouter.Core/Services/LeakProtection.cs:67-94`. Removed the
"enabled-sub-but-no-servers + no-manual-fallback" error branch from
`ValidateAppSettings`. The two structural invariants survive
(`subs.Count == 0`, `enabledSubs.Count == 0`); the third condition is
the resolver's job.

Silent-leak protection preserved:
- `VlessServersResolver.Resolve` (line 518 of StartupPipeline.ExecuteAsync)
  emits the documented fallback warning + uses manual Vless.Servers
  / Vless.Server when present.
- `ConfigGenerator.Generate` hard-throws on empty Vless.Servers
  (v2.28.2 hard guard) — catches the case where neither subs NOR
  manual entries exist.

Test impact:
- `LeakProtectionAppSettingsTests.Subscribe_EnabledSubButNoServersAndNoFallback_Fails`
  rewritten as `Subscribe_EnabledSubButNoServers_DefersToResolverFallback`
  (was negative, now positive — same scenario, different expected outcome).
- New pin: `Subscribe_BratScenarioTwoSubsBothEmpty_DefersToResolverFallback`
  reproduces brat's exact AppSettings shape at the moment of the wrong-fire.

### BR-2 — cache Remove-NetAdapter cmdlet-missing (LOW risk)

`VPNRouter.Core/Services/TunAdapterDiagnostics.cs`. Added per-process
`s_removeNetAdapterMissing` int flag. First call to
`TryRemoveAdapterAsync` that observes "is not recognized as the name
of a cmdlet" in stderr (EN/RU/DE locale matches) latches the flag.
All subsequent calls return false immediately, skipping the 600-1000 ms
PowerShell spin-up.

Restart picks up changes — if the user installs the NetAdapter module
later, a process restart re-detects.

Test impact: none added at this layer (would require process-spawning
in tests, which is brittle). Behaviour is covered indirectly by the
existing `TunAdapterReadinessTests` which exercise the cleanup
path without `Remove-NetAdapter` being available on Linux CI.

### BR-3 — SettingsLoader load-state diagnostic (NONE risk)

`VPNRouter.Core/Services/SettingsLoader.cs:241-265`. Single-line
`Console.Error.WriteLine` after successful parse+validation summarising:

```
[SettingsLoader] Loaded {path}: schema={N}, config_mode={M},
subs={K}[+N,-M,…], vless.servers={X}, vless.server={set|empty},
active_sub='…', active_vless='…'
```

Lets the next user-report investigation see the exact post-load state
without needing the actual config.yaml. Future BR-4 (repro the manual=0
loading mystery in brat's r5) leverages this output.

## Verification gate

| Gate | Status |
|---|---|
| `dotnet build VPNRouter.sln -c Release` | 0 errors, 203 pre-existing warnings |
| `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "LeakProtection|VlessServersResolver|ConfigGenerator|SettingsLoader|YamlStaticContext"` | 40/40 pass |
| Full test suite | In progress at brief-write time |
| MCP+UIA test | n/a — Core-only changes, no UI surface |

## What user does

**Nothing required.** Update normally:
- Auto-update banner → click Update
- One-liner: `iwr -useb https://vpn.ninitux.com/install.ps1 | iex`
- Manual: `VPNRouter-v2.35.0-r6-win.zip` from release page

## Risk

LOW across all three changes:
- BR-1 removes an over-aggressive check that pre-empted an existing
  fallback. Silent-leak protection survives at two downstream layers.
- BR-2 is a perf cache for a code path that was already non-fatal
  (warnings, not errors).
- BR-3 is observability only — no behaviour change.

## Carry-over

Ships on top of:
- Wave 39 firewall DNS lockdown from r5 (brat P0 mitigation)
- Wave 38a TUN adapter pre-start cleanup from r4 (alicemoren1991 P0)
- Phase 6 (YAML source-gen, JsonTypeInfo, AndroidJsonContext, etc.)

## Outstanding (BR-4, deferred)

Manual=0 in r5 vs manual=1-2 in v2.32.2 from the same on-disk YAML —
unexplained. BR-3 diagnostic line will help isolate this in the next
user repro. Two outstanding hypotheses:

1. r5 YAML deserialization regression specific to Vless.Servers.
2. brat manually edited the YAML between v2.32.2 stop and r5 start.

If (1) is real, it's a P0 bug that needs a separate hotfix. The lack
of complaints from other users since r5 ship suggests it's environment-
specific to brat — investigation continues if it surfaces again.
