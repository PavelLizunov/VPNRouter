# Business language, compact UI, device reports and node metadata

- Branch: dsh/business-pilot; continue PR #646 at accepted dfe08773.
- Owner follow-up: Russian/selectable language, less text, trustworthy country
  for opaque nodes, receipt/device attribution and real vpnctld server load.
- Risk: MEDIUM; UI localization, explicit diagnostic metadata and authenticated
  read-only node metadata. Preserve routing/engine, selection/cache and Standard UI.
- Rollback: retain the verified private 2b543cd8 installer and existing APIs.

## Why and confirmed baseline

The owner screenshot shows English UI without a language selector and too many
persistent helper paragraphs. MainWindowViewModel already provides persisted
language commands, but BusinessWindow uses one-time static string bindings and
has no selector. Country currently recognizes advertised labels only; provider
brands/opaque labels must not be guessed as countries. The owner explicitly
requests actual node load from vpnctld rather than only port latency.

The owner's new 20203-byte report is independently present at the receiver with
matching SHA256 and enabled employee-key association. It is the owner's report,
not a task fixture: preserve it. The summary has OS/format/log manifest but no
computer name or Windows user. Employee-key attribution does not identify a
specific device when the same personal build is used on multiple computers.

## What and how

1. Reuse existing language commands, provide a visible language choice and make
   Business static/service/country/status labels react without resetting VPN,
   replacing the pool or abandoning diagnostics. Persist the choice; show Russian
   on the owner's VM after upgrade. Preserve an explicit language on restart.
2. Keep only connection, selector and modes in the main window. Reuse the standard
   kebab menu for language, diagnostics and refresh; remove Details and permanent
   helper paragraphs. Keep concise progress/failure/receipt inside the menu and
   preserve keyboard access, narrow layouts and cancellation.
3. Add bounded computer-name/Windows-user fields to the diagnostic summary,
   disclosed in the diagnostic action tooltip. No hardware serial, machine SID,
   new persistent tracking identity, token or raw config. Distinguish authenticated
   employee mapping from client-reported device names; preserve the owner's report.
4. Inspect current vpnctld source/production revision and existing country/load
   data before implementation. Reuse existing grant authorization for a narrow
   client-safe read-only snapshot if the admin metrics cannot already be consumed
   safely. Return only the subscription's granted nodes, explicit country codes,
   actual CPU/memory/load samples and timestamps; absent/stale values stay unknown.
   No client admin token, scraping or third-party lookup of private endpoints.
   Backend changes require a separate correctly bound task checkout, its own
   instructions, tests and PR. Do not mutate another agent's root or deploy an
   unreviewed backend while researching.

## Invariants and material unknowns

- No public MainWindowViewModel surface change, engine/routing drift or active
  tunnel mutation. Preserve full backing pool, selected host/mode and cache.
- No desktop captures; owner-supplied image may be inspected. New screenshots use
  synthetic headless fixtures and are shown to the owner.
- Reuse individual private service inputs; never print/commit URLs, keys, raw logs
  or real endpoint metadata. Private EXE delivery only, ephemeral CI input cleanup.
- No SDK/build/package/full tests on Harness, Omarchy or the owner's guest. Exact
  SHA GitHub CI after its capacity/tool preflight; no WINBRAT start for this task.
- Read-only backend investigation is in progress. Historical checkout conclusions
  must be checked against current source and deployed revision. A known added-node
  VLESS EOF remains separate from UI changes; do not invent its cause or a fix.

## Six verification gates

1. Build: PASS. Private exact0fc49877 run42025808317 succeeds, embedded first-run
   and native split/full checks pass. App/Core/packaging are identical at5d112dc1.
2. Tests: PASS at5d112dc1, eleven SUCCESS/two expected SKIPPED. Linux3523/79,
   Windows3693/21, Business285/1 (passed/skipped). Existing automatic gates only
   for the final UI correction, as requested by the owner.
3. Docs: PASS for the0fc delivery; outcome/ledger and private receipts reconciled.
   The later simple-activity follow-up below awaits its own CI/build evidence.
