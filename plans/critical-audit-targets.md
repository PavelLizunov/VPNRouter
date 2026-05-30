# Critical-areas targeted audit — execution plan

**Authored**: 2026-05-29 (after v2.38.0-r6 context-menu audit)
**Mode**: token-budgeted (subscription limits, NOT $ API). Run **end-of-week,
only if subscription token limits remain**. Strict priority order; each
workflow is independent + stoppable — pause after any one if budget runs low,
resume next reset.

## Why targeted, not a uniform 100k-LOC sweep

- Naive full sweep ≈ **~1.6B tokens** — and it physically **can't run as one
  workflow** (hard cap 1000 agents; a uniform sweep needs more). ~95% of that
  would be re-reading mature, multiply-tested code at ~0 hit-rate.
- Targeted high-risk zones ≈ **~28-42M tokens across 4 independent workflows**
  — same bug-catching value where bugs actually live, ~40-50× cheaper.

## Calibration anchor (the only real data point)

Last run = the r1→r5 context-menu diff: **8.3M tokens / 82 agents / ~33 min**
for ~500 changed LOC while reading ~15 files (~6-10k LOC of context). Found
1 HIGH + 5 lower defects. Per-agent ≈ **~100k tokens** (input-dominated — each
agent reads many files). Expect each subsystem workflow below to be the **same
order of magnitude**: per-agent cost ≈ constant; total scales with files-read +
findings-to-verify, NOT linearly with LOC.

Estimates below are **calibrated UP** from that anchor (the r6 run taught us the
loop+verify pattern is ~10× heavier than first guessed). Treat as ±40% ranges.

## Execution policy

- Run W1→W4 **in priority order**. Highest product-risk first.
- Each workflow < 1000-agent cap → fits one run. ~10 concurrent → ~30-50 min wall each.
- After each: relay confirmed findings (3-skeptic verified). Real bugs → fold
  into the next `-rN` (v2.38.x patch or v2.39).
- Stop between any two workflows if subscription tokens are low; resume later.
- Each uses the proven harness: multi-lens find → loop-until-dry → 3-skeptic
  verify (default real=false) → synthesize + completeness-critic.

---

## W1 — Leak path  ★ PRIORITY 1 (the product's cardinal risk)

**Now staged into W1.1–W1.5** (2026-05-29). Each stage is independently runnable,
~2-4M tokens / ~15-30 agents / ~15-25 min, so it fits a single budget-window. Run
in order; stop after any stage. Same harness per stage (multi-lens find →
loop-until-dry → 3-skeptic verify → synth + critic). **W1.1 / W1.2 shrink hard if
AF-2 / AF-1 land first** (`plans/architecture-hardening-v2.39.md`) — the invariant
becomes unrepresentable, so the sweep only checks shape, not "did we forget".

### W1.1 — Proxy-outbound presence + Resolve-before-Generate + route.final
> **SCOUTED 2026-05-29 → CLOSED (no fan-out needed).** All 3 prod callers of
> `ConfigGenerator.Generate` resolve first: `ConfigPipeline.cs:97`
> (`VlessServersResolver.Resolve`) · CLI `StartCommand.cs:84`
> (`SubscriptionResolver.ResolveAsync`) · Android adds the server explicitly
> (`AndroidConfigBuilder.cs:114`). `route.final` polarity correct on all 4 combos
> (`ConfigGenerator.cs:1349-1355`). Proxy-presence DOUBLE-guarded
> (`ConfigPipeline:104` + `ConfigGenerator:948`, both throw). Hard-guard turns any
> future skip into a loud throw, never a silent leak. Tests:
> `ConfigGeneratorIncludeModeTests` / `ConfigGeneratorExcludeModeTests` /
> `ConfigGeneratorEmptyServersGuardTests`. **One out-of-scope finding**: Android
> bypasses `ConfigPipeline` → no `LeakProtection.ValidateConfig` (→ AF-2 (b) +
> backlog). Fan-out skipped — scout extracted the only finding at ~0 tokens.

