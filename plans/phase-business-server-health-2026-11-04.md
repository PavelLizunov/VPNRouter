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

1. Build: PASS at 28a7eb3a; shared Release solution in CI 41815855276.
   No Harness build or SDK install.
2. Tests: PASS at 28a7eb3a; Business 53 passed / 1 skipped, Linux 3292 / 78,
   Windows 3461 / 21. Full test workflow 41815855382.
3. Docs: PASS; Business guide and this outcome include probe and delivery limits.
4. Review: independent review unavailable under the owner's explicit no-subagent
   instruction. Perform and record source checks with distinct correctness,
   lifetime, trust and coverage lenses; do not label them independent review.
5. UI: PASS for synthetic headless coverage. All six new good/slow/offline
   screenshots at 340/420 from CI 41815855276 inspected; selector, labels, ping,
   modes and footer fit, with vertical scrolling available at the narrow width.
   Interactive screenshot gallery shown to the owner. No live desktop capture.
   WINBRAT remains cleaned and released; no new private installer is shipped.
6. Characterization: PASS; MainWindowViewModel API hash remains unchanged in
   the successful Windows suite.

## Outcome

Status: IMPLEMENTED AND CI VERIFIED at 28a7eb3acc9cf434b0eb765cd06b9b1a05c83c91.
All eleven applicable checks succeed; two private jobs skip normally. Brief
1a5d6d2e passed its full gate before implementation. The private 8f874598 installer
remains retained and unchanged; it does not contain these new indicators. This
source change has not been packaged or live-verified on WINBRAT. The final
outcome-only commit requires its own exact-head CI gate, visible in PR #646.

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
  layouts with all three colors. All twenty added cases pass in Business CI.
- Source checks performed by the same agent: correctness (physical choices and
  selection mapping), lifetime (one round, token ownership, timer/event cleanup),
  trust (no settings/health writes or upload, generic errors), coverage (UI binding
  colors, latency and busy controls). No independent review is claimed; the
  owner's no-subagent instruction remains in force.
- Local lightweight checks: Business XAML parses as XML; git diff --check passes.
  No local SDK, compiler, app launch or test runner was used. Existing exact-SHA
  CI owns build/full-test execution. Business job 126526289498 recorded capacity
  before compilation: 4 CPUs, 6 percent load, 14123692 KiB available RAM, 30.56 GiB
  free disk, no reported competing build processes, SDK 10.0.403, 2944 MiB page
  file with zero current use. This job and full Windows job 126526331997 succeed.
- Synthetic screenshot artifact 13313599785 belongs to CI 41815855276 at PR merge
  snapshot 90ee180f9d1d185917305e8e2ab44878bf8a345a for source head 28a7eb3a.
  Captures are intentionally retained outside Git for owner viewing; they do not
  contain the corporate connection, live desktop or actual measured node latency.
- Only this isolated task checkout is used. The local dsh/business-pilot branch
  was created at the accepted base after confirming it was not checked out
  elsewhere. Continue PR #646 without force-push, merge, tag or public release.

## Owner-requested configuration exposure check

Audit snapshot: 28a7eb3a, before this outcome-only update. Read-only inspection
covered 1443 tracked files at HEAD and every blob introduced by all 22 PR commits
relative to base f6b39dbfc4d4de6e064b470037ea6b5a312c5a4c, including superseded
versions. Combined corpus: 1494 unique blobs, 26200249 bytes. Checks included
share-link and credential patterns, long base64 candidates, fixed UUID locations,
Business bootstrap/build inputs, workflow output paths and PR body/commit messages.
No secret value was printed in the report.

- No corporate connection payload or credentials were found in the inspected
  source/history. New-history pattern matches are the synthetic masking fixture
  in BusinessSupportCollectorTests and the installer's public AppId, not a
  corporate VPN identity. Existing unrelated protocol examples are not evidence
  of a Business configuration leak.
- The repository intentionally contains routing rules and synthetic fixtures.
  The real connection is an external BusinessConnectionFile embedded only by
  the private build. Public UI/test builds have no such input. The private
  installer workflow uploads encrypted output plus hash/source receipts.
- Both temporary pilot build secrets were rechecked absent through GitHub's
  secret-name listing. The retained personal installer remains private and
  intentionally contains the embedded configuration; it must not be published.
- Limits: this was a structured/pattern source audit, not a comparison against
  the real UUID/key, a binary-resource extraction, or a scan of all pre-base
  history, every remote ref, external logs, forks or previously downloaded copies.
  No credential-exposure finding was confirmed; no rotation or history rewrite
  is claimed or performed.
