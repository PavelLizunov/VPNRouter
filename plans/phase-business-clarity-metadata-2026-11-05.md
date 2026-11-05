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
2. Keep connect, selector, modes and diagnostic Send obvious. Move explanatory
   paragraphs and secondary diagnostic actions into compact optional details;
   show brief actionable progress/failure/receipt states instead of permanent prose.
   Preserve keyboard access, narrow layouts and cancellation.
3. Add bounded computer-name/Windows-user fields to the diagnostic summary,
   disclosed in the optional privacy details. No hardware serial, machine SID,
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

1. Build: PENDING; exact-source public Release and private packaging.
2. Tests: PENDING; hot language/persistence, compact 340/420 layout, device summary,
   metadata grant isolation/validation/staleness and unchanged connection behavior.
3. Docs: PENDING; concise user guidance/privacy/actual outcomes and ledger.
4. Review: PENDING; independent correctness/security/coverage and requested final
   GPT ASTRA XHIGH recheck on the implemented snapshot.
5. UI/live: PENDING; owner-VM upgrade, Russian/selectable language, compact controls,
   real country/load samples if API is available, explicit report with device
   fields and independent receipt. Do not erase the owner's current report.
6. Characterization: PENDING; Standard/public VM API and existing regressions.

## Outcome

Status: implementation in progress; no new installer or backend deployment yet.
The brief cc819d83 passes eleven applicable checks/two expected skips. The existing
owner-VM client was switched from stored English to Russian by a narrow language
preference change/relaunch; the owner's received report was preserved.


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
