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
  fixed WINBRAT default. Do not start WINBRAT or create additional agents.

## Six verification gates

1. Build: PENDING; exact-source Release solution and private Business packaging.
2. Tests: PENDING; focused UI/service/parser/upload cases plus full Linux/Windows
   suites. Cover fourth-node arrival, invalid/empty/failure retention, selection
   and cache restart, busy-state races/cancellation, all variants, unknown geo,
   diagnostics success/refusal/retry/cancel and no automatic upload.
3. Docs: PENDING; update Business guide/private-input contract and this Outcome.
4. Review: PENDING; separate correctness/lifetime/security/coverage self-checks.
   Independent review is unavailable under the owner's no-extra-agent constraint;
   do not call self-checks independent review or a stable-release review pass.
5. UI/live: PENDING; synthetic 340/420 renders and actual owner-guest subscription
   update to four choices, diagnostic send with independent receiver receipt,
   choice/restart, two-node split/full routing and TUN/core cleanup. No desktop
   screenshot; preserve the owner's data and leave the app usable.
6. Characterization: PENDING; preserve shared ViewModel public-surface baseline
   and standard UI/engine behavior; no unrelated mechanical refactor.

## Outcome

- Status: PLANNED; no product or installed-binary changes yet.
- Private subscription probe is evidence of current server data, not proof that
  the existing application refreshed it. Existing installer remains 0079ee82.
- UIS, every rule/DNS/UDP path, third/fourth-node dataplane, office calls and
  sustained memory behavior remain separate acceptance limits.
- No merge, tag or public release is authorized. The prepared brief requires
  its exact-head CI gate before the implementation block.