4. Review: PASS source-only final GPT ASTRA XHIGH over all23 client delta files
   and9 backend delta files at5d112dc1/ed59850. No new confirmed P0/P1/P2; prior
   alias P2 closed. Actual CPU gate subsequently passed before activation.
5. UI/live: country/load API active with four fresh entries; receiver preserves
   all three owner reports. Owner-VM0fc installer upgrade PASS, Russian app open. No new
   connection/Send acceptance cycles for this quick redesign per owner instruction.
6. Characterization: PASS exact5d112dc1 Windows and existing Standard/VM contracts;
   MainWindowViewModel/routing unchanged in this final follow-up scope.

## Outcome

Current source is5d112dc1; private installer source is0fc49877 with identical
App/Core/packaging. Client gates and requested final review pass. Controllered59850
is deployed and healthy; scoped metadata HTTPS returns200/schema1/four fresh
countries/load/RAM pairs. Receiver source is updated with all owner reports and
registry/env preserved. Owner-VM upgrade and authenticated reply endpoint
verification PASS. The owner subsequently requests the simple-activity follow-up
below; its source is implemented but CI/private rebuild are pending. The dated blocks below describe implementation history;
this outcome and the final handoff receipt supersede their pending statuses.


## First client block before CI

- Added a visible persisted language selector, reactive window/service/row labels,
  a compact680-pixel connection window, collapsed explanatory details, concise
  diagnostic acknowledgements and hidden idle/success subscription chatter.
  Existing public MainWindowViewModel API/commands and tunnel state are preserved.
- Independent source reviews found shared busy/save-failure language problems,
  row-tooltip/warning-masking regressions, stale preference errors and a stop-vs-
  start presentation gap. Recorded before corrections; Business-owned labels
  distinguish start/stop/service-managed healthy states and preserve warnings.
- Independent test writer extended the window coverage to23 executions, including
  hotRU/EN, busy/connected/stopping/service-managed, save failure/retry, warning
  preservation, popup health labels, and compact340/420 geometry/details. Its
  screenshot-close lifecycle issue was recorded and corrected before CI.
- A separate agent adds bounded OS-reported computer/user names to optional
  summary.device; another adds separately labeled client_reported_device receipt
  metadata. Format/ZIP allowlist, authentication, quota, dedupe, response body and
  storage paths stay compatible. Tests cover bounds/Unicode/omission, environment
  spoofing, actual embedded collector and receipt identity/path isolation.
- All device fields are disclosed in RU/EN details and bundled README. Employee
  mapping remains authenticated by its individual upload key; names are supplied
  by the client, not proof of hardware identity. The owner's existing report is
  neither rewritten nor deleted.
- Source XML/Python parsing and whitespace checks pass. No local build/test/SDK
  was run. This UI/collector/receiver block now requires its exact-head CI.

## Parallel trusted metadata work

- Read-only investigation verified deployed vpnctld662b411, not historical local
  HEAD or different main. All four nodes have memory/load samples. CPU percent
  and CPU count are absent; present Unix1m load as load, never invented CPU%.
- Existing local GeoIP data supports CH/IS/NL/SE. New DTO uses local AppState.geo,
  explicit grants/protocol gates and cached DNS only, not brands/access logs or
  third-party endpoint lookup. Existing app-config has no client metadata DTO.
- Primary owns a secondary backend task worktree at deployed662b411. Agents share
  its disjoint source/test scopes; canonical/other task roots are untouched.
  Backend micro-spec, implementation,28 independent spec tests and diff-only
  reviews proceed in parallel. Important anonymous-rate-capacity and invalid-
  future-test-date findings were recorded before correction; recheck pending.
- Separate client agent supplies a bounded typed HTTPS metadata service/parser,
  monotonic aging and focused tests in new files. These are intentionally not
  integrated/committed with this first client block. Window/cache integration,
  actual sample/geo verification and final GPT ASTRA XHIGH remain pending.

## First-block CI correction