**The cardinal silent-leak core (v2.28.1).** Scope: `ConfigGenerator.cs` outbound
generation + hard-guard + `route.final`; `VlessServersResolver.cs` + the 3
call-sites (`StartAsync` / `Apply` / `HealthMonitor`). Lenses (3): proxy-outbound
presence · `route.final` direction (split=direct / full=proxy) ·
Resolve-before-Generate at every caller. **Est: ~2-4M · ~15-25 agents · ~15-20m.**
Maps to **AF-2**.

### W1.2 — include↔exclude polarity
> **SCOUTED 2026-05-29 → CLOSED (no fan-out).** Polarity SOUND end-to-end. The VM
> bridge (`GetActiveAppList` MVM:712) and the Core consumer
> (`ConfigGenerator.cs:51-77`) both key off the SAME `RoutingAppsMode == "exclude"`
> and select the SAME list: exclude → `RoutingAppsExclude` → routed direct +
> final proxy; include → `RoutingAppsInclude` (or legacy resolved) → routed proxy
> + final direct. Read/write symmetric (both via `GetActiveAppList`); idempotent
> toggle (`AppItemViewModel.IsChecked`); `RefreshAppCheckboxes` on flip; two
> independent lists (no cross-contamination); legacy `ExcludedApps` sweep correctly
> skipped in exclude mode (`SaveSettings` :3847). r6 shell-verb inversion already
> fixed. **Two non-leak findings:** (1) stale comment `ConfigGenerator.cs:45-50`
> claims mode is INFERRED from list population — the code doesn't (reads
> `RoutingAppsMode` directly); cosmetic, fold into AF-1. (2) `== "exclude"`
> re-derived in ~5 scattered sites — exactly the scatter AF-1's enum collapses.

**The r6 zone.** Scope: apps include/exclude bridge (`GetActiveAppList` /
`SetAppCheckedInCurrentMode` / SaveSettings re-derive in `MainWindowViewModel.cs`)
+ the exclude-mode `ConfigGenerator` path. Lens (1, deep): can an app in
exclude-mode ever land routed-instead-of-bypassed (or vice-versa) on ANY path —
UI toggle, shell verb, migration, apply-reload. **Est: ~2-3M · ~15-20 agents ·
~15m.** Maps to **AF-1**.

### W1.3 — DNS-leak
> **SCOUTED 2026-05-29 → CLOSED (no fan-out).** All 4 lenses sound:
> (1) `hijack-dns` ALWAYS emitted — `BuildRoute:1276` adds `{Protocol:dns,
> Action:hijack-dns}` unconditionally as rule #2 (+ `LeakProtection:245` backstop).
> (2) smart vs vpn_only correct — vpn-dns `Detour=proxy`; local-dns is Cloudflare
> **DoH** (`type:https`, NOT `type:local` → no getaddrinfo/ISP leak even on the
> direct path) `Detour=dns-direct` (`BuildDns:827-844`); per-process rule
> smart→local-dns / vpn_only→vpn-dns (:877). (3) dns-direct gotcha handled —
> local-dns points at the non-empty `dns-direct` outbound (`udp_fragment:true`
> :980), never bare `direct` (the 1.13.3 FATAL). (4) per-process coverage correct
> in all 3 modes (full→Final vpn-dns · exclude→listed local-dns · include→routed
> vpn-dns/local-dns) + `LeakProtection:210-222` warns on gaps. **One intentional
> nuance** (not a bug): smart mode resolves a tunneled app's domains via DoH
> OUTSIDE the tunnel — documented (CLAUDE.md #5) + warned; default vpn_only routes
> DNS through proxy = safe.

Scope: `ConfigGenerator.cs` DNS section + `LeakProtection.cs` DNS checks. Lenses
(4): hijack-dns rule presence · smart vs vpn_only routing (local-dns vs vpn-dns
detour) · `detour:"dns-direct"` empty-direct gotcha · per-routed-process DNS-rule
coverage. **Est: ~2-3M · ~15-20 agents · ~15-20m.**

### W1.4 — Custom-config inject + 1.11→1.13 migration
> **SCOUTED 2026-05-29 → injection core SOUND (1 finding); migration = fan-out
> candidate.** Proxy-tag selection sound — `FindProxyOutboundTag` (:333)
> selector→urltest→first-proxy, and `ResolveOrAssignProxyTag` (:371) MUTATES an
> untagged outbound's tag so injected rules always reference a real tag (they
> explicitly handle "rule→missing tag→silent fall-through", :367-369). Format
> dispatch sound (`DetectActionFormat` :391; both branches add `action:route`
> correctly). `route.final` forced `direct` in split (`Inject` :139). **FINDING
> (LOW-MED) W1.4-a:** `RemoveInjectedProcessRules` (:1347) deletes EVERY route
> rule carrying a `process_name` field, not just VPNRouter-injected ones —
> idempotent re-inject works, but a user-authored `process_name` rule in the
> custom JSON is silently wiped; if VPNRouter's list doesn't cover that app it
> falls to `route.final` (direct in split) = leak-from-intent. "By design"
> (custom-mode contract: VPNRouter owns process_name rules) but undocumented
> footgun. Fix: marker-tag injected rules + remove only marked, OR Validate-warn
> on pre-existing process_name rules. **`StripUnsupportedFeatures` (985-1346, 361
> LOC) NOT exhaustively scouted** — 22+ `CustomConfigInjectorTests` + sing-box-check
> cover it, but arbitrary/malformed user-JSON migration is the real mini-workflow
> target (feed adversarial configs → assert valid + non-leaky). Unlike W1.1-1.3, a
> fan-out here earns its tokens.

