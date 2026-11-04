# Business in-app diagnostics, live subscription and country labels

- Owner: Business pilot owner follow-up, including country/geo clarification.
- Branch: dsh/business-pilot; continue PR #646, no second PR.
- Base: 984001c248e208d67bed2829fec9835eda40900a.
- Risk: MEDIUM; private subscription refresh, explicit diagnostic upload and UI.
- Scope: Business app/services/window, private packaging inputs, focused tests and
  documentation. Standard UI/public ViewModel API, routing and engine preserved.
- Rollback: retain the verified 0079ee82 private installer and working preset;
  revert only this follow-up if a regression is confirmed.

## Why

The owner cannot find a diagnostic Send action inside the application. Existing
support delivery is a separate EXE; the app tray exports locally. The owner also
added a fourth server, but the installed Business build embeds a static snapshot
and its Refresh button only probes ports. They request visible country/geo beside
servers and ask whether vpnctl can supply server load.

## Confirmed baseline

- Exact base has all eleven applicable CI checks SUCCESS and two expected skips.
- BusinessWindow contains Connect, port Refresh and a selector, with no upload UI.
- BusinessBootstrap.Apply reads VPNRouter.Business.Connection at construction;
  BusinessDefaults.Apply replaces the server list and clears subscription sources.
- The originally supplied HTTPS subscription was recovered from the original
  owner session without printing it. A fresh bounded request returns HTTP 200,
  13 entries, four distinct direct VLESS hosts, no blank/duplicate labels. Raw
  response/reference remain in protected task staging outside the repository.
- vpnctl source has node memory/load telemetry under the administrative surface.
  Its current app-config wrapper contains config/version/refresh metadata but no
  node metrics. The public sub-access endpoint counts subscription requests,
  not VPN sessions or resource load. A client metrics API needs its own contract;
  no admin credential or scraped admin HTML belongs in employee installers.

## What

1. Add a visible Diagnostics area with collection and explicit Send, progress,
   acknowledgement and retry/failure behavior. Reuse the reviewed allowlisted
   Business log collector and receiver contract. Preserve local reports on
   failure; no automatic upload, raw config, URL entry or extracted-script steps.
   Package only an individual upload identity, never receiver/admin credentials.
   Keep standalone diagnostics available when the app cannot launch.
2. Preserve the immutable private subscription reference in future Business
   builds alongside a fallback snapshot. Refresh on disconnected startup and
   explicitly from the window, with bounded requests and cancellation. Validate
   a complete response before replacing state; failed/empty/invalid refresh keeps
   the last usable list. Retain all protocol variants, selected physical host and
   split/full mode. Changes to the list cannot mutate an active/starting tunnel.
   Keep a valid source-bound cache across restart, with embedded fallback for a
   fresh offline start. Show a useful update/failure state, without private URLs.
3. Show country names in the selected UI language and small reliable flag assets
   beside physical choices. Use recognized country information advertised by the
   subscription; an opaque/custom unknown label shows unknown country. Do not
   infer a country from latency or silently send private endpoints to a geo API.
4. Report vpnctl load integration feasibility accurately. Current API is missing
   a client-safe per-granted-server metric snapshot. Document the needed narrow
   API, timestamp/staleness and missing-data semantics; do not claim load is
   already available or substitute request counters. Backend/API deployment is
   outside this client change unless separately requested by the owner.

## How

- Apply phase-task-launcher on the existing accepted task branch: commit this
  brief/findings first, verify CI, then implement the client block.
- Keep Business service ownership separate from the shared engine. Reuse existing
  HTTPS/parsers, private staging/collector/sender and private packaging workflow.
- Keep credentials/raw responses outside Git/chat/screenshots and public CI.
  Private builds use encrypted-only delivery, exact-source receipts and cleanup.
- Build/test only exact-SHA GitHub CI with its recorded tool/capacity preflight.
  Never compile or install SDKs on Harness, Omarchy or the owner's guest.
- Upgrade and test the owner's already selected Windows VM through its verified
  SSH channel; this explicit target continues the earlier owner exception to the
  fixed WINBRAT default. Do not start WINBRAT. The owner subsequently authorized multiple agents and
  machines; three read-only independent source reviews now cover this draft.

## Six verification gates

1. Build: PENDING; exact-source Release solution and private Business packaging.
2. Tests: PENDING; focused UI/service/parser/upload cases plus full Linux/Windows
   suites. Cover fourth-node arrival, invalid/empty/failure retention, selection
   and cache restart, busy-state races/cancellation, all variants, unknown geo,
   diagnostics success/refusal/retry/cancel and no automatic upload.
3. Docs: PENDING; update Business guide/private-input contract and this Outcome.
4. Review: IN PROGRESS; correctness/lifetime, security/trust, and tests/UI
   independent reviewers are authorized by the later owner instruction. No
   reviewer builds, mutates workers, changes files or recursively delegates.
5. UI/live: PENDING; synthetic 340/420 renders and actual owner-guest subscription
   update to four choices, diagnostic send with independent receiver receipt,
   choice/restart, two-node split/full routing and TUN/core cleanup. No desktop
   screenshot; preserve the owner's data and leave the app usable.
6. Characterization: PENDING; preserve shared ViewModel public-surface baseline
   and standard UI/engine behavior; no unrelated mechanical refactor.

## Outcome

- Status: IMPLEMENTED / CI PENDING; installed binary is still unchanged.
- Private subscription probe is evidence of current server data, not proof that
  the existing application refreshed it. Existing installer remains 0079ee82.
- UIS, every rule/DNS/UDP path, third/fourth-node dataplane, office calls and
  sustained memory behavior remain separate acceptance limits.
