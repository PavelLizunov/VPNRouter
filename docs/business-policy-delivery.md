# Corp policy delivery runbook

Status: source preparation for later authorized delivery. Backend operator setup,
reader overlap, protected export and the owned sing-box runner are implemented
and CI-verified at vpnctl `3116a25`. Managed-artifact integration and later live
acceptance remain separate gates. No merge, release, deployment or activation is
authorized by this document; no live operation is claimed.

## Intent, baseline and invariants

Enable signed site policies for existing Corp employees at
`https://admin.corp.ninitux.com`, preserving their VPN subscription and support
identity. Source baseline: VPNRouter
`098b7d3496e1b3b7397b3903503c0a90ea376b04`, inspected 2026-11-15. The existing
[implementation brief](../plans/phase-corp-policy-implementation-2026-11-14.md)
owns task intent, gates and outcomes; the
[defect ledger](../plans/OPEN-DEFECTS.md) owns unresolved findings. Backend
candidate/CI status in the brief is historical evidence, not deployment evidence.
Recheck both exact source SHAs and open findings before later execution.

Invariants:

- Preserve the entire current private bundle, settings, subscription cache,
  policy journal, diagnostics and device/support identity. Add only the intended
  policy capability; a minimal replacement bundle can silently remove services.
- Keep subscription URLs, reader/support tokens, portal password and signing
  private keys outside Git, logs, screenshots, chat and public artifacts.
- Shared updates remain credential-free. A downloaded subscription, signed
  policy or editable YAML cannot install trust roots or change trusted endpoints.
- Preserve accepted revision/digest and distinguish accepted, pending and applied.
  Restore old policy content by publishing a greater revision, never by deleting
  the journal or lowering the server counter.
- Keep publication disabled until backend, routing, private migration and actual
  client gates pass. Follow [the project contract](agent-contract.md) for release
  authority and [worker rules](test-workers.md) for builds/verification.

## Why a private migration is required

[BusinessProvisioningStore.Resolve](../VPNRouter.App/Services/BusinessProvisioningStore.cs)
validates and persists the embedded `VPNRouter.Business.Connection` resource on
every startup when present. It overwrites `provisioning/business.json`; an edit
of that saved file beneath an old embedded EXE is not a durable migration.

A shared binary reads existing protected provisioning when no resource is
embedded. It cannot grant a missing policy capability by itself. There is no
standalone provisioning-import command in the current App/CLI entry points.
Do not invent such a command or present manual JSON replacement as a supported
employee workflow.

The supported delivery bridge is a new private installer containing the entire
current private bundle plus `business.policy`. After first launch persists that
bundle, subsequent credential-free shared updates can reuse it. An old private
EXE can overwrite the migrated bundle again; retain it only as a quarantined
recovery artifact, not as a routine rollback launcher.

## Backend and ingress prerequisites

