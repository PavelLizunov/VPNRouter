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

**Scope (~5k core LOC, reads ~8-10 files):**
- `VPNRouter.Core/Services/ConfigGenerator.cs` (1395)
- `VPNRouter.Core/Services/LeakProtection.cs` (680)
- `VPNRouter.Core/Services/CustomConfigInjector.cs` (1355)
- `VPNRouter.Core/Services/VlessServersResolver.cs`
- apps include/exclude bridge: `GetActiveAppList` / `SetAppCheckedInCurrentMode`
  / SaveSettings re-derive in `MainWindowViewModel.cs` + the exclude-mode
  `ConfigGenerator` path

**Lenses (6):** route-rule correctness · proxy-outbound presence (v2.28.1 silent
leak) · DNS-leak (hijack-dns / smart vs vpn_only / detour) · include↔exclude
polarity (r6 was here) · 1.11→1.13 migration (StripUnsupportedFeatures) ·
`route.final` direction.

**Find:** 2 rounds (dense/critical). **Verify:** 3 skeptics. **Synth:** + critic.
**Est: ~10-15M tokens · ~60-90 agents · ~40-50 min.**
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
| W1 Leak | 10-15M | 60-90 | ~45m |
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