**Self-contained (custom path).** Scope: `CustomConfigInjector.cs` (1355) +
`StripUnsupportedFeatures`. Lenses (3): inject idempotency (re-inject removes old
process rules) · action-vs-legacy format dispatch · migration completeness (DNS
type, `dns-direct`, geosite/geoip strip, `route.final`). **Est: ~3-4M · ~20-30
agents · ~20-25m.**

### W1.5 — LeakProtection backstop completeness  (run last)
> **SCOUTED 2026-05-29 → CLOSED. Backstop STRONG, 2 blind spots.** Catches:
> proxy-outbound existence (:235 ERROR), custom-mode proxy (:494), hijack-dns
> presence (:245), per-process DNS rule (:219), `dns.strategy=ipv4_only` (:188),
> `strict_route=false` + TUN addr (:192), placeholder/shadow-IP cross-check (:185),
> per-protocol outbound shape (VLESS/Hy2/TUIC/SS :249). **GAP-1 (LOW): route.final
> direction NOT validated** — only INFERRED (`isFullTunnel = Final=="proxy"` :226)
> to pick the DNS check; a wrong final for the mode wouldn't be flagged. **GAP-2
> (LOW, = CLAUDE.md #5): no exclude-mode awareness** — per-process DNS check (:203
> `Outbound=="proxy" || Action=="route"`) assumes include semantics; exclude-mode
> rules are action=route→outbound=direct, so it mis-fires (spurious "DNS may leak"
> on excluded apps) AND skips the exclude invariant. Both are "backstop won't
> catch a FUTURE regression", not live leaks. → fixable: tighten the proxy-routed
> predicate to `Outbound in {proxy,proxy-udp}` + add a route.final-vs-mode check.

