# Architecture hardening — model-system findings (v2.39+)

**Authored**: 2026-05-29 (after v2.38.0 stable cut). Theoretical model-system
review, NOT a token-audit. These are *structural* findings — fixing them removes
whole bug-classes that the `critical-audit-targets.md` audit would otherwise
keep *detecting* instance-by-instance.

**Grounding done this session**: no DI container in App (composition via the
`VPNRouter.Core/Platform/PlatformServices.cs` factory); modes are loose strings
(`AppSettings.cs:276` ConfigMode, `:219` RoutingAppsMode, `Profile.cs:30`
DnsMode) normalized post-hoc in `AppSettingsSane.cs`.

**Verdict**: layering (Core purity) is respected; **domain modeling** is not —
the costly bugs were born from state + invariants expressed by *convention*,
not *types*.

Priority order = leverage (bug-class removed × blast radius) ÷ cost.

---

## AF-1 — Stringly-typed mode state = an implicit state machine by hand  ★ root #1

**Symptom.** Recurring same-shape bugs: v2.28.2 (subscribe leak), v2.28.2-r2
(custom without config), v2.28.3-r4 ("IsSubscribeMode wins"), r6 (exclude-mode
shell-verb leak). Each = a missed transition or flipped polarity.

**Root cause.** The mode automaton (subscribe/generated/custom × include/exclude
× dns) is spread across independent strings in `AppSettings` PLUS duplicating
booleans in the VM (`IsSubscribeMode`/`IsVlessMode`) hand-synced in
`SaveSettings`. Two sources of truth reconciled by a rule. `AppSettingsSane` is
a band-aid normalizer, not an invariant.

**Fix strategy.**
- Min: `enum ConfigMode { Subscribe, Generated, Custom }` + one normalize-on-load;
  collapse VM booleans into a single derived property over one field.
- Better: model the config source as a sealed/discriminated union
  (`Subscribe(sub) | Generated(servers) | Custom(path)`) so "custom without path"
  is **unrepresentable**.
- New tests: round-trip + every transition; delete the per-mode guard patches as
  they become dead.

**Why.** Removes the silent-leak-from-mode-desync class — the worst VPN failure.
Past fixes patched symptoms; the type makes the symptom unrepresentable.

**Where.** `AppSettings.cs:276/:219`, `Profile.cs:30`, `MainWindowViewModel.SaveSettings`
+ `Is*Mode` booleans, `AppSettingsSane.cs`. **Cross-ref audit stage W1.2.**

**SCOUT UPDATE (W1.2, 2026-05-29).** Confirmed the scatter — `RoutingAppsMode ==
"exclude"` is independently re-derived in ~5 sites: `GetActiveAppList` (MVM:715),
`ConfigGenerator` (:53), SaveSettings legacy sweep (:3847),
`OnRoutingAppsModeChanged`, `IsRoutingAppsModeExclude` (:630). Consistent today
(no bug — W1.2 polarity is sound), but this is the exact fragility the enum
collapses into one place. Also found a stale comment (`ConfigGenerator.cs:45-50`)
describing a list-population mode-inference the code does NOT do — fold a
one-line comment fix into this item when it lands.

**Risk.** MEDIUM — touches SaveSettings + YAML schema; needs a migration for
existing string values (trivial: parse string → enum on load). Characterization
hash will move (mode booleans are part of MVM surface) — re-pin.

---

## AF-2 — Resolve-before-Generate invariant held by convention, not types  ★

**Symptom.** v2.28.1 silent leak — a caller of `ConfigGenerator.Generate` skipped
`VlessServersResolver.Resolve`.

**Root cause.** "Always Resolve before Generate" lives in comments + a runtime
`LeakProtection` backstop. Three callers (`StartAsync`, `Apply`, `HealthMonitor`)
must each remember. Nothing stops calling Generate with unresolved servers.

