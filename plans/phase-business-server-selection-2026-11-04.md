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

1. Build: PASS at installer source 8f874598; private Windows CI 41727744978.
2. Tests: PASS; Linux 3272/78 skipped, Windows 3441/21 skipped, Business 33/1 skipped.
3. Docs: PASS; employee checklist and this outcome describe manual switching.
4. Review: COMPLETE with three source-only correctness/trust/coverage lenses;
   confirmed name/sibling findings were fixed and regression-tested.
5. UI: PASS; synthetic widths 340/420 inspected, full scoped WINBRAT scenario passes.
6. Characterization: PASS; existing public ViewModel surface remains unchanged.

Status: IMPLEMENTED AND LIVE VERIFIED. Installer source 8f874598 passed all eleven
applicable PR checks; the two private jobs skip normally in public CI. The final
records commit still requires its own exact-head CI gate. No public tag, release
or merge is requested. No workstation/control-plane compilation occurred.

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
- Worker availability was checked before acquiring the canonical WINBRAT lease.

## Initial CI correction

At 6bc2f846 the shared build and private installer job 41726615434 succeed.
Public Windows CI 41726474747 passes 31 Business fixtures and skips the private
fixture, but both narrow/wide window cases stop at an obsolete all-descendants
TextBox assertion. The pinned Avalonia 12.1.3 Fluent ComboBox contains a hidden
PART_EditableTextBox even when its editable mode is false. Verify that the
selector is noneditable and that no input is effectively visible, preserving
the employee-facing invariant. Production App/Core source is unchanged by this
test correction. Repeat the exact-SHA gate before delivery.

The same two fixture failures are the only failures in the Linux full run
41726474704: 3270 passed, 2 failed, 78 skipped. The pinned template source is
[Avalonia 12.1.3 ComboBox](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Themes.Fluent/Controls/ComboBox.xaml).

## Live verifier preparation

At 8f874598 all eleven applicable PR checks pass. Linux full tests: 3272 passed,
78 skipped; Windows characterization/full run: 3441 passed, 21 skipped; Business
surface: 33 passed, one private fixture skipped. Private installer run 41727744978
succeeds at the exact source SHA. Both synthetic headless widths (340 and 420)
were visually inspected; the selector, modes and footer fit without clipping.

The first two operational runs stop before installation and clean up successfully.
Windows PowerShell 5.1 wraps ConvertFrom-Json array pipeline output as one item
inside the verifier's array subexpression. A separate assigned-value check sees
all three prepared choices. Correct the task-owned verifier assignment; no
product source or installer change is indicated by this preparation failure.

Private Windows CI capacity before packaging: 4 logical CPUs, 6 percent CPU,
14040340 KiB available RAM, 30.57 GiB free disk, zero competing build jobs;
SDK list includes 10.0.403; the 2944 MiB page file has zero current use.
WINBRAT before the scenario: 8 logical CPUs, 1 percent CPU, 13498 MiB free RAM,
zero swap use, 115.8 GiB free on C:, no competing build/VPN processes, no Business
data or TUN. Windows PowerShell 5.1 and the Framework runtime were present;
no SDK installation or compilation occurred there. The worker lease uses the
canonical sequential pool and must be released after task-owned cleanup.

UIA diagnostics distinguish visible Text labels from selectable containers.
Both the short-label ListItem query and a full-row-name retry fail across separate
helper invocations. The passing adapter expands the selector, verifies all three
visible prepared labels, resolves the SelectionItemPattern container through its
child label, selects it and collapses the popup in one interactive invocation.
The precise cause of the separate-operation failure was not established. Only a
task-owned copy of the canonical helper is extended; the tracked verifier and its
exact source copy remain unchanged. No live desktop was captured.

## Final live outcome and delivery

- [Sanitized WINBRAT report](business-server-selection-winbrat-2026-11-04.json):
  all twelve required stages PASS, no failure; UIS and absence of memory leaks
  remain explicitly UNVERIFIED.
- The original personal installer is installed, bootstraps without imports,
  connects/stops and is upgraded in place to the new installer. Corporate UUID
  and full routing-mode preservation are checked privately.
- All three choices are visible. Two physical nodes are actually exercised.
  Selecting node two is saved before connection, survives a complete app process
  restart, and becomes the generated TCP proxy target. Three split/full/split
  cycles connect and stop with TUN/core cleanup. Representative IPv4 HTTPS egress
  confirms ordinary curl Direct in split, the selected Telegram-named probe Proxy,
  full-mode Proxy, and a different VPN egress after switching nodes.
- This does not verify every destination/process rule, every DNS path, customer
  UIS calls, real office reachability or automatic failover. Short process samples
  are recorded without declaring memory leaks fixed or absent.
- Executed orchestration: [scenario](business-server-selection-live-verify-2026-11-04.ps1)
  plus [task-only UIA extension](business-server-selection-uia-extension-2026-11-04.txt).
  Stage the latter as selector-uia-extension.txt in the scenario WorkDir. The
  private choices file is administrator-owned and must not be committed.
- Installer SHA256: e1d9b224bb10678ceb04622bc16a1e92ba62e7159ff3faf1fa96a446556e6bb8;
  62991068 bytes. Both native core split/full configuration checks pass in private
  CI. Public screenshots belong to workflow 41727632248 at the corresponding PR
  merge snapshot; they contain synthetic settings only.
- Task-owned Windows install, Business data, probes, UI tasks and entire staging
  root removed; no TUN or core remains. Canonical sequential worker lease released.
  Ephemeral GitHub build secrets, isolated GPG agent and controller private stage
  removed. The retained installer/receipt/checklist are private delivery artifacts.
- Owner requested moving the remaining CI/handoff work to another computer before
  powering off this workstation. Preserve the tested installer in private storage
  on the reachable Harness environment and continue there; do not rebuild it.

Rollback: retain/reinstall the previous private pilot. Shared ordinary VPNRouter
source remains in the same project; this change does not fork its engine.
