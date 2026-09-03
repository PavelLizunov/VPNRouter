# Phase — Cold Startup Acceleration and Native Commands Optimization

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/perf-cold-startup-and-native-commands`
**Accepted base**: `origin/main` head `0feaa483`
**Roadmap ref**: Audit Wave 2 / Performance & Resource Optimization
**Effort**: 0.5 days
**Risk**: LOW
**Blast radius**: Desktop startup (`MainWindowViewModel.cs`, `ServiceViewModel.cs`, `MainWindowViewModel.AutostartBootstrap.cs`), DNS flusher (`DnsFlusher.cs`), Windows Firewall manager (`FirewallManager.cs`), and unit tests.
**Rollback**: revert branch commit; restore prior implementations

## Why

1. Desktop startup latency: `MainWindowViewModel` runs extensive eager initialization in Simple Mode:
   - `ServiceViewModel` spawns two `sc.exe query` processes synchronously during constructor, waiting up to 10s each.
   - `BootstrapAutostartAsync` executes `ServiceVm.Refresh()` 500 ms after start on the UI thread, freezing the UI.
   - `LoadApps()` eagerly parses `default.json` and `bypass-windows.json`, allocating hundreds of `AppGroupViewModel` and `AppItemViewModel` objects (~1.5–3 MB RAM) when the user only sees Simple Mode.
2. Windows DNS flush latency: `DnsFlusher.cs` executes `ipconfig.exe /flushdns` as a separate child process (40–120 ms latency, memory and handle churn).
3. Firewall startup latency: `FirewallManager.CleanupOrphanedRules()` iterates 3 prefixes and executes `netsh.exe advfirewall firewall show rule name=all dir=out` 3 times sequentially, parsing the complete ruleset 3 times (1.2–2.5s overhead).

## What

1. In `DnsFlusher.cs`:
   - On Windows, invoke native `dnsapi.dll!DnsFlushResolverCache()` (< 0.5 ms, 99% faster) with graceful fallback to `ipconfig.exe /flushdns`.
2. In `FirewallManager.cs`:
   - Implement `FindRulesByPrefixes(IEnumerable<string> prefixes)` to execute `netsh.exe show rule name=all dir=out` once and match all prefixes in memory.
3. In `ServiceViewModel.cs`:
   - Add `bool eagerRefresh = false` parameter (default `false`) so constructing `ServiceViewModel` does not spawn external `sc.exe` queries. Refresh on demand or when opening the Settings/Service tab.
4. In `MainWindowViewModel.cs`:
   - In `LoadSettingsIntoUI()`: defer `LoadApps()` when `IsSimpleMode` is active.
   - In `OnSelectedTabIndexChanged(int value)`: when navigating to Applications (tab 3), ensure apps are loaded.
   - In `MainWindowViewModel.AutostartBootstrap.cs`: remove synchronous SCM query on the UI thread.
   - Preserve `MainWindowViewModelCharacterizationTests` public API surface hash strictly unchanged.
5. Tests:
   - Unit tests covering `DnsFlushResolverCache` fallback and execution.
   - Unit tests covering single-pass `FindRulesByPrefixes`.
   - Contract tests for lazy service refresh and deferred app loading.

## How

1. Commit phase brief.
2. Implement DNS flush, firewall single-pass, and lazy subsystem loading.
3. Add unit tests in `VPNRouter.Tests`.
4. Multi-iteration verification (build/tests, Opus adversarial review, GitHub Actions CI).
5. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [ ] Gate 1 — Build clean: Release solution build completes with zero errors.
- [ ] Gate 2 — Tests green: all unit and characterization tests pass (0 failures).
- [ ] Gate 3 — Docs: outcome recorded and plans updated.
- [ ] Gate 4 — Adversarial review: Opus swarm review confirms no missing rules, no stale service state, and no surface drift.
- [ ] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

Pending execution.
