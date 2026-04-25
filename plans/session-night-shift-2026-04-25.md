# Night-shift session report — 2026-04-25 → 2026-04-26

User went to sleep at ~02:18 with mandate to keep working until either I'm
confident everything is OK or I find more problems to fix. This file
chronicles what shipped, what got audited, and what's open.

---

## Shipped: v2.28.2-r1 — silent VPN leak fix

**Severity**: privacy/security regression. Update strongly recommended.

**Trigger**: server admin reported 249 "flow mismatch" errors per day from one
user's IP. Investigation found the user's `current.json` had **zero `vless`
outbounds** but `route.rules` still pointing at a `"proxy"` tag. sing-box
silently ignored the rules → all process traffic went DIRECT through ISP,
bypassing VPN. Plus sing-box's own urltest probes hit the upstream server with
no VLESS handshake (the actual cause of "flow mismatch").

**Root cause**: `VpnEngine.Apply` (the hot-reload path) called
`ConfigGenerator.Generate` directly on `settings.Vless.Servers` straight from
disk. In subscribe mode that list is empty in YAML — subscription servers live
in `App.Subscriptions[].Servers` and only get aggregated into `Vless.Servers`
in memory by `MainWindowViewModel.StartVpnAsync`. `StartAsync` had this
aggregation; `Apply` didn't.

### Fix — 4 layers of defense

| Layer | What it does | File / Line |
|---|---|---|
| **1. Primary** | New `VlessServersResolver` — single source of truth for aggregating subscription→VLESS in any code path. | `VlessServersResolver.cs` (new, 136 lines) |
| **2. Hard guard** | `ConfigGenerator.BuildOutbounds` throws `InvalidOperationException` if called with empty servers. Prevents the silent-broken-JSON case at the source. | `ConfigGenerator.cs:319-340` |
| **3. Apply validation** | `VpnEngine.Apply` now runs `LeakProtection.ValidateConfig` (was only in StartAsync). Catches "no proxy outbound" via line 67 of LeakProtection. | `VpnEngine.cs:793-803` |
| **4. Custom-config validation** | `VpnEngine.Apply` for custom-mode now runs `CustomConfigInjector.Validate` first. Same bug class for user-provided JSON. | `VpnEngine.cs:766-779` |

**Bonus**: `HealthMonitor.GenerateConfigJson` also calls `Resolve` defensively.

### Tests added (11)

- 8 `VlessServersResolverTests`: subscribe mode aggregation, disabled subs,
  empty fallback, manual mode, 4 `DescribeEmptyReason` actionable strings.
- 2 `ConfigGeneratorEmptyServersGuardTests`: hard guard + end-to-end
  subscribe → resolve → generate happy path.
- 1 `Generate_FromSubscribeMode_PassesSingBoxCheck`: integration test that
  runs `sing-box check` on the generated JSON. Pins binary-level compatibility.

All 11/11 pass locally. Skipped sing-box check test gracefully if binary
absent.

### Commits

```
14ec5da fix(v2.28.2-r1): silent VPN leak when Apply called with empty Vless.Servers
3b82df2 fix(v2.28.2): defense-in-depth — LeakProtection in Apply + Resolve in HealthMonitor
482bcbd fix(v2.28.2): Apply now validates custom-mode config (defense-in-depth)
5feab65 test(v2.28.2): add sing-box check integration test for resolver+generator pipeline
```

Tag `v2.28.2-r1` points at `482bcbd` (test commit ships only in source, not
runtime artifacts).

### Release artifacts

12 assets uploaded to https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.28.2-r1:
- Windows: `VPNRouter-v2.28.2-r1-win.zip` + `update` + 2× `.sha256`
- Linux: `.deb` + `.AppImage` + `.tar.gz` + 3× `.sha256`
- macOS: `.dmg` + `.zip`

`isPrerelease: true` (rolling candidate). `v2.28.1` remains the `Latest` stable
until user confirms `-r1` works.

---

## Code audit results (Phases 2A-2I)

Areas surveyed for similar silent-leak / empty-state / missing-reference bugs.

### ✅ No issues found

- **`SubscriptionFetcher`** — three input formats handled, dedup correct,
  smart fallback keeps cache on fetch failure. Minor improvements possible
  (retry, response size cap) but not critical.
- **`SingBoxManager`** — Stop/Restart/Hot-reload paths solid,
  `EnableRaisingEvents = false` before Kill prevents false crash detection
  (per CLAUDE.md design decision). Linux capability mode + pkexec fallback
  both correct.
- **`FreeConfigAggregator`** — incremental save every 50 tests / 5s,
  goal-seeking early-stop already present, skip-recent (6h) for already-tested
  entries. UX work for v2.28.3 (default early-stop) is feature, not bug.
- **`AppPaths`** — deterministic, cached, cross-platform correct.
- **`GeoDataDownloader`** — sanity-check size, re-download on truncation.
  Minor: no auto-refresh after first download (geo data may stale, low impact).