71c7b5cf product build, receiver tests and public contract jobs passed, but all
three test-build jobs found the same missing Avalonia extension namespace in the
new layout fixture. Recorded BUSINESS-LAYOUT-TEST-NAMESPACE before adding the
import. No product behavior changed in this correction; exact-head CI is pending.

## First block accepted at46dddb63

Exact PR head matches local46dddb6324b4f9df07db0de5b9f706217fe16f1f. All eleven
applicable checks passed; the two private dispatch-only jobs were skipped as
expected. Business151 passed/1 skipped; receiver tests passed. All23 new UI
executions passed, including RU/EN340/420 images, which were retrieved, visually
inspected and shown with explicitly synthetic data. The owner's VM still runs
the previous2b543cd8 binary. New private package/live acceptance remains pending.

Before metadata integration, independently reviewed parser tests exposed a
100ms deadline phase-order assertion; recorded before correcting the test. The
previous CI finding was also placed inside the ledger introduction by an overly
broad insertion anchor. Restore the introduction from71c7 and move the finding
under Open; this correction does not affect source/CI evidence.

Metadata implementation proceeds in three disjoint parallel scopes: controller/
window lifecycle, independent controller/window tests, and trust review. Root
owns optional embedded endpoint, row formatting/localization/country support and
fixture timing. No MainWindowViewModel public API or routing changes are planned.

## Metadata integration before CI

- First-block full counts at46dddb63: Linux3389 passed/79 skipped; Windows3559/21;
  Business151/1. These are executed CI evidence, not inferred from source review.
- Optional embedded-only business.nodes_url configures a separate HTTPS client.
  Malformed/duplicate/oversized optional values disable metrics while preserving
  subscription/support/bootstrap. The source-bound cache identity is unchanged.
- A window-owned controller fetches at most once at a time, every five minutes
  while visible (including connected), with thirty-second age notifications and
  explicit refresh. Hide/context swap cancels and discards late replies; close
  disposes once. Failed requests/rebuild/language retain the same snapshot age.
- Matched rows supply explicit countries; matched null stays unknown. Known
  labels remain a fallback only without a matched row. Added a cached Swiss
  vector flag and safe unsupported-code handling. No external endpoint GeoIP.
- Compact row shows actual Unix1m load and occupied RAM, real zero, missing data
  and stale values. Tooltip explains that load is not CPU percent and gives age
  derived from server time plus monotonic elapsed time. No metrics persist.
- Independent writer supplies15 controller/window executions including active
  diagnostics/tunnel/selection retention and340/420 RU/EN synthetic captures.
  Root adds15 optional-configuration executions, exact monotonic TTL boundary,
  and corrects the scheduler-dependent deadline fixture recorded in the ledger.
- Three independent read-only lenses (trust, concurrency/lifetime, coverage)
  reviewed the final production draft and returned no new confirmedP0/P1/P2.
  Known nonblocking constructor-request/timer/popup coverage limits are recorded.
- Source XML parsing/whitespace checks pass. No local build/test/SDK ran. Exact
  new-head CI, requested final GPT ASTRA XHIGH, private packaging, backend
  controlled activation and owner-VM live acceptance are still pending.

## Metadata-block CI corrections

Atfd5925da the product builds; Business checks execute279 cases (276 pass, two
fail, one skip). CI confirms strict metadata host validation incorrectly accepts
a bracketed DNS label after the shared lenient VLESS stripper; record before
adding metadata-only raw bracket validation and malformed-bracket cases.
The pool-rebuild fixture also baselines settings writes before existing selected-
row synchronization. Move the metadata-only baseline after rebuilding, retaining
its no-write assertion for the actual failed metadata request/callback.
The final Astra review started atfd5925da was cancelled before replacing its
scope; a fresh review must use the corrected exact commit. No clean final review
or new installer is claimed. Backend2ace802 CI remains pending full tests.

## Final Astra finding and private-build handoff

