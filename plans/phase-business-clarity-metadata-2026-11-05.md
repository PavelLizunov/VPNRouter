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

Status: brief/research. No new product edit, installer build or backend mutation.
