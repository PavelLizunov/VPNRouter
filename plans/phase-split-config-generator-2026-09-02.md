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

- [x] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors in PR workflows `33671409899` and `33671785072`.
- [x] **Gate 2 — Tests green**: baseline `2830 total / 2773 executed` became `2835 total / 2778 executed`, all passed with zero errors and zero warnings; Windows characterization passed `24/24` with zero failures.
- [x] **Gate 3 — Docs**: this outcome recorded with exact file line counts and commit SHAs; `plans/` updated.
- [x] **Gate 4 — Self-review**: dual Opus adversarial review confirmed zero P0/P1 blockers, zero signature drift, zero casing drift, and complete using/compilation safety across `net10.0` and `net10.0-android`.
- [x] **Gate 5 — UI verify**: N/A (pure Core logic split; no UI surface modified).
- [x] **Gate 6 — Characterization diff**: source-integrity test asserts all 50 member declarations are present; four structural characterization scenarios and 42 existing test classes confirm byte-for-byte fidelity and zero behavior drift.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR #215 remains open and unmerged
**Commits**: `993b6a26` (brief); `8b012989` (mechanical split); `2ade0c19` (test fixture alignment); `7b49fa0d` (QUIC test assertion correction)
**Pushed**: `origin/dsh/split-config-generator`; PR #215 — https://github.com/PavelLizunov/VPNRouter/pull/215
**Test deltas**: +5 tests (`2835 total / 2778 executed / 2778 passed / 0 failed / 0 warning`); Windows characterization `24/24` passed
**Files changed**:
- `VPNRouter.Core/Services/ConfigGenerator.cs`: 345 lines (orchestrator, serialize, inbounds, tun/process helpers)
- `VPNRouter.Core/Services/ConfigGenerator.Rules.cs`: 562 lines (AdBlock, custom rules, macOS helper expansion, geo bypass)
- `VPNRouter.Core/Services/ConfigGenerator.Dns.cs`: 264 lines (BuildDns, BuildVpnDnsServer, DoH parsing helpers)
- `VPNRouter.Core/Services/ConfigGenerator.Outbounds.cs`: 356 lines (BuildOutbounds, group orchestration)
- `VPNRouter.Core/Services/ConfigGenerator.OutboundBuilders.cs`: 497 lines (VLESS, Hy2, TUIC, Shadowsocks, Naive, AWG, transport, TLS)
- `VPNRouter.Core/Services/ConfigGenerator.Route.cs`: 185 lines (BuildRoute, SlipstreamProcessName)
- `VPNRouter.Tests/ConfigGeneratorSplitCharacterizationTests.cs`: 397 lines (member integrity + 4 scenario characterizations)
- `plans/phase-split-config-generator-2026-09-02.md`: this phase brief and outcome record

**Gate results**: All gates passed on commit `7b49fa0d` (workflow `33671409899`) and verified in independent repeat workflow `33671785072`.

**Surprises encountered**:
- In characterization `Scenario3`, testing AmneziaWG endpoint generation on a runner without `sing-box-lx` required initializing `SingBoxFeatures.OverrideAwg = true;` with `[Collection("SingBoxFeaturesSerial")]`, matching `AmneziaWgEndpointTests.cs`.
- In characterization `Scenario1`, `GetActiveServers()` filters same-host siblings by IP match (`s.Server == active.Server`), requiring identical host IP for dual flow/noflow group generation. Also confirmed that QUIC reject is intentionally omitted when `hasUdpProxy` is true, reflecting deliberate UDP proxying.

**Follow-ups spawned**: None for `ConfigGenerator.cs`. Next matrix task may proceed on subsequent god-files (`VpnEngine.cs`, `MainWindowViewModel.cs`).
**Lessons for methodology doc**: Exact byte-slice validation combined with source-integrity assertions guarantees 100% mechanical faithfulness when decomposing god-files without logic regressions.
