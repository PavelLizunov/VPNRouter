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

- No regional-health probe implementation, node-selection redesign or automatic failover changes in this feature.
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

- Status: PARTIAL; the first client policy block is implemented locally, awaiting exact-head CI. Portal, polling/bootstrap/connection integration and delivery are not yet implemented.
- Starting evidence: all seven PR #675 checks completed successfully at `d429a1c2f241062a4923ec6d01d2567d974d711b`; current `business` and vpnctl `main` match the pinned bases above.
- Source reconnaissance: startup overwrites rules; ordinary saves reparse stale text; current Business geo precedence can override explicit proxy; engine ApplyAsync is guarded and currently restarts sing-box; existing visible-window subscription refresh cannot serve policy polling. Findings and dispositions are tracked in OPEN-DEFECTS before fixes.
- Backend source supports an isolated router and current SQLite; no separate service is required. Private Corp-to-subscription binding, verified egress addresses and activation configuration remain delivery prerequisites.
- No local build, remote host mutation, deployment or release has been performed.
- Foundation block: [wire v1](../docs/business-policy-wire-v1.md), P-256 parser, full PSL/domain validation, immutable snapshots, optional provisioning, bounded HTTP, protected atomic journal, explicit route/DNS priorities and UDP-preserving site rules. The embedded preset now pins the portal through VPN; the accepted-policy overload is not yet connected to runtime synchronization.
- Added tests cover strict signatures/schema/limits, shared public vectors, domain normalization/PSL, optional malformed provisioning, durable replay/rotation/writer races, streamed response/deadline cancellation, full replacement and both-mode transport behavior. Dedicated Windows/Linux CI compiles the Business branches. Test execution is pending; no counts are claimed yet.
- Independent foundation review: separate Core security, routing correctness and storage/lifetime reviewers. Core has no remaining confirmed findings. Routing found proxy-domain UDP bypass; storage found missing directory durability. Both are source-corrected with regressions; the lead checked these bounded corrections. There is no claim of a final full-feature review.
- Source checks: whitespace, project XML, shared fixture JSON/base64/payload digests and pinned PSL SHA pass. No local compilation or test run.
- Next: obtain green foundation CI, integrate application-owned policy polling and immutable connection capture with an actual owned-core acceptance receipt, implement the isolated vpnctl portal against the shared wire contract, and run final cross-component reviews/tests.