These are acceptance requirements for later backend delivery. Operator setup,
rotation, overlap and protected export are implemented in vpnctl PR #323; the
ingress and actual-client delivery gates remain pending. Use its reviewed
[backend runbook](https://github.com/PavelLizunov/vpnctl/blob/dsh/corp-policy-portal-20261115/docs/specs/corp-policy-delivery.md)
at the accepted backend SHA before execution. This client document supplies no
substitute SQL, CLI or live probe commands.

1. Provision public IPv4 DNS A records for the canonical host and a valid TLS
   chain. The current observer admits only canonical IPv4 exits; do not publish
   an IPv6 delivery claim or AAAA path until that path has a separately reviewed
   proof/denial contract. Check every published address. Bind the isolated Corp
   surface to that host and prevent alternate-host/operator-surface access.
2. Restrict backend ingress to the intended trusted proxy path. The proxy must
   strip client-supplied forwarding/IP headers and set the single trusted client
   address itself. Configure exact trusted peers; do not trust forwarded headers
   from arbitrary callers. Test direct-backend and spoofed-header denial.
3. Set up Corp disabled and bind it privately to the intended existing subscription
   and fixed Business capability profile. Before the first publication, the editor
   starts from [business-rules.json](../packaging/windows/business-rules.json);
   setup itself must not publish a signed revision. Do not assume separate
   unmerged site changes are already in issued EXEs.
4. Create a separate hashed Corp password, private P-256 signing key and revocable
   reader credential. Do not reuse operator authentication. Export only the
   read capability and verification public keys into private client provisioning;
   a reader cannot log into the portal, publish or select another organization.
5. Establish verified actual IPv4 exits for every eligible effective route,
   including NAT. IPv4-mapped IPv6 peers normalize to IPv4; true IPv6 observations
   are rejected by the current backend. Detoured entries are excluded from the
   Business choices. Bind mappings to subscription eligibility and route
   fingerprints, with stale/missing mappings denied. Inventory/DNS/ingress IPs,
   employee source IPs and a successful TCP handshake are not egress evidence.
   The owned-helper runtime and controller lifecycle are CI-covered; deployed
   mapping verification remains a prerequisite.
6. Verify browser IP gating before login and on protected requests, separate
   host-scoped sessions, CSRF/Origin checks, logout/revocation, rate limits and
   bounded publication. Disable a subscription and prove access disappears.
   Reader GET `/api/v1/corp/policy` requires an enabled binding/user and its
   separate reader bearer, including while the client is stopped. Observer POST
   `/api/v1/corp/egress-observation` instead requires a single-use probe challenge
   and resolved peer, and must work during disabled setup. Do not place the
   browser IP/password gate in front of these two machine-authenticated paths.
7. Back up the backend database consistently with revisions, signed bytes, audit,
   binding and credential state. Back up signing keys separately with restricted
   access. Rehearse restore with publication disabled; preserve the highest
   published revision across database rollback before enabling writes again.

Portal transport is implemented and CI-verified at the client baseline: a dedicated
outbound for the exact selected direct VLESS Business choice, matching HTTPS DNS
and portal UDP rejection before ordinary routing. Business failover must choose
from that same available host-choice set. This preserves ordinary-site sibling
behavior without using it for the portal. Fresh exact-head CI after any change
and later actual egress verification remain required; source JSON alone does not prove the path.

TUN capture is also required. Existing user, mandatory LAN and auto-detected
route exclusions precede sing-box domain rules. At activation, verify that every
owned public portal address is outside those exclusions and test actual traffic
on the target client. Domain rules do not establish protection for destinations
that bypass capture, including arbitrary DNS-rebinding scenarios.

## Prepare the private capability

Use a trusted copy of the current complete bundle outside the repository. Preserve
root `config` byte-for-byte where practical and all existing fields, including
`business.subscription_url`, `nodes_url`, `updates_url`, and `support.endpoint` /
`support.token` when present. Preserve extension fields too. If the old bundle is
a raw URI list, wrap its complete original content as the JSON `config` string
and preserve any separately provisioned service values; do not reconstruct a
subset of nodes from a UI view.

Add exactly these fields under `business.policy`:

| Field | Required value |
|---|---|
| `policy_id` | Stable backend identity, 1-64 ASCII letters/digits or `-_.` |
| `endpoint` | Exactly `https://admin.corp.ninitux.com/api/v1/corp/policy` for this deployment; no userinfo, query or fragment |
| `read_token` | Separate reader credential, 32-512 ASCII bearer-token characters |
| `keys` | 1-4 objects containing exactly `id` and `spki`; unique identifier IDs and canonical base64 DER P-256 SubjectPublicKeyInfo public keys |

The policy object accepts exactly `policy_id`, `endpoint`, `read_token`, `keys`.
The parser allows other trusted HTTPS endpoints in general, but the portal
routing protection and backend export here are verified for this fixed host/URL.
See [wire v1](business-policy-wire-v1.md) and
[BusinessPolicyConfiguration](../VPNRouter.App/Services/BusinessPolicyConfiguration.cs)
for exact validation. A malformed optional capability disables sync while VPN
startup can remain usable: successful VPN connection alone does not validate
migration. The complete bundle limit is 1 MiB. Export and validate privately;
never print its contents into build output. No administrative password or private
signing key belongs in it.

## Build artifacts for later authorized delivery

The following are command templates for a future authorized, preflighted Windows
worker or exact-SHA private CI job, not commands to run on a live client or this
workstation. Use a clean checkout, task-owned empty output directories and the
pinned SDK/toolchain already installed. Record CPU, RAM/swap, disk, running jobs,
SDK and machine identity as required by [test-workers.md](test-workers.md).
Missing SDK/Inno Setup is a blocker; do not install it as part of this runbook.

Private bridge template:

```powershell
./tools/build-business.ps1 -ConnectionFile <external-complete-private-bundle.json> -OutputDir <private-task-output>
```

[The script](../tools/build-business.ps1) sets `VPNRouterEdition=Business` and
`BusinessConnectionFile` and uses Inno Setup 6. Ensure `BusinessSharedUpdate` is
unset before invoking it: this private script does not clear an inherited shared
mode, and the project rejects combining it with a connection file. Verify the expected embedded connection resource in the built App without
printing it. Output is `VPNRouterBusiness-Setup-v<version>.exe` plus `.sha256`;
the staging payload and installer are secret-bearing private artifacts. Policy
signatures are distinct from Windows Authenticode; this step does not claim
installer code signing.

Subsequent shared update template:

```powershell
./tools/build-business-update.ps1 -OutputDir <new-empty-shared-task-output>
```

[The shared script](../tools/build-business-update.ps1) requires PowerShell 7.2,
sets `VPNRouterEdition=Business`, `BusinessSharedUpdate=true`, clears/refuses
`BusinessConnectionFile` and validates the actual package before issuing hashes.
It emits a credential-free `VPNRouterBusiness-update-v<version>-win-x64.zip`,
`.sha256` and `source-sha.txt`. See
[shared packaging](../packaging/windows/business-update.md) for manifest fields,
admission rules and partial-copy recovery. Use the approved private distribution
channel for the bridge; do not upload plaintext private build output as ordinary
CI artifacts. A checksum proves integrity against its trusted reference, not
authorization to publish or install.

## Client backup, migration and recovery gates

For a later authorized rehearsal, use fixed WINBRAT through the project verifier,
not the developer desktop. Use isolated synthetic fixtures for screenshots.

1. Stop the client cleanly and record the binary version/source and private
   artifact hash. Back up the full Business installation and
   `%ProgramData%\VPNRouterBusiness` with ACLs to restricted storage. Include
   `provisioning/business.json`, `policy/`, `config.yaml`, `state.json`, caches,
   profiles and logs; these may contain secrets. Preserve subscription/device
   identity rather than treating a new install as migration.
2. Preserve per-user reports under
   `%LocalAppData%\VPNRouterBusinessSupport`, including `Client/Reports` and
   standalone support `Reports`, plus reports saved to user-selected locations.
   A sanitized support ZIP is not a provisioning or replay-floor backup.
3. Verify the private installer and hash, then rehearse in-place installation and
   first launch. The installer stops Business and can relaunch it. Confirm privately
   that complete provisioning persisted, endpoints/support identity survived and
   policy sync is enabled. Do not erase existing data or uninstall as preparation.
4. Accept a signed revision and restart. Preserve `policy/<TrustId>.bin` and its
   directory permissions; the journal contains the signed envelope and durable
   revision/digest floor. TrustId derives from policy ID plus canonical endpoint,
   not reader token/key set. Record only redacted acceptance evidence.
5. Rehearse a newer shared ZIP through the existing trusted update manifest and
   verify its resource is absent, the private bundle remains unchanged, reports
   survive, and accepted/deleted sites survive restart and ordinary settings save.
6. Rehearse upgrade/rollback/upgrade before delivery. Prefer a compatible
   credential-free recovery binary plus the latest trusted bundle/journal. Never
   launch an old embedded EXE against migrated data without accounting for its
   overwrite. Keep a verified new private installer for repair; shared replacement
   is atomic per file, not across the package, and a failed copy must not relaunch.
7. Restore only with the client stopped and permissions intact. Do not replace a
   newer journal with an older backup merely to make startup pass. A full OS
   restore can rewind the local floor; reconcile with the trusted current service
   while publication is frozen. A corrupt/unverifiable journal needs controlled
   trust/data recovery, not deletion or fallback to embedded seed rules.

## Key and reader rotation

Keep policy ID and canonical endpoint stable. First deliver a private bridge with
both old and new public keys while publishing under the old key. Confirm every
target client can trust both and still verifies its existing journal. Then publish
a greater revision under the new key and verify client acceptance/restart before
removing the old key in a later trusted private bridge. Offline clients must be
accounted for; removing their journal's signer too early blocks recovery.
Shared binaries and signed policies cannot add verification keys.

Rotate the reader through a private provisioning bridge preserving all other
fields and the same namespace. Backend operator controls support two overlapping
readers: add the new reader,
export its exact capability, migrate and verify the intended clients, then remove
the old reader. Never remove the last reader. Retain the accepted policy during
any fetch outage; an export alone does not prove client migration. Reader/key
changes never reset
the revision floor. An endpoint/identity change requires a separate explicitly
reviewed trusted migration; it creates a different journal namespace.

## Required actual-client acceptance evidence

Do not infer completion from CI, publication, or a generic Connected label.
Record exact client/backend SHAs, artifact hashes, revision and redacted outcomes
for two isolated clients before any later owner activation decision:

- Existing subscription, support upload/report access and identity survive the
  private bridge and shared update; new optional capability actually fetches.
- Allowed verified Corp exits reach login in both Business modes; outside,
  spoofed, stale and disabled-subscription exits fail. Operator cookies and reader
  credentials cannot authenticate/publish; wrong password and logout work.
- Portal TCP and its HTTPS DNS use only the selected verified VLESS path; portal
  UDP is rejected. Exercise same-host siblings, failover and detours, plus IPv6
  denial under the current IPv4-only observation contract. Check actual external
  egress and TUN capture, not only JSON.
- Search/add/edit/delete and both `direct` / `proxy` actions work; normalized URLs,
  protected conflicts, invalid domains, signed empty list and concurrent saves
  behave correctly. Deletions stay deleted after restart/save/shared update.
- Route and DNS agree for explicit sites in both modes and beat geographic/app
  defaults; protected UIS/process behavior remains intact. Exercise actual HTTPS
  and applicable UDP, not just parser fixtures.
- `next_connection` preserves a running session. Confirmed `now` reconnects only
  an owned running session; stopped clients stay stopped. Cover hidden window,
  offline/recovery, stop/apply race and generation/load failure. Distinguish durable
  accepted/pending from actual core-applied receipt for the exact configuration.
- Invalid signature, older/equal-conflicting revision, HTTP error/redirect and
  disk failure retain the last valid policy. Corrupt journal and removed signer
  show recovery required rather than resurrecting deleted seed entries.
- Exercise key overlap, reader rotation, backend backup restore and the full
  private bridge -> shared update -> rollback -> upgrade path with preserved floor.

This document was verified by source/path and scoped whitespace review only.
No build, runtime test, live probe, migration, deployment or activation was run.
