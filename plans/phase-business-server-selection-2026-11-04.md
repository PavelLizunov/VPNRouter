# Business server selection after office outage

- Branch: dsh/business-pilot; accepted base: 41b8d6f2e369304862555f8baf41284e9d4efb48.
- Risk: MEDIUM; selection changes the VPN endpoint used by the existing engine.
- Scope: Business window, preset selection persistence, shared localization,
  focused tests, private Windows packaging and fixed WINBRAT verification.
- Rollback: retain the previous private installer and revert this scoped change.

## Why

The owner reported office-wide loss of VPN access on November 3 and requested a
manual server selector. Monitoring recorded a transient failure to reach the
selected node's VPN and SSH ports near 18:44 Moscow time. Its VPN process did
not restart; this does not establish the customer's exact failure cause.
The current window has no selector, and BusinessDefaults.Apply overwrites the
saved active server on every application launch.

## What and how

Reuse Servers, SelectedServer, settings persistence and the existing connection
command. Add a localized selector for bundled endpoints. Disable changes while
connected/connecting; the employee disconnects, chooses a server and reconnects.
Preserve a saved choice only when it resolves to the trusted embedded preset;
otherwise retain the existing usable VLESS default. Refresh embedded credentials
and routing as before. Do not add imports, free configs or a second VPN engine.

Cover selection persistence, missing-choice fallback, generated endpoint/routing,
busy-state controls and narrow layout. Review correctness, secret/trust boundaries
and coverage independently. Package the same account privately through exact-SHA
Windows CI, then verify two different server endpoints and app restart on WINBRAT.
Keep real credentials/configuration and expanded host details outside Git.

## Verification and outcome

1. Build: BLOCKED pending implementation SHA and exact-SHA CI.
2. Tests: BLOCKED pending focused regressions and full applicable CI gate.
3. Docs: IN PROGRESS; update employee switching instructions and this outcome.
4. Review: BLOCKED pending scoped independent bug-hunt.
5. UI: BLOCKED pending headless renders and the actual WINBRAT scenario.
6. Characterization: existing public ViewModel surface must remain unchanged.

Status: ACTIVE. Baseline is clean and all eleven applicable PR checks passed;
the two private artifact jobs were intentionally skipped in public CI.
No workstation/control-plane SDK install, build or VPN execution is authorized.
This private Business update does not request a public release, merge or tag.

## Implementation and review before CI

- Baseline brief 3c999b9c passed all eleven applicable checks; implementation
  started only after its full gate completed.
- Business window reuses the existing server rows and connection command. An
  internal selection action validates membership/busy state, saves immediately
  and surfaces a localized generic write error. No public ViewModel API changed.
- Preset bootstrap preserves the saved physical node within refreshed embedded
  credentials. Missing names fall back to its default; blank/duplicate labels
  fail before settings are changed. The picker offers one direct VLESS node per
  host while the complete protocol/UDP sibling pool remains in the engine.
- Source-only independent reviews: business_server_correctness,
  business_server_trust and business_server_coverage. Name ambiguity and sibling
  semantics were confirmed, recorded, then fixed; same-host and label regressions
  supplement the original different-host selection tests.
- Existing private corporate API inspected without printing configuration:
  10 entries, 3 physical VLESS hosts, zero blank or duplicate labels.
- Tests also cover immediate save, restart provisioning, refreshed credentials,
  removed choice, generated primary/UDP host, foreign rows, busy controls, save
  failure, Russian labels and widths 340/420. Runtime and disk serialization
  still require the private Windows build and WINBRAT scenario.
- Lightweight XAML XML parse and diff whitespace check pass. No local compiler,
  SDK install, package build or full test execution was used.
- Worker pool is currently idle; no lease has been acquired for this update.