**Fix strategy (RE-SCOPED after W1.1 scout, 2026-05-29).** The funnel ALREADY
EXISTS for desktop: `ConfigPipeline.Generate` (Phase 2F) couples
`VlessServersResolver.Resolve` (:97) → empty-guard (:104) →
`ConfigGenerator.Generate` (:115) → `LeakProtection.ValidateConfig` (:123). GUI +
HealthMonitor go through it; CLI resolves via `SubscriptionResolver.ResolveAsync`
(`StartCommand.cs:84`) first; the hard-guard (`ConfigGenerator.cs:948`) turns any
future skip into a LOUD throw, never a silent leak. The "3 callers" framing was
stale — the real prod callers are ConfigPipeline + CLI + Android. So AF-2 shrinks
to two cheap moves:
- (a) make `ConfigGenerator.Generate` `internal` + an `[Obsolete]`/guard note (or
  require a `ResolvedServers` token) so a raw call is hard to write by accident;
- (b) route `AndroidConfigBuilder.cs:129` through `ConfigPipeline` (or add the
  missing `LeakProtection.ValidateConfig`) — it currently bypasses it, the one
  finding the W1.1 scout surfaced.

**Why.** A missed Resolve = silent traffic leak = cardinal sin. Convention +
guard caught it only AFTER it shipped; a type catches it at compile time. This
is the *preventive* form of audit-stage W1.1 — fix it and W1.1 shrinks to
"is the proxy outbound shaped right", because "did we resolve" becomes
impossible to get wrong.

**Where.** `ConfigGenerator.Generate` signature, `VlessServersResolver`, the 3
call-sites. **Cross-ref audit stage W1.1.**

**Risk.** LOW — pure signature/threading change, no schema. Highest leverage per
LOC of any item here.

---

## AF-3 — Fat ViewModel: orchestration in the wrong layer

**Symptom.** `MainWindowViewModel` = ~7250 LOC ("grew back in v2.37.0 from
ZapretOneClick orchestration"), pinned by a characterization hash (dual Win/Linux
pin + hash-drift bumps).

**Root cause.** The VM does what belongs in Core: connection lifecycle, Zapret
orchestration, apply/reconnect. MVVM boundary violated — VM is presentation AND
application-service. The 10-file partial split is cosmetic (same class, same
surface). The hash is a *symptom*: the class is too big to test by behavior, so
it's tested by reflection-shape — and that pin actively resists decomposition.

**Fix strategy.** Extract application-service objects into Core
(`ConnectionOrchestrator`, `ZapretOrchestrator`); VM becomes thin glue. Replace
the hash crutch with real behavior tests on the extracted services (unit-testable
in Core, no UI). Move incrementally, one orchestrator per -rN.

