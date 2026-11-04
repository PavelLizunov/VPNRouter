# Business server reachability indicators

- Branch: dsh/business-pilot; continue existing PR #646, no second PR.
- Base: 353a28735364ea3f885026dd2e002d689f400586 (eleven applicable CI checks passed).
- Risk: MEDIUM; adds bounded network probes and window-owned asynchronous state.
- Scope: Business window, physical-node reachability display, localization,
  deterministic tests and synthetic screenshots. Preserve routing and selection.
- Rollback: revert this scoped change; retain the already verified 8f874598 installer.

## Why

The owner asks to see the current UI, add traffic-light server availability with
ping, and explain collection/upload by button. Current Business selection has no
reachability display. The separate personal diagnostics EXE already collects and
uploads on demand; continuous statistics upload is not implemented.

## What

Show an indicator and measured latency for each physical-node choice and the
selected node. Green means the endpoint answers the quick probe; yellow means
slow or inconclusive; red means the probe could not reach it. Unknown/checking
is neutral, never fabricated success or failure. Explain that a port probe is
not proof of VPN authentication or working proxied traffic.

Refresh while disconnected, initially and periodically, with an explicit refresh
action. Avoid measuring the tunnel's synthetic local response: pause/cancel and
invalidate results during connecting/connected states. Resume after disconnect.
Use the existing bounded TCP probe and semantic colors, not ICMP or a new engine.
Keep raw hosts/errors/credentials out of user-facing labels and screenshots.

Preserve the three physical choices, complete backing pool, immediate selection
save, trusted restart restoration, existing modes, engine/tray/recovery and the
standard UI/public MainWindowViewModel surface. No automatic node switching,
continuous telemetry, diagnostic uploader integration or private rebuild is
included in this block. The old installer does not contain the new indicators.

## How

1. Publish existing synthetic screenshots and verify the diagnostics behavior.
2. Commit this brief and pass its exact-head CI gate before implementation.
3. Add a Business-only window-owned probe controller with bounded cancellation,
   no overlapping rounds, UI-thread updates and complete event/timer cleanup.
4. Bind traffic-light status and latency in the selector; add localized help and
   refresh action. Do not persist measurements or send them to a statistics API.
5. Cover good/slow/failure/unknown results, cancellation, stale completions,
   busy/disconnect transitions, window/context teardown and narrow layouts.
6. Run exact-SHA CI, inspect new synthetic screenshots, update docs and PR.

## Verification

1. Build: PENDING; exact-SHA GitHub CI only. No Harness build or SDK install.
2. Tests: PENDING; focused Business tests and applicable full Linux/Windows gates.
3. Docs: updated Business guide, including on-demand diagnostics and probe limits.
4. Review: independent review unavailable under the owner's explicit no-subagent
   instruction. Perform and record source checks with distinct correctness,
   lifetime, trust and coverage lenses; do not label them independent review.
5. UI: existing 340/420 and diagnostics synthetic screenshots retrieved from
   CI 41727632248 and shown. New screenshots PENDING. No live desktop capture.
   WINBRAT remains cleaned and released; no new private installer is shipped.
6. Characterization: PENDING; preserve the existing MainWindowViewModel API hash.

## Outcome

Status: IMPLEMENTED, CI PENDING. Brief 1a5d6d2e passed all eleven applicable checks
before implementation. The private 8f874598 installer remains retained and
unchanged; this source change has not been packaged or live-verified on WINBRAT.

- Business-only choice wrappers keep reachability transient, separate from shared
  server health history and settings. Color, localized status and latency update
  together. Unknown/checking never implies a measured ping; inconclusive results
  stay yellow. No raw probe error is displayed.
- The window owns a 30-second dispatcher timer, immediate/explicit refresh and
  one cancellable round. Busy/hidden/context-change/close paths invalidate results
  and cancel requests. Late callbacks check both cancellation and current VPN
  state before updating. A new round waits for cancelled work to finish.
- Deterministic fakes cover all statuses, submillisecond latency, coalescing,
  cancellation and stale responses, failures isolated to one node, selection/save
  invariants, refresh click, hidden/context-change/close cleanup and 340/420
  layouts with all three colors. Actual execution awaits CI.
- Source checks performed by the same agent: correctness (physical choices and
  selection mapping), lifetime (one round, token ownership, timer/event cleanup),
  trust (no settings/health writes or upload, generic errors), coverage (UI binding
  colors, latency and busy controls). No independent review is claimed; the
  owner's no-subagent instruction remains in force.
- Local lightweight checks: Business XAML parses as XML; git diff --check passes.
  No local SDK, compiler, app launch or test runner was used. Existing exact-SHA
  CI owns build/full-test execution; the Business job records capacity/tool
  preflight before compilation. Record new run numbers and inspected screenshots
  after completion.
- Only this isolated task checkout is used. The local dsh/business-pilot branch
  was created at the accepted base after confirming it was not checked out
  elsewhere. Continue PR #646 without force-push, merge, tag or public release.
