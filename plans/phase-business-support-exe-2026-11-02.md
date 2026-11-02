# One-file Business diagnostics delivery

- Owner: VPNRouter Business support follow-up
- Branch: dsh/business-pilot
- Base: 77fde2707a8513bb63c48930e18ae969cac41274
- Risk: MEDIUM; employee reports cross a public HTTPS boundary.
- Blast radius: standalone support executable, receiver integration, Business
  support packaging/tests/docs. The distributed VPN pilot remains usable.
- Rollback: keep the verified script collector; remove only task-owned receiver
  routes/artifacts if the new delivery path is withdrawn.

## Why

The owner rejected extracting scripts and manually sending ZIP files as too
complex for employees. They requested using their existing Homelab and landing
infrastructure to find the receiving endpoint rather than asking them to supply
infrastructure details already available through Harness.

## What

Deliver one Windows EXE with one clear Send diagnostics action, visible progress,
confirmation on acknowledged delivery and a useful failure state. Embed the
existing reviewed collector/sender rather than writing another redaction engine.
Extract required implementation files internally; employees must not extract,
select archives, open PowerShell, enter URLs or copy tokens. The tool must work
with the previously delivered Business pilot and without starting VPNRouter.

Inspect the existing landing/backend/API before selecting receiver placement.
Use the existing HTTPS ingress and hosting capacity. Preserve current landing,
VPN subscription routes, identities, DNS, mail and unrelated workloads. Keep
reports private, bound body/storage/retention, and resolve upload authorization
using the existing infrastructure or scoped credential provisioning. Do not
publish credentials or real employee reports. No new VPN client or fleet agent.

## How

1. Inspect current Homelab routes, hosting source and available receiver/auth.
2. Commit this brief and verify its exact-head CI before implementation.
3. Add a small single-file Windows frontend around the verified scripts, built
   on prepared Windows CI tooling. Inspect compiler availability first; never
   install an SDK or compile on the development/control-plane machines.
4. Implement only the missing receiver boundary and configure the chosen HTTPS
   route after reviewing the concrete source, host identity and deployment path.
5. Exercise synthetic report delivery, refusal, oversized data, loss of network,
   archive retention and the exact employee flow; review independent lenses.
6. Deliver the configured EXE and record real source/build/hosting evidence.

## Verification

1. Build: BLOCKED pending implementation and Windows tool preflight.
2. Tests: BLOCKED pending wrapper/receiver fixtures and exact-SHA CI.
3. Docs: PARTIAL; scope and employee action recorded before code.
4. Review: BLOCKED pending complete diff and independent source review.
5. UI: BLOCKED pending isolated synthetic UI evidence and applicable fixed
   WINBRAT verification; no live desktop or employee-data capture.
6. Characterization: N/A; no application ViewModel/API refactor.

## Outcome

- Status: PARTIAL; read-only discovery in progress; no new production mutation.
- Baseline: all 11 checks green at 77fde270, worktree clean before this brief.
- Homelab: connected through the existing Harness control plane; canonical
  homelab skill and DNS/publication runbook read. Current Caddy is active.
  The live apex route separates VPN config API from the existing landing backend.
  Landing VM identity and running-service inventory observed through its guest agent.
- Unknowns: actual receiver/auth capability, landing source ownership, compiler
  preflight, destination URL and complete real delivery evidence.