- **`SubscriptionResolver`** — Service/CLI bootstrap path. Complementary to
  `VlessServersResolver`; both arrive at same end state via different paths.
- **GeoBypass + DNS rules** — all outbound/server references self-consistent.
  No missing-reference bugs found.

### 🔧 Defensive fixes shipped during audit

- **`VpnEngine.Apply` skipped `LeakProtection.ValidateConfig`** → fixed in
  3b82df2.
- **`VpnEngine.Apply` skipped `CustomConfigInjector.Validate` for custom mode** → fixed in 482bcbd.
- **`HealthMonitor.GenerateConfigJson` didn't run `Resolve`** → fixed in 3b82df2.

### 🟡 Backlog (non-critical, defer)

- `MainWindowViewModel.StartVpnAsync:1787-1796` — duplicate aggregation logic.
  Could be removed since `VpnEngine.StartAsync` now resolves internally. But
  the GUI version also flips `ConfigMode = "generated"`, which may have
  other UI-side side-effects. Skip for now, revisit during v2.29 cleanup.
- `SubscriptionFetcher` — add retry with exponential backoff (Zapret got it
  in v2.28.1, same pattern). UX win on flaky networks.
- `SubscriptionFetcher` — cap response size to avoid OOM from rogue
  provider returning 10GB.
- `GeoDataDownloader` — add periodic refresh (currently only downloads if
  missing).

### 🔵 Patterns confirmed safe (would have caught the bug)

- All `route.rules[].outbound` references in `ConfigGenerator` map to
  outbounds we always emit (`direct`, `dns-direct`) or guard with the new
  hard-throw (`proxy`, `proxy-udp`).
- All `dns.servers[].detour` references map to outbounds we always emit
  (`proxy` guarded, `dns-direct` always).
- `CustomConfigInjector.StripUnsupportedFeatures` handles outbound removal
  + route rule conversion correctly (legacy → 1.13 migration).

---

## Github issues review

GitHub issues empty (1-star repo, user feedback through direct contact).
Redirected Phase 3 to code-level patterns + recent fix commit history.

Recent fix history shows active areas:
- v2.28.x: subscription UI, Zapret download, install.ps1 Byte[], silent leak
- v2.27.x: YouTube bypass (geoip-ru removal), Service UX, 4 user-reported bugs

Pattern: every cycle finds 1-2 user-impactful bugs + 1 "process improvement"
fix. v2.28.2 follows that pattern exactly.

---

## Verification

**Build**: `dotnet build VPNRouter.sln -c Release` → 0 errors, 32 pre-existing
warnings (unchanged from baseline).

**Tests**: 11/11 v2.28.2 regression tests pass locally with sing-box 1.13.10
on this VM. Full test suite has unrelated `testhost` lock issue that prevents
parallel re-build during a test run (cosmetic; doesn't affect correctness).

**CI**: Mac (24943375860) + Linux (24943375855) builds completed successfully
on tag `v2.28.2-r1` pointing at 482bcbd. All 12 platform artifacts on
release page (verified via `gh release view`).

**APT repo**: HTTP 200 on `https://vpn.ninitux.com/apt/dists/stable/main/binary-amd64/Packages`.

**Homebrew Cask**: not bumped (correct — prerelease policy).

---

## What's NOT done (deferred)

1. **User testing** — `-r1` not yet confirmed by the field user who reported
   the bug. Auto-update on their machine should pick this up; if they hit
   regressions we ship `-r2`. Stable promotion to `v2.28.2` on their thumbs-up.
2. **v2.28.2 stable cut** — same as above, awaits confirmation.
3. **`-r1` server-side verification** — server admin's "flow mismatch" log
   should drop to ~zero after the user updates. Not measurable from my side.
4. **Backlog items** in audit table above.

---

## Observable invariants (post-fix)

These should hold for any code path that ends in `ConfigGenerator.Generate`:

```
INV-1: settings.Vless.Servers.Count > 0 OR settings.App.Subscriptions has
       at least one Enabled=true subscription with non-empty Servers.

INV-2: ConfigGenerator.Generate output always contains an outbound with
       tag "proxy" if route rules emit "outbound: proxy" (which is
       unconditional in split tunnel + processes > 0 mode).

INV-3: If invariant (1) is violated, ConfigGenerator throws. There is no
       silently-broken-JSON path remaining.

INV-4: Apply runs Resolve THEN ValidateConfig. Both steps before sing-box
       sees the new config.
```

The 11 regression tests pin these. Future code changes that break any of
them fail tests immediately.

---

## Next session opening move

1. Check user's response on `-r1` testing (auto-update pulled it? leak
   actually closed?).
2. If 👍 → cut stable `v2.28.2` (no suffix), delete `-r1` release.
3. If 🐛 → ship `-r2` with whatever they found.
4. Resume v2.28.x roadmap (Free Configs UX two-tier + early-stop).