GPT ASTRA XHIGH reviewed all26 client follow-up files at25f3ca38 and all12 backend
files at2ace802, excluding the earlier PR307 commits. One source-confirmedP2:
raw producer endpoint deduplication conflicts with the consumer's canonical-host
keys; the coordinator confirmed the admin-input, producer and consumer paths.
Recorded before correction. No confirmedP0/P1; no runtime execution by reviewer.
The backend subsequently changes only isolated test seeding at176e1ad; its exact
CI still needs completion before the new alias-canonicalization product block.

Client25f3ca38 now has eleven actual SUCCESS checks and two expected SKIPPED.
Private inputs were recovered read-only from the exact installed2b DLL, with
guest/controller hashes matching, no assembly execution/guest mutation and all
13 entries/four choices/same account/services preserved. Private nodes reference
is derived from the existing same-origin individual source. Values stay private.
Encrypted-only installer dispatch at25f3ca38 was accepted; run ID was not visible
in the single immediate run-list response. Resolve it on the next watch event;
do not poll. Both task-owned ephemeral pilot secrets remain for that running job
and must be removed and verified absent after retrieval/failure. No new install.

## Owner fast UI correction (supersedes additional UI acceptance)

Owner explicitly requests a quick redesign without additional tests: reuse the
base window kebab menu for diagnostics/language, remove Details and all permanent
explanations, keep only connection/server/modes, show real vpnctld country/load,
and allow TCP ping while connected in split mode. Existing user choice/tunnel
must not switch automatically; fresh load may order the displayed choices.
No new UI test cases or further VM connection/Send cycles for this change. Adapt
existing UI assertions only where controls intentionally move; automatic existing
CI/build gates remain. Prior shared canonical consumer test was written before
this steering and remains the backend finding's targeted contract regression.

Exact25f3 client is now installed on owner VM (installer exit0), same account,
choice/mode/language,13entries/fourchoices and originalreport preserved. Russian
app left open, VPN/core/TUN off; task staging removed. No Send/connect performed
by the task during upgrade. Owner's subsequent Send clicks are owner reports;
preserve them and verify actual receipts before answering.

The owner also requests a separate design agent for a concise Gemini diagnostic
reply. Prepare the service/short-response/error fallback design; do not transfer
real diagnostic archives to a new external service while only designing it.

## Today-delivery modular service contract

Owner gives60-90minutes from11:55UTC for a usable version and explicitly requires
remote statuses/analysis to evolve independently of the installed client. Add a
small stable support-analysis-v1 data contract to THIS base client, separate from
Gemini implementation: existing POST durable2xx remains receipt truth, optional
v1 analysis message and bounded same-origin authenticated result polling. Older
receiver/EXE/fakes remain compatible; absent/invalid/timeout analysis never
downgrades a received report. Plaintext bounded replies only, no downloaded code
or automatic commands/config/network changes. Backend receiver can later supply
new analysis rules/model/text without a client update. No real reports sent to
Gemini and no provider key or model is configured in this phase. No additional
UI test cases/VM acceptance cycles per owner; existing automatic CI remains.

Verified owner's two manual Send receipts11:35:21Z/11:35:27Z,19210bytes each,
valid ZIP/hash, enabled employee mapping, both with computer/user fields in
summary; old20203byte08:43report preserved. No Telegram/email forwarding exists.
Read-only service baseline confirms4 fresh country/load/RAM samples available.
Concrete blocker to live data was old controller662b411 plus missing Caddy
node-status matcher, not unavailable metrics. Candidate1fafc61 now passes all11
CI jobs including exact-source four-binary/asset package. Stage/backup/scoped
route validation in progress; no live controller activation claimed yet.

## Current delivery block before push

Minimal base-style kebab replaces main diagnostics/language/refresh controls and
removes Details. Main keeps only connection/server/modes. Existing UI test methods
and data rows were adapted, not expanded. Split-connected TCP probes remain
active; transitions/full/hide retain cancellation. Fresh actual load/RAM orders
only displayed choices under selection-sync guard, never selecting a new tunnel.
Stable support-analysis-v1 channel confirms POST2xx before bounded optional body/
poll handling; receipt remains delivered through analysis/cancel/failure. Receiver
adds scoped reply-only GET and optional versioned POST payload, legacy POST exact.
No Gemini/model calls, provider credentials, new UI test cases or VM connect/Send
cycles. Python/XML/source whitespace checks pass; existing CI/build to run.

