# Corp policy portal and Business client synchronization

- Owner: Pavel Lizunov; explicit implementation approval on 2026-11-14 after the interactive flow review.
- Branch/PR: `dsh/business-policy-health-plan-20261114`, [VPNRouter #675](https://github.com/PavelLizunov/VPNRouter/pull/675), target `business`.
- Accepted base: `8fe6c19d9114d1fc7527054c4fe43e4940565c8e`; implementation starts from reviewed plan head `d429a1c2f241062a4923ec6d01d2567d974d711b`.
- Backend: `PavelLizunov/vpnctl`, accepted default branch `main` at `cc071d3143886ccf1cd0869011a694ab67882181`; separate linked task PR required before changes there are delivered.
- Risk: HIGH; authenticated administration, routing/DNS and reconnect lifecycle.
- Blast radius: Business-only policy capability, vpnctld Corp routes/storage, provisioning and their tests. Standard behavior and operator administration retain their contracts.
- Related plan: [phase-business-policy-health-2026-11-14.md](phase-business-policy-health-2026-11-14.md). Its planning review is historical; this brief owns the newly approved implementation.

## Why

The existing corporate subscription is embedded in the EXE issued to employees.
Its site rules currently require a rebuild to change and are reinstalled from the
embedded preset at every startup. The owner wants a simple website where an
administrator chooses whether each site uses the VPN, then saves that choice for
all clients of this subscription.

## What

### Login

1. Open `https://admin.corp.ninitux.com` through the corporate VPN in either Business routing mode.
2. Allow the password form only when the real request IP is a verified exit of a server currently eligible for the existing corporate subscription. Other IPs receive a clear instruction to connect to that VPN.
3. Authenticate with the separate Corp password. The subscription's VPN node IP grants access to the form, not proof of subscription ownership.
4. Expose only Corp rules. Do not expose infrastructure operator functions under this host or reuse the operator session.

Use vpnctl's existing subscription render inputs and eligibility rules with the
fixed Business capability profile, not the browser's User-Agent. Handle disabled
subscriptions, suppression, protocol visibility and detours. Global inventory IPs
and employee source IPs are not this allowlist. Normalize IPv4/IPv6 and trust proxy
headers only through configured trusted proxies. Verify actual egress during
activation; ingress/DNS inventory addresses alone do not establish NAT egress.

The browser IP gate applies before login and on protected requests. Client policy
reads use a separate header credential, including while the VPN is stopped; that
credential cannot log into the portal or publish. Host binding assigns Corp on
the server, never from a request-selected organization or IP alone.

### Rules

- Prefill the managed site list from `packaging/windows/business-rules.json`; users do not re-enter existing sites. The separate unmerged mkvt24.ru PR #674 is not assumed present in the issued EXE.
- Offer search, add/edit/remove, and two choices: "Без VPN" (`direct`) / "Через VPN" (`proxy`). A pasted URL visibly becomes its normalized domain before save; root and subdomains are included.
- Validate bounded DNS/IDNA names, duplicates, public suffixes and conflicting overlapping suffixes. Editing an existing site changes its entry; invalid input cannot publish a partial policy. Rule validation does not claim site reachability.
- Publish a complete managed-site list. Once a valid policy is accepted, deleted or changed seeded entries must not reappear at app restart. An explicitly signed empty list is valid; empty/error HTTP responses are not.
- Keep UIS/telephony infrastructure and process rules protected. Pin the portal's domain through VPN. Reject managed changes that conflict with protected domains; do not silently accept ineffective choices.
- Explicit site direction wins over geographic bypass and application routing in both "Выбранные приложения и сайты" and "Весь трафик с исключениями". Routing and DNS use the same immutable effective list. Preserve process-name casing and existing TCP/UDP behavior.
- Save whole revisions with optimistic concurrency. History records bounded redacted changes; restoring content publishes a new, greater revision. Never claim "applied to all" without client acknowledgements.

### Application timing

- Default: next connection. The current VPN session keeps its captured rules.
- Optional: apply now, with explicit confirmation that calls/downloads may be interrupted. After receiving the policy, only an already running owned VPN session may reconnect.
- Never start a manually stopped VPN. Do not call the UI reconnect fallback that can Stop+Start after a failed apply. Use the engine's guarded apply boundary and recheck lifecycle intent.
- Poll independently of window visibility, with bounded requests and cancellation. Offline clients receive later; current connection transitions defer application safely. Coalesce revisions without repeated reconnect loops.
- Separate accepted, pending and actually applied revisions. A generic Connected label is not proof of configuration acceptance. Network errors do not authorize rollback to an older policy.
- Keep the last valid accepted policy on failed retrieval/validation. Keep acceptance durable before advertising success; disk failure must not advance the acceptance floor in memory only.

## How

1. Commit this brief and reconciled scope first, then obtain exact-head green CI before implementation.
2. Freeze the shared wire specification and fixtures. Use ECDSA P-256/SHA-256, a fixed 64-byte IEEE P1363 signature and exact signed payload bytes carried as base64. The signed payload binds schema, Corp policy identity, revision, key ID, publication time, complete site list and apply timing. Do not sign independently reserialized Rust/C# JSON.
3. Bound decoded response to 64 KiB, 512 sites, JSON depth 8 and 10 seconds. Reject redirects, duplicate/unknown critical fields, incorrect identities/keys/signatures and older revisions. Equal revisions are idempotent only for the same payload. Pin a public-suffix dataset and shared positive/negative domain fixtures.
4. Extend optional trusted provisioning with policy identity, HTTPS endpoint, read credential and verification-key set. Invalid/missing policy capability disables only sync. Subscription responses, remote policy and editable YAML cannot change trust roots. Key rotation uses an overlap of provisioned keys; administrative secrets/private signing keys never enter an EXE.
5. Add isolated Corp router/auth and SQLite storage in vpnctl. Reuse subscription eligibility, Argon2 password hashing and trusted-peer handling. Use separate revocable sessions, Secure/HttpOnly/SameSite cookies, CSRF/Origin checks, rate limits, escaped output and a restrictive CSP. Publish signed bytes, revision and audit atomically; cap request bodies deliberately rather than inherit the operator router's 8 KiB cap.
6. Add Core validation/effective-rule assembly and App transport, protected atomic acceptance journal and coordinator. Revalidate cached signed bytes at bootstrap. Capture a separate effective settings snapshot at connection/apply boundaries; refreshing accepted policy must not mutate settings retained by the current engine/failover context. Prevent ordinary settings saves from restoring stale CustomRulesText.
7. Add focused behavior and integration tests, perform independent correctness/lifecycle/security reviews, fix confirmed findings recorded in OPEN-DEFECTS, and run all applicable CI at exact heads.
8. Prepare a trusted one-time provisioning migration procedure for already issued clients. Preserve subscription, identity and reports; ordinary shared update ZIPs remain credential-free. Document DNS/ingress, actual-egress verification, operator secrets and activation steps as concrete delivery prerequisites. Merge/release/activation remain separate owner commands.

Likely client files: `BusinessDefaults`, `ConfigGenerator`, new `BusinessPolicy*`
Core services; `BusinessClientConfiguration`, `BusinessBootstrap`, new App policy
transport/store/coordinator; MainWindowViewModel connection/settings/lifetime
integration; localization, Business tests and provisioning/package contracts.
Backend files: isolated Corp handlers/router, shared subscription eligibility,
SQLite migration/storage, direct signing dependency, independent HTTP spec tests
and the generated project map. Confirm each zone contract before editing.

## Invariants and exclusions

- No regional-health probe implementation or node-selection redesign. The portal transport correction narrowly filters Business failover candidates to the same available direct-VLESS host choices; probe ranking, retry and Standard selection behavior are preserved.
- No live credentials or subscription URLs in source, fixtures, logs, screenshots or PR text.
- Preserve the separate PR #674 and the existing local main checkout.
- No local workstation/harness builds, packages, SDK installation or full test runs. Use exact-SHA GitHub CI; trusted workers require the prescribed read-only load/identity preflight.
- A shared binary alone cannot enable the feature for old provisioning. Do not claim a live portal or migrated clients from source/CI results.

## Rollback

Before activation, revert task changes on their branches. For policy content,
republish the prior content at a newer revision; never lower the client's durable
acceptance floor. Backend rollback retains policy/journal data and backup revision
history. A binary rollback must preserve private provisioning and journal files;
validate upgrade/rollback/upgrade before any delivery. Freeze publication during
an incompatible backend rollback and retain the last accepted client policy.

## Verification

1. Build: BLOCKED pending implementation; Release solution plus explicit Business-edition compilation in GitHub CI, backend Rust build at its own exact SHA.
2. Tests: BLOCKED pending implementation; focused and full applicable suites. Cover signature/schema/limits/replay/rotation, real bootstrap restart and ordinary saves, both directions in both modes, both apply timings, stopped/offline/hidden clients, stop/apply/dispose races, atomic writes and two isolated clients receiving one revision. Server tests cover allowed/disallowed/spoofed IP, disabled subscription, wrong password, cross-surface access, revocation, CSRF, concurrency and signed publication.
3. Docs: BLOCKED pending final implementation evidence; approved scope and source findings recorded. Wire fixtures, migration runbook and final Outcome still required.
4. Review: BLOCKED pending code; independent bug-hunt lenses for correctness, security and lifecycle/test coverage. The earlier lead planning review is not independent product review.
5. UI: BLOCKED pending portal/client UI; deterministic narrow-width render and actual interaction tests in CI. Any later shipped candidate requires the applicable fixed-WINBRAT scenario. No live desktop scenario on this workstation.
6. Integration/characterization: BLOCKED pending implementation; shared Rust/C# fixtures, exact route/DNS snapshot consistency, preserved Standard and operator surfaces, private provisioning/shared-package compatibility. No mechanical god-file split is planned.

## Outcome

- Source/CI readiness (2026-11-15): client `098b7d3496e1b3b7397b3903503c0a90ea376b04`
  passed all 13 applicable checks (three manual delivery jobs skipped). Backend
  `3116a257bb09678c634a58dbdbd9727ef2ed6509`, run `43714819498`, passed
  all 12 jobs, including canonical Cargo/just-ci, 14 real process/socket/TLS
  tests, browser verification and static musl. A final cross-component diff
  review of wire/export/eligibility/client validation and routing returned no
  findings. A separate delivery-document review also returned no findings.
  This final client block changes only delivery documentation and ledger/brief
  receipts; local links, whitespace and secret scanning are checked, and its
  exact-head CI remains required. Backend managed-artifact compatibility is a
  separately pending CI gate. No deployment, private installer migration,
  actual-client capture/egress or production activation is claimed or authorized.

- Delivery-document reconciliation (2026-11-15): align this branch's runbook with
  green client `098b7d3496e1b3b7397b3903503c0a90ea376b04` and backend
  `3116a257bb09678c634a58dbdbd9727ef2ed6509`: exact portal reader URL,
  implemented operator/overlap controls, IPv4-only observed exits, TCP/DNS pin,
  UDP rejection and distinct browser/reader/observer ingress gates. Preserve
  private installer migration, TUN capture and later authorized live acceptance.
  The backend owned sing-box runner is green in PR #323 at that baseline;
  managed-artifact integration and later live deployment remain separate gates.
  Verification: independent source audit, path/contract/whitespace review and
  exact-head client CI; no local build/runtime. Rollback is a task-branch revert.

- Portal transport source block: dedicated exact-choice VLESS/HTTPS DNS, UDP rejection and a bounded Business failover candidate filter. Independent correctness, security and test-consumer reviewers ran on the complete working-tree diff. One confirmed failover integration defect was recorded before correction and covered by a restart callback that actually generates the new configuration. No remaining confirmed blocker from those reviews; full lifecycle/dataplane execution remains outside this source gate. Static whitespace checks pass; runtime checks are not run locally and fresh client CI is required.

- Next verified block (2026-11-15): after backend 5758b2010ee24306cf19eb4576db83d7a534f1a6 passed all 12 CI checks, add operator preparation controls and constrain the client portal transport. The client must build a dedicated outbound from the exact selected direct VLESS Business choice, reject portal UDP before ordinary routing, and use a dedicated matching DNS path. Same-host siblings, health narrowing and automatic pool selection must not redirect this protected traffic. Invalid, chained or unavailable selections fail closed. Preserve ordinary-site transports and both modes; regression coverage includes Reality/WS/XHTTP siblings, selection changes, domain/subdomain precedence and unavailable selection. Rollback is a task-branch revert; runtime validation is exact-SHA CI only. Backend operator setup/rotation/export remains separate from later egress verification and activation.

- Backend HTTP/browser candidate 4db751c6743ed0c136d28e4c2e36b425e4b4b1d1 is now published in vpnctl PR #323; its head was confirmed by GitHub. It adds the isolated portal/eligibility, 51 independent tests and CI browser scenarios. Independent reviews and allowed source checks passed; fresh runtime checks and screenshots remain pending. Publication access was resolved through an owner-authorized repository-specific write deploy key, created using the existing GitHub API credential. Both the existing credential and complete new key/recovery record were retained in the permanent owner-access store under project vpnctl / machine github.com before use. No PAT/browser login was created and no credential was put in Git. The canonical stored origin is unchanged. CI must pass at this SHA before the next product block; PR #323 is rewatched for automatic continuation. Client PR #675 remains unwatched until its next publication. Small-model agents prepared uncommitted operator and delivery contracts during the access work; these still need lead review.
- Client verification at b4703c1228ae8f4b2ca12bccf084db4a25f55360: all 13 applicable checks completed successfully; three manual private-delivery jobs were skipped. The workflow follow-up preserves the synthetic screenshots, and visual inspection is recorded below. Backend foundation is green at 908bcacd9a1c2e41ddc058eb1d6aca47516d10da with all 11 checks successful, including all canonical Cargo commands and just ci; no deployment or release has occurred.
- Verification follow-up: preserve the two synthetic pending-policy headless screenshots from each dedicated Business CI job as per-OS exact-head artifacts. Layout assertions passed at b874f33. At b4703c12, both dedicated Business jobs passed and the lead retrieved run 43649604579 artifacts 14135053700/14135008919. All four Linux/Windows 340/420 px images were inspected: pending text is legible and primary/mode controls fit without clipping. No live desktop capture or production data is involved.
- Status: PARTIAL; the first client policy block is pushed in `14cec7bde44a26476e9312e6156a243e164410b3`. Its invalid-key fixture correction is green at `6c744252ab0f963dba0d0fc0b87214b000d56b6e` (13 applicable successes, three manual private-delivery jobs skipped). The polling/bootstrap/connection runtime block is pushed and green at b874f33faea7daf464ada1e5cf73750bdfd573a2 (13 applicable successes, three manual private-delivery jobs skipped); portal and delivery remain pending.
- Starting evidence: all seven PR #675 checks completed successfully at `d429a1c2f241062a4923ec6d01d2567d974d711b`; current `business` and vpnctl `main` match the pinned bases above.
- Source reconnaissance: startup overwrites rules; ordinary saves reparse stale text; current Business geo precedence can override explicit proxy; engine ApplyAsync is guarded and currently restarts sing-box; existing visible-window subscription refresh cannot serve policy polling. Findings and dispositions are tracked in OPEN-DEFECTS before fixes.
- Backend source supports an isolated router and current SQLite; no separate service is required. Private Corp-to-subscription binding, verified egress addresses and activation configuration remain delivery prerequisites.
- Backend foundation is published in draft [vpnctl #323](https://github.com/PavelLizunov/vpnctl/pull/323), targeting main, at 908bcac. It adds shared wire/domain validation, P-256 signing, disabled Corp storage, transactional publication/audit and revocation. Independent source reviews and 61 spec-derived tests passed in exact-head CI run 43650614943. The next candidate adds isolated HTTP/browser routes and Business eligibility with 51 further spec-derived tests; its execution is pending publication. The operator/actual-egress/delivery block remains outstanding, and nothing is activated.
- No local build, remote host mutation, deployment or release has been performed. Client runtime compilation and tests passed in GitHub CI at b874f33; no live VPN or shipped-client verification is claimed.
- Foundation block: [wire v1](../docs/business-policy-wire-v1.md), P-256 parser, full PSL/domain validation, immutable snapshots, optional provisioning, bounded HTTP, protected atomic journal, explicit route/DNS priorities and UDP-preserving site rules. The embedded preset pins the portal through VPN. Runtime synchronization now connects the accepted-policy overload to bootstrap and isolated connection captures in the green runtime block.
- Added tests cover strict signatures/schema/limits, shared public vectors, domain normalization/PSL, optional malformed provisioning, durable replay/rotation/writer races, streamed response/deadline cancellation, full replacement and both-mode transport behavior. Both dedicated Windows/Linux Business policy jobs passed at `14cec7b`; the shared Release build and Android compilation also passed. This does not establish full green CI.
- CI correction: `business-surface` reported 586 passed, 1 failed and 8 skipped; the Linux test job reported 3833 passed, 1 failed and 81 skipped. Both failed the malformed optional-policy theory. Its invalid-key fixture replaced unescaped random-key base64 inside serialized JSON, which can escape `+` and leave the valid policy unchanged. The fixture now supplies `invalid-key` before serialization; all rejection/provisioning assertions and product validation remain intact. Recorded as BUSINESS-POLICY-INVALID-KEY-FIXTURE before correction; no local .NET execution. Scoped diff/whitespace checks pass; exact-head CI remains required before the next product block.
- Independent foundation review: separate Core security, routing correctness and storage/lifetime reviewers. Core has no remaining confirmed findings. Routing found proxy-domain UDP bypass; storage found missing directory durability. Both are source-corrected with regressions; the lead checked these bounded corrections. There is no claim of a final full-feature review.
- Source checks: whitespace, project XML, shared fixture JSON/base64/payload digests and pinned PSL SHA pass. No local compilation or test run.
- Runtime block green at b874f33: load the signed journal before bootstrap; enforce canonical managed rules on save; deep-capture effective settings before background engine calls; poll independently of window visibility; apply only to the session captured at receipt; distinguish accepted and actually applied using owned core acknowledgement. Unverifiable startup journals block new policy-managed connections rather than restore removed seed sites. Added bootstrap/save, lifecycle race, cancellation and narrow UI regressions. Health recovery keeps the current typed configuration; unversioned config writes cannot restore old snapshots. Queued applications respect the acknowledged per-session revision floor. Current and recent bounded redacted diagnostic logs remain available to in-app support.
- Runtime review: independent App, coordinator/settings and Core reviewers checked source without executing tests. Confirmed capture timing, completion text, stale health settings, pre-spawn lease and diagnostics findings were recorded before corrections. The lead also confirmed health-transition admission and queued older-apply races. The final independent Core source recheck found no remaining confirmed production findings in its inspected snapshot. Subsequent bounded changes (old-owner teardown, successful apply plus actual health recovery coverage, and admission during crash backoff) were source-checked by the lead. The successful engine regression covers both a running core and publication during crash backoff, with deleted-site and current-receipt assertions after actual HealthMonitor recovery. Source/whitespace and XML checks pass; no added test has been executed locally. Exact-head CI passed at b874f33; no final full-feature review is claimed.
- Next: implement the isolated vpnctl portal against the shared wire contract, prepare trusted provisioning migration, and run final cross-component reviews/tests. Backend work is based on cc071d3 in its own task branch; the existing main checkout is preserved.
