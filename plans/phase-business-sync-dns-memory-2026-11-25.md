# Business shared DNS and memory backport

- Branch: `dsh/business-sync-dns-memory-20261125`
- Base: `origin/business` at `3bfe504b` (PR #741)
- Risk: High. DNS policy and portal routing, process ownership, and tray lifecycle have edition-specific contracts.
- Rollback: Revert the backport on `business` as a unit.

## Why

Bring shared DNS configuration, memory lifetime, and memory usage fixes to the Business line while preserving its managed routing and desktop behavior.

## What

- Port shared `Dns.LocalDns` handling from `54a93484` (#739).
- Port applicable desktop and Core lifetime fixes from `e4614529` (#735).
- Port runtime GC configuration, tray trimming, polling, and RAM diagnostics from `818d35b9` (#737).
- Verify dead-code removals in `fb94881c` (#738) against the entire Business tree before applying them.
- Preserve Business portal and policy routing, managed window and repair behavior, and both desktop edition build paths. Exclude #740, #736, and GitHub workflows.

## How

Read the source patches and compare them with the Business implementation. Reconcile overlapping changes at their existing entry points, port applicable regression tests, and audit the combined tree before committing. Record edition-specific decisions and omitted hunks in the outcome.

## Verification

1. Build: GitHub CI only. Review the default and Business compile inputs and Windows and Ubuntu policy matrix before handoff.
2. Tests: Port relevant regressions and review current DNS, policy, lifecycle, tray, and repair contracts. Test execution belongs to GitHub CI.
3. Documentation: Complete this outcome, run `git diff --check`, and parse touched project and Avalonia XML.
4. Review: Trace configuration and lifetime changes against their callers and record the verification limit for this implementation assignment.
5. UI: Review both tray window paths statically. Rendering and UI MCP build belong to GitHub CI.
6. Characterization and integration: Check public surface, compile conditions, references, and unchanged workflow inputs.

## Outcome

### Ported changes

- `54a934849b75abbc08128788e04d3bf05898a582` (#739): Ported all five DNS source/test files. Non-default `Dns.LocalDns` applies to the direct resolver and custom JSON replacement or synthesis. The encrypted legacy default remains the same. Added hostname-bootstrap assertions to the Business portal transport test for split and full mode. Business site and geo precedence, the portal DNS detour, reverse mapping, and the portal UDP reject continue to use the existing Business policy paths.
- `e4614529d2be94d03e24acb9e20829aac91977be` (#735): Ported the shared UI, log-tail, ETW, WMI, process polling, diagnostics, free-config, HTTP client, and engine lifetime changes with upstream regressions. The Business diagnostic log path, staged deep-verifier executable, policy launch acknowledgement, and tray labels remain in use. Kept the desktop stop check before draining policy work and disposed the engine after the drain. The ETW worker retains ownership until exit, including a stop during setup. Skipped the absent Standard assistant repair partial.
- `818d35b93812bc629112473fd43b92d4d073a9a3` (#737): Ported GC runtime options to App, Headless, and Service, the hidden-window poll interval, bounded delayed trimming, and RAM telemetry. The trimmer takes an Avalonia `Window` and traverses the logical and visual trees for inactive deferred pages. Standard pages rehydrate on activation. The eager Business window keeps its repair state and content and shares cache trimming and runtime refresh on restore. The tray regression covers both window types.
- `fb94881cd7aa9ede5e0751559dc8adfe3997b2be` (#738): Ported verified dead members, localization forwarders and keys, and `hk-divider`. The ETW write-only field was removed with the port of its lifetime changes. Other declarations named `AutoSelectStatus` and `CountryFlag` still serve Business XAML and tests. Kept `MacFirewallManager.IsAnchorMode` because `MacFirewallManagerTests` calls it. Also kept the in-use reduced-motion helpers in `StatusEmblem` and `SimplePage`. Skipped absent `RollbackHistory`, `AssistantApply`, `AssistantStepLimit`, `MacPrivilegePaths`, and `MacPrivilegedClient` hunks.

Source plans were represented by this outcome. Updated the three existing Business lifecycle findings in the defect ledger. PR #740, PR #736, and workflow changes were excluded.

### Verification

1. Build: Pending GitHub CI. Read the Standard desktop inputs, Business shared-update validation and edition define, Windows and Android compile conditions, Core and App friend assemblies, and the unchanged workflow build inputs.
2. Tests: Pending GitHub CI. Reviewed the affected ConfigGenerator, custom import, Business DNS, portal, policy, repair, and window contracts against their source entry points. The new Business portal assertions fall under the existing Ubuntu and Windows policy matrix filter. The Standard test job includes the new shared DNS and tray regressions.
3. Documentation: Static pass. `git diff --check` and staged whitespace checks passed. Python ElementTree parsed the three touched project files and `HomeKit.axaml`. Task Markdown additions are ASCII.
4. Review: Source audit completed for this implementation handoff. Independent review remains pending. Full-tree identifier scans included script and XAML consumers and distinguished same-named members on different types.
5. UI: Both tray paths were checked in source for startup hidden, close-to-tray, minimize fallback, restore refresh, and shutdown. UI tests and UI MCP execution are pending GitHub CI.
6. Characterization and integration: No changed declared public member or generator attribute in the shared ViewModel partials. Conditional directives balanced in all 63 touched C# files. Checked cross-project signatures and caller references, and confirmed an empty workflow diff.

The base was `3bfe504b`. At the pre-push check, its Standard test, Ubuntu and Windows Business policy, Business surface, and UI MCP build jobs were green. All builds and tests for this branch are left to GitHub CI. Patch reject files were removed after reconciliation. The active worktree and task branch remain available for PR integration.