Backend alias/candidate1fafc61 passes all11 CI jobs, but actualCPU preactivation
finds AWS-LC C startup AVX incompatible with VM119. Old service remains healthy;
all backups/stages/config-validation ready. Workflowed59850 corrects baseline and
adds AVX-less emulator gate. New artifact must pass actual VM version before
activation; do not use rejected1fafc61 binary. No live database restore occurred.

A current exact-value check of task-owned recovered credential/reference/endpoint
values finds zero matches in the current tracked tree and new source. Raw inputs,
receipts and configured installers remain private. This is not an all-ref/history
erasure claim.

CI0fc49877 product build and receiver checks pass. The Business fixture retains
three old short Send labels after intentional kebab wording; correct only those
literals. One separate headless cleanup exception occurs in isolated-app setup,
not application code. Close the language fixture-owned flyout before its window
and drain its dispatcher. No new test cases, product change or private rebuild
is required for this fixture-only correction; installer source remains0fc49877.

## Final review and service activation receipts

The requested GPT ASTRA XHIGH recheck covered all 23 client delta files from
25f3ca38 to 5d112dc1 and nine backend delta files from 2ace802 to ed59850, plus
relevant unchanged callers. No new confirmed P0/P1/P2; the prior endpoint-alias
finding is closed in source. CPU-artifact acceptance was pending in that review
and subsequently passed actual VM119 version probes and deployment. The reviewer
did not execute tests or access private data.

Exact 5d112dc1 public checks: 11 SUCCESS / 2 expected SKIPPED; Linux 3523 passed /
79 skipped, Windows 3693/21, Business 285/1. Public PR merge f1149a03 and 5d112dc1
have identical Git tree f4c41fe1b81cb6cb9ea2b1963dfebfeb1ba73e15. RU/EN 340/420
synthetic captures were retrieved, inspected and shown; no desktop screenshots.
Existing fixture assertions were adapted, with no extra UI scenarios or owner-VM
connection/Send cycles for the final correction.

Private installer exact 0fc49877, run 42025808317, succeeds: 63,031,393 bytes,
SHA256 b5e554823d5c0b13aa56190beba115f2ad6c746aafcf19aa41d29405ee1962fb.
App/Core/packaging are identical to 5d112dc1; the fixture-only correction did not
trigger a private rebuild. Hosted Windows preflight: four CPUs / 1 percent load,
13,948,032 KiB available RAM, zero pagefile usage, 27.69 GiB free disk, zero
competing jobs; installed .NET 10.0.403 and Go 1.24.13. Embedded first-run: one
passed; native split/full: PASS. Both ephemeral pilot secrets were deleted and
absence verified; delivery key, isolated GPG and raw private-input staging were
removed. Configured private delivery and receipts are retained.

Controller ed59850: all 11 exact-head checks SUCCESS, baseline CPU / Nehalem QEMU
gate PASS, both Rust version probes PASS on actual VM119 before install. Four
binaries and 26 assets match all 30 installed digests; service active, health
200 / 0.9.0+ed59850, restarts zero. Consistent DB backup and separate restored-copy
selfcheck remain protected; no live DB restore or schema changes. The rejected
1fafc61 artifact was never installed. Scoped Caddy metadata routes validate and
reload with existing ownership/permissions preserved. A recorded staging-mode
mistake was corrected after restoring old config, without route outage. Original
legacy config text is unchanged; no live subscription refetch/counter increment
was used for compatibility verification.

Real private authenticated metadata HTTPS at 12:38 UTC: 200 / schema 1 / four
nodes / four countries / four loads / four valid RAM pairs / all fresh, TTL 1320;
strict DTO and privacy headers PASS. Receiver 5d source is active. Authenticated
analysis GET returns 200 for all three existing receipts, exposing only stored,
report_id and analysis, with default unavailable. Unknown 404, unauthenticated
401, public archive 404, health 200; foreign receipt reader guard PASS, without a
second existing token for a real foreign-identity HTTPS check. All three owner
archives and receipts, registry/env and 14 other Caddy files are preserved. No
new POST reports, provider/model calls or automatic client-statistics upload.
Gemini remains unconfigured; this delivery supplies its stable reply channel.

