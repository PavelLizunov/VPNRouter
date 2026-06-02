# Follow-up regression review — v2.40.0-r1 (combined v2.39+v2.40), 2026-06-02

Second adversarial workflow over `v2.38.2..e44c543` AFTER the 15 review fixes
landed — hunting for regressions the fixes themselves introduced. 7 reviewers
(one per changed surface) -> each finding adversarially verified by 2 independent
skeptics (refute-biased) -> synthesis. 16 agents, ~2M tokens.

**4 raised, 2 confirmed, 2 dropped by verify.** Both confirmed are fixed in
v2.40.0-r2.

## Confirmed + FIXED (2)

### #1 [MEDIUM · regression_of_fix=TRUE · 2/2 on re-read] M5 ScrubRoutingForApp over-removes a shared process name
`VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs` (ScrubRoutingForApp),
reached from RemoveCategory / RemoveCustomApps / RemoveCustomApp.
The M5 fix (e44c543) made the row-removal scrub do `RemoveAll(Match)` on BOTH
RoutingAppsInclude and RoutingAppsExclude unconditionally. The same process name
can exist as separate AppItems across groups (shell-verb add + a profile group;
dedup is within-group only), and `SaveSettings` never rebuilds those routing
lists from VM state — so removing ONE group silently un-routed an app the user
still had checked in ANOTHER group (leak-from-intent in reverse: a wanted route
dropped, or a still-wanted exclude entry removed). Zero test coverage.
**Fix (v2.40.0-r2):** new pure helper `RoutingAppListEditor.IsStillRoutedByAnother`
(name + .exe-insensitive, case-insensitive); ScrubRoutingForApp now passes the
names of all OTHER still-checked AppItems and only RemoveAll when none match.
item.IsChecked is set false first, so it's excluded. +6 unit tests.

### #2 [LOW · regression_of_fix=FALSE · pre-existing] Include-split per-app DNS falls back to local resolver
`VPNRouter.Core/Services/CustomConfigInjector.cs` (InjectDnsRules / FindRemoteDnsTag).
H1 wired `EnsureSynthesizedRemoteDns` only into the `dns.final` path, not into the
per-app `InjectDnsRules(useRemoteDns:true)` call. When a custom config carries no
proxy-detour DNS server, the per-app rule for tunnelled include-apps fell back to
`servers[0]` (a local/dns-direct resolver) -> those apps' DNS leaked. Predates
v2.39 (old code had the same servers[0] fallback); H1 simply didn't extend to it.
**Fix (v2.40.0-r2):** InjectDnsRules now takes `proxyTag` and, when
`useRemoteDns && FindRemoteDnsTag==null && proxyTag present`, synthesizes the same
Cloudflare DoH-via-proxy server H1 uses, instead of the local fallback. +1 test
(include-split per-app DNS rule resolves through proxy).

## Dropped by adversarial verify (2 — NOT bugs)
- LOW EnsureSynthesizedRemoteDns idempotency ignores detour -> tag-collision re-leak:
  refuted — the synth tag `vpnrouter-vpn-dns` is ours; a user config naming a
  direct-detour server with that exact tag is not a reachable scenario.
- LOW N1+L2 removes the deep-verify count cap -> unbounded libbox spin-ups:
  refuted — the Android verify loop is bounded by target/exhaustion; no cap was
  removed by the N1 status-text / L2 dedup change.

## Verification
Desktop build 0 errors; affected suites 68/68 (RoutingAppListEditor incl. 6 new
IsStillRoutedByAnother + CustomConfigInjector incl. new include-split synth + H1
sing-box checks); full logic suite 1544/0. MVM characterization unchanged
(ScrubRoutingForApp is a private body change).