**Why.** The most-churned, least-testable logic lives in the VM; its bugs
(mode-desync, apply-race) are the ones biting. Moving to Core makes it testable
AND reusable by CLI/Service/Android (which today can't reuse VM logic).

**Where.** `MainWindowViewModel.cs` + partials → new Core orchestrators.

**Risk.** HIGH (in aggregate) — do it in small, hash-pinned slices. Each slice is
LOW if it preserves behavior (characterization gate proves it).

---

## AF-4 — OS coupling via locale-dependent text parsing + hand-written cmd/ps1

**Symptom.** helper.cmd CMD-parser bug bricked 100% of v2.31.7 upgrades; CO-5
localized-netsh parser wiped RU/DE/ES firewall rules.

**Root cause.** Critical OS ops (firewall, update-helper, service control) shell
out to netsh/sc/cmd and parse locale-dependent text — brittle, hard to test, on
the highest-blast-radius paths.

**Fix strategy.** Structured APIs over text: Windows Firewall via COM
(`INetFwPolicy2`)/P-Invoke instead of netsh parse; updater logic in a tested C#
component instead of hand-written cmd (min: exhaustive cmd/ps1 fixture tests with
locale fixtures). Build on the existing `IProcessRunner` seam.

**Why.** These two paths caused TOTAL-outage incidents. Locale text parsing is a
structural fragility, not a one-off. Preventive for audit-stages W2/W3.

**Where.** `FirewallManager.cs` (netsh), `packaging/windows/install.ps1` +
helper.cmd, `ServiceInstaller` (sc.exe). **Cross-ref W2/W3.**

**Risk.** MEDIUM — firewall COM rewrite needs careful parity testing against the
netsh behavior; partial (fixture tests first) is LOW-risk and already valuable.

---

## AF-5 — sing-box lifecycle = hand-rolled concurrent state machine (B1–B4 open)

**Symptom.** Documented latent races: B1 (ProcessExit dual-hook), B2 (concurrent
Stop), **B3 (Restart state-race — still open)**, B4 (State-field unsync). The
intentional-stop pattern (`EnableRaisingEvents=false` before `Kill`) is clever
but fragile.

**Root cause.** Process state (Stopped/Starting/Running/Stopping/Crashed) is
implicit, spread across an event callback + cancellation tokens + optimistic
bools, with transitions arriving from multiple threads (UI Stop, HealthMonitor
restart, ProcessExit) with no single serialization point.

**Fix strategy.** One explicit state machine; ALL transitions through a single
lock / serialized queue (or actor/`Channel`), replacing the flags. B1–B4 then
collapse into "is this transition legal from this state?". This is the vehicle to
finally close B3 with a dedicated test suite.

**Why.** Connection reliability — symptoms are internet-stuck-on-stop,
restart-loops, TUN-orphan. Concurrency bugs resist point-fixes (each B is a
patch). Audit-stage W4 *detects* these; the explicit machine *removes* them.

**Where.** `SingBoxManager.cs` (1562), `HealthMonitor.cs` (766). Existing backlog:
`plans/singbox-lifecycle-hardening-v2.36.md`. **Cross-ref W4.**

**Risk.** MEDIUM-HIGH — concurrency rewrite; gate with the SingBoxManager state
tests (Task #21) + new race tests before/after.

---

## AF-6 — DI factory-based, seams uneven → testability is patchy  (lighter)

**Symptom.** Happy-path tests rely on null-seams; coverage is lumpy.

**Root cause.** `IProcessRunner`/`IHttpClient`/`IWindowsDnsHardening` seams are
the right direction but partial; `PlatformServices` is a factory, not a
composition root with uniform injection. Some services still `new` deps or call
statics (`AppPaths`, `SettingsLoader`).

**Fix strategy.** Uniform constructor injection across Core services + interfaces
for `SettingsLoader`/`AppPaths` (a container is NOT required — the factory can
stay). 

**Why.** Lets behavior tests replace the characterization-hash crutch (AF-3
enabler); uniform seams.

**Where.** `VPNRouter.Core/Platform/PlatformServices.cs` + service ctors.

**Risk.** LOW — mechanical, incremental. This is an *enabler*, not a risk-fix —
lower priority than AF-1..AF-5.

---

## Sequencing (cheap + high-leverage first)

| # | Item | Leverage | Cost | Maps to audit |
|---|---|---|---|---|
| 1 | AF-2 `Generate(ResolvedServers)` type-invariant | compile-time anti-leak | LOW | shrinks W1.1 |
| 2 | AF-1 modes → enum/sealed union | removes leak-bug class | LOW-MED | shrinks W1.2 |
| 3 | AF-5 explicit sing-box state machine, close B3 | reliability + B1–B4 | MED-HIGH | closes W4 |
| 4 | AF-4 firewall COM + cmd/ps1 fixture tests | anti-outage | MED | shrinks W2/W3 |
| 5 | AF-3 extract VM orchestration → Core (incremental) | testability + reuse | HIGH (staged) | — |
| 6 | AF-6 uniform DI seams | enabler for AF-3 tests | LOW | — |

**Key insight.** AF-1 + AF-2 are the *preventive* versions of what the paid W1
audit would detect. Land them and ~half of W1 becomes "unrepresentable by
construction" → the expensive sweep narrows.

## Cross-references
- `plans/critical-audit-targets.md` — the *detection* counterpart (W1 staged).
- `plans/singbox-lifecycle-hardening-v2.36.md` — B1–B4 detail (AF-5).
- Each item, when started, gets a per-task brief via the `phase-task-launcher`
  skill (brief → branch → impl → verify → outcome).
