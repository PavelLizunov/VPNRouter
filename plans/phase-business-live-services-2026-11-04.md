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

1. Build: PASS at exact 2b543cd8; public Release/API gates and private packaging
   run 41939228794, including native split/full configuration checks.
2. Tests: PASS; Linux 3364 passed/79 skipped, Windows full 3534/21, Business
   126/1, including sixteen protocol/consumer/restart regressions. The private
   first-run validation passes 1/0; all thirteen backing rows retained.
3. Docs: PASS; Business guide/private-input contract and observed outcome updated.
4. Review: PASS; three independent source lenses and requested final GPT ASTRA
   XHIGH review/recheck. Its Reality P2 is corrected and source-confirmed closed;
   recheck finds no new confirmed P0/P1/P2. AWG synthetic boundary cases remain
   a recorded nonblocking coverage follow-up.
5. UI/live: PASS for scoped client flows; synthetic 340/420 renders, four choices,
   live/manual refresh, same-account upgrade, collection/explicit delivery with
   independent receiver receipt, cancel/open folder, restart, two retained nodes
   through split/full/split and TUN/core cleanup. The newly added host separately
   FAILS its VLESS/HTTPS path with EOF; cause undetermined, ledger entry open.
6. Characterization: PASS at exact 2b543cd8; shared ViewModel public surface and
   Standard UI/engine gates remain intact.

## Outcome

- Status: IMPLEMENTED AND PRIVATELY INSTALLED on the owner's selected Windows VM.
  Installed source is 2b543cd8d6314f87e22966235ab2eb9177cf1a81; the final records
  commit requires its own public CI. No product rebuild is needed for records.
- Four physical choices and thirteen backing entries are visible/persisted;
  startup and manual source updates acknowledge four. Country names/flags use
  recognized advertised labels; opaque labels visibly remain unknown. This is
  partial advertised-country coverage, not independent geolocation. Server load
  is not implemented; the documented grant-scoped vpnctl API is still required.
- Diagnostics collect locally, and only explicit Send uploads. Independent
  receiver observation sees zero reports through startup/manual refresh/local
  collection, then one after Send. Archive hash/marker/employee mapping match.
- Two retained hosts pass three split/full/split cycles, immediate save/full
  restart, generated TCP host, representative IPv4 HTTPS routing, control guards
  and complete TUN/core teardown. Original choice/mode restored; app left open,
  VPN off, normal VM viewing window active, requested SSH access retained.
- The new fourth host is first in the refreshed provider order. Two attempts on
  it fail; the instrumented retry reports curl TLS exit 35, and native logs show
  VLESS outbound EOF. Retained VLESS fields compare unchanged. This proves the
  added host is listed, not that its VPN path works or that this is a client bug.
- UIS, sustained memory, every routing/DNS rule, UDP dataplane, the other retained
  host and real office reachability remain unverified. Existing memory findings
  are unchanged. No merge, tag or public release is requested.
- [Sanitized owner-VM result](business-live-services-owner-vm-2026-11-05.json)
  records passing client flows and the new-host failure separately. Private
  installer/checklist/receipts and task-only executed verifier sources remain in
  protected delivery storage; no live desktop screenshots or raw logs are public.

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


## Final review correction and private packaging diagnosis

- The exact b851c667 public gate is confirmed: eleven SUCCESS and two expected
  private SKIPPED. Linux 3348 passed/79 skipped; Windows full 3518/21; Business
  110/1. Build, native collector bridge, synthetic widths and characterization
  pass. UI workflow 41933092304 and full tests 41933092273 are snapshot evidence.
- The requested GPT ASTRA XHIGH final review completed read-only across the full
  41-file follow-up. It confirms one P2: Reality without a valid public key can
  parse, replace/cache the working pool, then fail LeakProtection on connection
  and bypass embedded fallback after restart. No new P0/P1 was reported. Record
  BUSINESS-REFRESH-PROTOCOL-FIELDS before correction; validate required fields in
  the shared Business pool parser before mutation or startup cache acceptance.
- Added regressions for Reality key length/encoding, short IDs, unsupported
  security and malformed backing credentials. The consumer test drives HTTP and
  the real service/VM, asserts no row/selection/save/disk-cache mutation, rejects
  direct bypass, then repeats actual file-backed bootstrap. A previously cached
  malformed Reality response must use the embedded fallback. Tests await CI.
- Private run 41934741893 at b851c667 compiled the installer but failed first-run
  strict response validation. Source diagnosis: native capability discovery looks
  in the test output/fresh data tree, while the verified fork core is in payload/app;
  four AWG rows are filtered, causing the strict 13-row count mismatch. Probe the
  actual payload core version/tags for scoped test capability settings, restoring
  prior values. Do not enable features blindly or change product runtime policy.
- The same private fixture reload can emit active labels through SettingsLoader
  Console/Error and Serilog summaries on failure. Record before correction, quiet
  only secret-bearing validation and restore both sinks exactly. No raw input or
  raw build log is published; ephemeral private build secrets removed and absence
  verified. No installer from the failed run was delivered or installed.
- Review acceptance limits remain: exact-source live upload/fourth-node/dataplane
  pending; recognized advertised countries only, unknown for opaque labels; load
  requires the documented grant-scoped backend API. UIS and sustained memory
  acceptance remain separate. Repeat final Astra review on corrected source.