- No merge, tag or public release is authorized. The prepared brief requires
  its exact-head CI gate before the implementation block.

## Implementation block

- Brief cedd1aaa84f5109b619b3c485ee63c6f44d25d66 passed all eleven applicable
  exact-head checks, with two expected private skips, before implementation.
- Added a Business-only controller, HTTP boundary, private collector bridge and
  embedded immutable service metadata reader. In-app Collect/Send/Open folder/
  Cancel actions preserve failed reports and show acknowledgement only on success.
  The canonical allowlisted PowerShell collector is embedded unchanged.
- Added a complete-response subscription refresh, source-bound raw-response
  cache in the existing atomic settings transaction, and guarded pool replacement.
  A save failure restores the old settings before changing visible rows. Both UI
  connection flags and actual engine running state block replacement. Retained
  nodes are matched by refreshed label or physical host; mode is not changed.
- The window rebuilds transient health rows after a list change and owns service
  cancellation/timers on hide, connect, context swap and close. Shared VM public
  surface and connection implementation were not edited.
- Added eight small locally drawn flag assets and localized country names for
  recognized advertised labels; unknown/custom labels remain explicit unknown.
  Source metadata is not a verified geographical/egress observation.
- Added deterministic parser/cache/source-isolation, HTTP upload/refusal/bounds,
  fourth-node/variant/selection/restart/rollback, busy/cancel/stale-completion,
  no-auto-upload, retry/fresh-after-ack, country and 340/420 UI fixtures. A Windows
  fixture executes the actual embedded collector against synthetic logs and checks
  masking, absence of copied config, stage cleanup and private report ACL.
- Correctness/lifetime/security/coverage self-checks are performed; three
  independent reviews are running under the later owner authorization. A stale-archive-after-ack
  issue in the draft was recorded before correction and given a regression.
- Source checks only on Harness: scoped diff, XML parsing, asset dimensions and
  private-value comparison. No local SDK/build/test execution. All runtime/build
  gates remain pending exact-SHA CI and the later private install/live scenario.
- Actual server metadata currently recognizes only some country labels. Unknown
  labels are not guessed; a future vpnctl country-code field is documented along
  with the client-safe load API needed for opaque nodes.

## Refreshed input audit and owner review authorization

- A read-only credential comparison with the installed owner guest confirms the
  recovered subscription is the same account. No raw credential was printed.
- Exact comparison of the refreshed private input against 1464 current paths
  initially found two real endpoint addresses in three old test files and one
  historical plan. No subscription identity or VLESS credential matched. The
  finding was recorded before replacing those and nearby historical live-log
  values with RFC documentation addresses. Historical Git commits are untouched.
- Recheck: 21 private values, zero matching current-tree paths. This supersedes
  the earlier old-snapshot-only zero-match statement for the refreshed source;
  it does not claim that public history was erased or that endpoints are keys.
- The owner now explicitly permits multiple subagents and machines. Three
  independent read-only reviews inspect the complete current draft, with separate
  correctness/lifetime, security/trust and tests/UI questions. Model/settings are
  inherited unchanged. Build/SDK restrictions and one mutable scenario per worker
  still apply; no additional VM, backend deployment or product build was started.

## Independent review disposition before CI

- Three independent reviewers completed source inspections: business_live_correctness,
  business_live_security and business_live_coverage. Findings were reopened and
  recorded before corrections; no reviewer ran builds, tests or VM operations.
- Corrected the draft's elevated user-writable script execution: the immutable
  embedded collector now goes to fixed system PowerShell over redirected stdin,
  with system working/module paths and no executable disk staging. Security
  reviewer rechecked the source boundary; actual WinPS bridge awaits CI.
- Corrected malformed-YAML diagnostic fallback to omit unparseable content,
  including unloadable backups, rather than reconstruct secret scalar boundaries.
  Added short-wrapped base64 cache and unknown multiline regressions. Security
  reviewer rechecked this causal path; parsed YAML still preserves safe fields.
- Corrected physical-host priority when labels are reused, null-context panel
  cleanup and Explorer process disposal. Correctness reviewer rechecked all three.
- Added actual shared-bootstrap/file-backed restart fixtures, source mismatch/
  corruption cases and unknown-length streaming limit tests. The prior manual
  pool hydration fixture is now named as pool persistence, not bootstrap proof.
- Reviewers contributed only four new regression files under explicit disjoint
  ownership. Coordinator inspected each. Corrected the older contradictory guide
  paragraphs. All build/runtime/layout claims remain pending exact-SHA CI.
- Source audit also retrieved the existing personal standalone diagnostics EXE
  privately and verified its known SHA256. No new upload identity was issued or
  receiver registry changed, and no new client was installed in this block.

## Requested final Astra review

The owner explicitly requests an additional final review using GPT ASTRA with
XHIGH reasoning after implementation/verification. Keep the primary model and
existing reviewers unchanged. Route that review to the requested exact model
through the available T3 delegation capability, with a clean final snapshot,
scoped diff, expected behavior and actual CI/live evidence. Reviewer is read-only
and does not recursively delegate or access private delivery inputs. Source-
confirm findings, fix accepted blockers and rerun affected gates before handoff.
Do not substitute another model if the requested route is unavailable.

## First implementation CI correction

At 495aa4d4, Linux/shared and Windows/shared builds stop before tests with CS0103
for AppPaths in the new pool-save method (runs 41932551614 and 41932551579).
The partial file lacked using VPNRouter.Core; add that namespace only. The
failure was recorded before correction. Runtime/UI tests remain unexecuted for
this snapshot; repeat all applicable checks at the correction head. No installer
was rebuilt or deployed from the failing source.
