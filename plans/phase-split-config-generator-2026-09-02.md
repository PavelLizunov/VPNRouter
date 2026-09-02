# Phase — Mechanical split of ConfigGenerator god-file

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/split-config-generator`
**Accepted base**: `origin/main` head `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
**Roadmap ref**: matrix audit SU-1 god-file refactoring / `plans/v3.0-refactor-roadmap.md`
**Effort**: 1 day
**Risk**: LOW to MEDIUM (mechanical refactoring without behavioral alterations; strict invariants prevent regressions)
**Blast radius**: `VPNRouter.Core/Services/ConfigGenerator*.cs` and characterization tests; zero public API, settings, or wire-format changes
**Rollback**: revert branch commits; `ConfigGenerator.cs` remains monolithic on `origin/main`

## Why

`VPNRouter.Core/Services/ConfigGenerator.cs` is 2,173 lines long and serves as a monolithic generator combining top-level orchestration, rule-set cache management, custom routing/DNS rule translation, full DNS configuration, inbounds, outbound group election, transport/TLS builders, and routing rule construction. This monolithic structure hinders maintainability and concurrent review. Splitting into focused `partial class` files improves readability and navigability without altering execution semantics.

## What

- Partition `ConfigGenerator.cs` into six cohesive `public static partial class ConfigGenerator` files:
  1. `ConfigGenerator.cs`: Entry point `Generate`, `Serialize`, `SingBoxOptions`, MTU/stack/process normalization helpers, `BuildInbounds`. (~300 lines)
  2. `ConfigGenerator.Rules.cs`: AdBlock rule-set and route/DNS rules, CustomRules compilation and insertion, macOS helper process expansion, and Russian geo bypass. (~565 lines)
  3. `ConfigGenerator.Dns.cs`: `BuildDns`, `BuildVpnDnsServer`, DoH URL parsing helpers, and bare LAN TLD filter. (~265 lines)
  4. `ConfigGenerator.Outbounds.cs`: Top-level `BuildOutbounds` orchestrator and group dispatcher. (~355 lines)
  5. `ConfigGenerator.OutboundBuilders.cs`: Protocol-specific outbound and endpoint builders (VLESS, Hysteria2, TUIC, Shadowsocks, NaiveProxy, AmneziaWG), transport config, and TLS config. (~495 lines)
  6. `ConfigGenerator.Route.cs`: Top-level `BuildRoute` and `SlipstreamProcessName`. (~180 lines)
- Preserve every member's visibility, exact signature, parameter names, comments, and method body verbatim.
- Maintain case-sensitive `process_name` logic, DNS fail-closed rules, detour-chain invariants, and sing-box schema output byte-for-byte.
- Zero package, dependency, public interface, or behavioral modifications.

## How

1. Establish a clean brief-only baseline in GitHub Actions PR CI against `origin/main`.
2. Add deterministic characterization tests asserting byte-for-byte serialized JSON parity across diverse scenarios (VLESS flow split, Hysteria2 Brutal/Obfs, AmneziaWG endpoint in exclude mode, and Chained Detour).
3. Add a source-integrity test verifying every member method and field from the monolithic baseline exists exactly once across the partial files.
4. Use `gemini-swarm` (`ninitux/gemini-3.8-flash-high`) to mechanically carve and emit the 6 partial-class files cleanly.
5. Verify member preservation, reassembly integrity, and compilation without warnings.
6. Run adversarial bug-hunt / Opus review on the split diff.
7. Verify all CI checks on GitHub Actions (Ubuntu test suite, Windows characterization, Go test, Android APK build).

### Tests written

- Deterministic baseline characterization tests asserting byte-for-byte identical output for all proxy protocols, DNS configurations, and routing modes.
- Member completeness and source-integrity tests ensuring zero lost or modified declarations.
- Existing 42 test classes covering `ConfigGenerator` continue to pass without modification.

### Verification approach

Run focused `ConfigGenerator` unit tests, full discovered test suites on Ubuntu and Windows, Android build compilation, and characterization tests. GitHub Actions is the mechanical oracle. No live VPN processes are manipulated.

## Verification gate

- [ ] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors or newly introduced compiler warnings.
- [ ] **Gate 2 — Tests green**: all existing tests and new characterization tests pass with zero failures.
- [ ] **Gate 3 — Docs**: phase outcome recorded with exact file line counts and commit SHAs; `plans/` updated.
- [ ] **Gate 4 — Self-review**: mechanical diff review verifying zero logic or casing drift across all extracted partial classes.
- [ ] **Gate 5 — UI verify**: N/A (pure Core logic split; no UI surface modified).
- [ ] **Gate 6 — Characterization diff**: characterization and round-trip tests confirm byte-for-byte JSON output equivalence before and after split.

## Outcome

**Status**: IN PROGRESS
**Commits**: brief commit pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results**: pending.
**Surprises encountered**: pending.
**Follow-ups spawned**: pending.
**Lessons for methodology doc**: pending.