Known limits remain: added-node VLESS EOF, UIS, sustained memory, every rule /
DNS / UDP, other-node / office acceptance and nonblocking timer / production
constructor / popup / backend override / detour / real-IP test gaps. Do not infer
VPN health from TCP ping or resource load, or employees online from subscription
counters. Current exact-value source scan: 27 needles / 1481 paths / zero matches;
this is not an all-history erasure claim.

## Owner follow-up: simple server activity indicator

At 12:50 UTC the owner confirms the installed UI works but rejects raw Unix load
and RAM in server rows. Show one low/medium/high/unknown activity indicator. Keep
capacity/saturation claims separate: the selected Swedish node has 130-266 GiB
per complete UTC day and 160 GiB rolling24h, while physical link capacity and
instantaneous peaks are unknown. Its country mapping is independently confirmed.

Add optional schema1 activity_level from existing reset-safe rolling24h traffic.
Backend policy: below25 GiB low,25 to below100 GiB medium,100 GiB and above high;
only usable observed data qualifies, otherwise unknown. These are product activity
bands, not CPU percent, link utilization, or a guarantee of VPN performance.
The server owns this policy and can refine it without replacing the client. No
hardcoded country/provider override, new node probes, migration, downloaded code,
selection mutation or routing change. Preserve older numeric fields on the wire.
The client maps only the optional enum to localized text/color and sorts stable
activity bands; missing, null, invalid or stale values remain unknown. Remove raw
RAM/load from visible text and hints. Existing UI assertions only, no new UI or
connection/Send cycles. Independently written backend contract coverage and existing
CI apply to the additive API. Private build/upgrade follow the same guarded path.

## Delivered owner-VM baseline and follow-up verification

At12:45UTC the exact0fc49877 installer exits0; graceful tray Exit used a verified
native owned menu, without process kill. Installed DLL SHA256
433516461c246e93a30555836715fd1bc162f5f76c0caba6bd54d129db8c6f43.
Russian main/menu visible; account, selection, mode, language,13entries/fourchoices
and all three owner reports retained. Core/TUN zero, no new connections or sends.
Guest task/staging and host installer staging removed; owner VM/app/RDP intentionally
remain running. Task-only WinPS5 array capture and YAML indentation comparisons
were corrected to compare parsed scalar values, without reinstall/acceptance cycles.

The next activity block keeps this working private installer as rollback. Client
uses only an optional enum, backend owns25/100GiB activity bands and bounded fresh
24-hour-bucket coverage. No link saturation claim, CPU-count migration or new probe.
Independent backend spec writer saw no implementation; separate diff-only reviewer
returned no confirmed findings. Root source review confirms enum fallback, fresh
state, stable selection and legacy numeric wire compatibility. CI remains pending.
Recovered the exact private input read-only from the installed0fc DLL, without
assembly execution; SHA256 matches the earlier input receipt. Managed resources
are length-prefixed without the initially assumed alignment; no input values or
raw assembly were printed. The new private build must remove its task-owned
secrets/key/staging after retrieval while retaining configured private delivery.

## Activity recheck and fixture-only CI correction

Final GPT ASTRA XHIGH atae1276b0/624ed21 reviews the complete11client/10backend
file deltas and reports no confirmed new P0/P1/P2. Seven independent HTTP tests
contain17datasets; malformed timestamp/noninteger/excess-bucket branches remain a
recorded nonblocking coverage limit. Existing CI then finds an old health-tooltip
fixture selecting both the health and new activity Grids. Target the direct health
dot owner, preserving the oracle. Backend cargo check finds only an outdated
generated project map before compilation. These corrections change fixture/docs,
not productApp/Core/packaging/backendRust; private installer source remainsae1276b0.