## Corrected-source gates and requested Astra recheck

- Exact 2b543cd8d6314f87e22966235ab2eb9177cf1a81 passes eleven applicable
  public checks and two expected private skips: Linux 3364 passed/79 skipped,
  Windows full 3534/21, Business 126/1. All sixteen added protocol/consumer/
  restart cases pass. Runs 41938144975 and 41938145033; shared API remains intact.
- A new isolated GPT ASTRA XHIGH review of all five correction files plus their
  current callers confirms the prior Reality P2 closed in source, meaningful
  service/HTTP/VM/disk/restart coverage, no confirmed fixture defect and no new
  confirmed P0/P1/P2. No builds, private input reads, remote mutations or tests
  were performed by that reviewer. AWG guard boundary fixtures are recorded as
  a nonblocking coverage follow-up, not an observed product failure.
- Private exact-source run 41939228794 succeeds. Preflight: four logical CPUs,
  7 percent CPU load, 14069756 KiB available RAM, 30.55 GiB free disk and zero
  competing jobs. Actual native-core capability inspection permits all thirteen
  rows; private first-run fixture passes 1/0 skipped. Native split/full config
  checks both PASS. No controller/owner-machine build or SDK install occurred.
- Encrypted-only artifact retrieved/decrypted privately, source and SHA checked.
  Installer: 63013276 bytes; SHA256
  59e95f97372a159b38d5c5c2dee957d4748cc4c28060d636c88da34ac4fb1cbd.
  Both ephemeral GitHub build inputs were removed and absence verified; isolated
  GPG agent/home and delivery key removed. Configured EXE remains private.
- Owner guest preflight: eight logical CPUs, 1 percent load, 13954 MiB available
  RAM, 101 MiB page-file use, 38.96 GiB free C:, zero competing build jobs,
  no app/core/TUN. Upgrade exits 0 and installed product metadata matches exact
  2b543cd8. Host shared-copy and guest-stage installer hashes match. No guest or
  host resource/UAC/VM lifecycle change. Interactive live acceptance is completed below.


## Exact installer live acceptance and cleanup

- In-place upgrade exits 0, same corporate account and original choice/mode are
  privately compared, and installed product metadata matches exact 2b543cd8.
  The full source-bound cache contains thirteen rows/four physical VLESS choices.
  Actual UIA checks all four selectable labels in one popup, acknowledges manual
  live list refresh, sees advertised country/unknown labels and port status, and
  confirms Collect/Send visible and enabled.
- Actual in-app collection emits one 10368-byte allowlisted ZIP with the owned
  synthetic marker and without the synthetic password. Receiver count is zero
  before and after startup/manual refresh/local collection. Explicit Send shows
  delivery acknowledgement; independent receiver validation matches archive
  SHA256 27d3befc9a70fd31cbb7c13d57898b29a738437dfcc2acc6e2199bc5287af8db,
  marker, absence of the synthetic secret and existing employee association.
- A new collection after full restart, collection cancellation retaining the
  previous ZIP, and opening its actual Explorer folder all pass. Only the newly
  opened test folder window is closed. No automatic upload is introduced.
- Initial first-row regression attempts exercise the newly added host, which is
  first in provider order. Both stop at selected-process HTTPS/VLESS EOF; the
  instrumented retry records curl TLS exit 35. Cleanup passes after each. The
  task verifier now explicitly resolves two retained physical hosts from the
  prior settings, while still validating all four UI choices. This separates
  existing-client regression evidence from the new host's unresolved availability.
- Three cycles on retained hosts pass: split on original host one, full and split
  on original host two. Selection saves before connect and survives full process
  termination/relaunch. Generated proxy host matches selection, controls disable
  while connected, ordinary IPv4 HTTPS is Direct in split and the Telegram-named
  probe is Proxy, both are Proxy in full, with distinct VPN egress between hosts.
  All stops remove TUN/core; prior mode/choice restored.
- A first final-readiness adapter stopped on early UIA FromHandle during app
  startup. Bounded provider-readiness retry then passes on the same installed
  binary. This changes only the private task helper. Final same-session UIA
  confirms visible enabled selector/Send and live four-server acknowledgement.
- Removed all five task UI jobs, guest installer/verifier/backup staging, private
  support-identity recovery stage, owned synthetic log and three local report
  directories. No recent collector temporary stage remains. Removed only the
  independently verified test receipt/archive at the receiver. App/data/SSH and
  private delivery remain deliberately; app runs with VPN off and no TUN/core.
  Headless RDP service is stopped; the normal owner viewing window is active.
- Confidential input audit: eighteen recovered credential/reference values and
  four endpoint values match no current path across 1469 files. Fourteen PR/base
  snapshots, 1511 unique blobs, contain no matching credential/reference values;
  endpoint matches are confined to the four already documented historical
  fixture/plan paths. Those current files are sanitized; public history is not
  rewritten. Failed/successful private CI logs have no matched recovered values
  or private labels. This does not scan every ref or external copy.
- Both ephemeral GitHub build inputs remain absent. Isolated GPG agent/home and
  key are gone. Task controller raw input/log/config staging is removed after
  retaining only private configured delivery, sanitized receipts and verifier
  sources. Harness/Omarchy/guest received no SDK or project build.
