# Business pilot for one Windows employee

- Owner: Pavel Lizunov; implementation in the current repository.
- Branch: dsh/business-pilot
- Base: f6b39dbf (main; initial checks successful).
- Roadmap: N/A; customer-specific distribution using existing engine.
- Risk: HIGH: connection provisioning, DNS and routing precedence.
- Blast radius: opt-in desktop distribution, first-run provisioning, Windows packaging and focused tests.
- Rollback: remove Business installation/provisioned data; ordinary build and its settings remain separate. Revert task commits before redistribution.

## Why

Prepare a Windows pilot in less than two hours: install, open, Connect, with existing VPNRouter architecture and the supplied routing policy. No employee JSON import or manual app selection.

## What

Reuse generated routing, custom rules, subscription/connection models, simple UI, tray and health recovery. Default outbound must be direct; six supplied processes and specified destinations use proxy. Preserve executable casing. Keep standard builds unchanged. Never commit or publish employee credentials or subscription URLs. Use individual server credentials, with update/revocation administered per employee.

The attachment is a throne-route-profile v1 containing domain suffixes and process names, no IP rules or VPN connection. in-mag.ru is in both Direct and Proxy: owner decision pending, no implicit priority. UIS endpoints, DNS paths and UDP behavior need customer-machine acceptance; do not claim it works from config inspection.

## How

1. Inspect current settings/bootstrap, routing/DNS, installer/update paths and available CI.
2. Add minimal opt-in Business identity and strict policy conversion; reject unresolved conflicts.
3. Apply bundled non-secret routing on first use, integrate protected individual provisioning, reuse simple UI.
4. Produce a credential-free CI installer and prepare private employee configuration through an admin delivery mechanism.
5. Add regression/contract tests and employee/admin/UIS instructions; review implementation.

No public release/tag or main merge is requested. Build/package/test work runs only in exact-SHA GitHub CI or an authorized preflighted worker, never locally. Missing VPN settings block a live VPN pilot, not implementation.

## Verification

1. Build: PENDING; Windows CI publish and Inno installer plus applicable Release build.
2. Tests: PENDING; policy/provisioning/routing regression tests and applicable existing suites in CI.
3. Docs: PENDING; employee steps, administration, UIS diagnostics and final evidence.
4. Review: PENDING; bug-hunt with correctness, lifetime/security and coverage lenses; independently verify findings.
5. UI: PENDING; preserve layout, isolated screenshots where applicable. WINBRAT/customer live VPN acceptance blocked pending credentials and machine access.
6. Characterization: N/A unless public ViewModel surface or mechanical splits change; normal behavior must stay unchanged.

## Outcome

- Status: IN PROGRESS.
- Initial source: clean main at f6b39dbf; recent exact-head checks successful; no suggested hash bump file.
- Semantic choice: in-mag.ru pending owner decision.
- Private connection parameters: not supplied.
- Delivery target: first employee Windows installer, then approximately 20 individually provisioned employees.

## Scope correction from owner

Owner requested the fastest pilot: compile the supplied private connection and routing directly into a Business variant; expose only status and Start/Stop. Defer enrollment, fleet control and automated provisioning architecture. in-mag.ru is Direct by explicit owner decision. Connection wrapper fetched successfully and uses the existing supported subscription format (10 share links). Never publish its content. Baseline Linux CI exposed an existing coreutils test-fixture failure; repair it before product edits.