**The net under W1.1–W1.4.** Scope: `LeakProtection.cs` (680). Meta-lens: for each
leak class surfaced by W1.1–W1.4, does `ValidateConfig` / `ValidateAppSettings`
actually catch it? List coverage gaps (e.g. smart-mode false-warning, CLAUDE.md
#5; exclude-mode polarity currently unvalidated). **Est: ~2M · ~12-15 agents · ~12m.**

**Staged total ≈ 11-16M (same ballpark; 5 independent pieces).**
**Locks:** silent traffic leak — the worst thing a VPN can do.

## W2 — Firewall + DnsLeakLockdown  ★ PRIORITY 2 (lock-out / data loss)

**Scope (~1.5k LOC):** `FirewallManager.cs` (944) + the netsh parser,
`block_on_vpn_fail`, DnsLeakLockdown enable/disable.

**Lenses (5):** netsh parse + locale (CO-5 wiped RU/DE/ES firewall rules) ·
rule-leak when VPN fails · lockdown-breaks-internet (brat r9-r18 saga) ·
data-loss on pre-existing user rules · cleanup-on-exit completeness.

**Find:** 1-2 rounds. **Verify:** 3 skeptics.
**Est: ~4-6M tokens · ~30-45 agents · ~25-35 min.**
**Locks:** wiping the user's firewall / locking them out of the internet.

## W3 — Update / install path  ★ PRIORITY 3 (blast = 100% of users)

**Scope (~2.7k LOC + scripts):** `UpdateChecker.cs` (1360),
`packaging/windows/install.ps1` (367), embedded `helper.cmd`, SelfRepair,
`SettingsMigrator.cs` (632).

**Lenses (5):** cmd/ps1 parser correctness (helper.cmd CMD-parser bug bricked
100% of v2.31.7 upgrades — delayed-expansion!) · semver/`-rN` version-compare ·
xcopy integrity / partial-update · migration data-loss · update-channel
(stable/experimental) handling. Cross-language (C# + cmd + ps1).

**Find:** 1-2 rounds. **Verify:** 3 skeptics.
**Est: ~6-9M tokens · ~40-60 agents · ~30-40 min.**
**Locks:** a broken updater that can't fix itself (the highest blast radius).

## W4 — sing-box lifecycle + close B3  ★ PRIORITY 4 (connection reliability)

**Scope (~4.3k LOC):** `SingBoxManager.cs` (1562), `HealthMonitor.cs` (766),
`TunAdapterDiagnostics.cs` (920), `VpnEngine.cs` (1027).

**Lenses (6):** Stop/Restart state races (**B3 — still open from v2.36**,
factor .NET issue #63328) · TUN-lock release (Task #53 surface) · crash-recovery
restart-loop · TUN-orphan cleanup (ERROR_FILE_EXISTS / wrong-adapter) ·
Resolve-before-Generate invariant · concurrent Stop/Dispose (B1/B2 regression
check).

**Find:** 2 rounds. **Verify:** 3 skeptics.
**Est: ~8-12M tokens · ~50-80 agents · ~40-50 min.**
**Locks:** internet-stuck-on-stop, restart loops, TUN orphan. Also the vehicle
to finally close B3 with a dedicated test suite.

---

## Totals

| | Tokens | Agents | Wall |
|---|---|---|---|
| W1 Leak (W1.1–W1.5, piecewise) | 11-16M | 75-110 | 5× ~15-25m |
| · W1.1 outbound/resolve/final | 2-4M | 15-25 | ~15-20m |
| · W1.2 include↔exclude polarity | 2-3M | 15-20 | ~15m |
| · W1.3 DNS-leak | 2-3M | 15-20 | ~15-20m |
| · W1.4 custom inject + migration | 3-4M | 20-30 | ~20-25m |
| · W1.5 LeakProtection backstop | 2M | 12-15 | ~12m |
| W2 Firewall | 4-6M | 30-45 | ~30m |
| W3 Update | 6-9M | 40-60 | ~35m |
| W4 Lifecycle | 8-12M | 50-80 | ~45m |
| **All four** | **~28-42M** | **~180-275** | **~2.5-3 h** (sequential) |

## Out of scope (deliberately skipped — low risk / low hit-rate)

UI/XAML, FreeConfigs pool, Zapret/TgProxy (recently churned + own MCP suites),
Android port, localization, tests, generated code. Sweep these only if a
specific incident points there.

## How to launch (end-of-week)

Per workflow: build the find→verify→synth script (same shape as
`v2380-context-menu-audit`), scope = the files above, `git diff` not needed
(full-file review — pass file list in CONTEXT). Run one, read result, decide
whether budget allows the next. Real findings → `-rN` fix + the missing test.
