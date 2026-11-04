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
3. Docs: IN PROGRESS; this brief records scope; update the Business guide.
4. Review: independent review unavailable under the owner's explicit no-subagent
   instruction. Perform and record source checks with distinct correctness,
   lifetime, trust and coverage lenses; do not label them independent review.
5. UI: existing 340/420 and diagnostics synthetic screenshots retrieved from
   CI 41727632248 and shown. New screenshots PENDING. No live desktop capture.
   WINBRAT remains cleaned and released; no new private installer is shipped.
6. Characterization: PENDING; preserve the existing MainWindowViewModel API hash.

## Outcome

Status: BRIEF ONLY; implementation has not started. The private 8f874598 installer
remains retained and unchanged. Only this isolated task checkout is used; the
local dsh/business-pilot branch was created at the accepted base after confirming
that no other worktree has that branch checked out. Continue the existing PR.
