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

1. Build: BLOCKED pending exact implementation SHA and Windows compiler preflight.
2. Tests: BLOCKED pending exact-SHA Windows EXE and Linux receiver CI fixtures.
3. Docs: PASS for implementation scope; intake runbook, employee instruction and
   Business guide explain private one-file delivery and the preserved admin fallback.
4. Review: PASS for source inspection by separate security, Windows/lifetime and
   tests/CI reviewers. Confirmed findings were recorded before fixes; runtime CI pending.
5. UI: BLOCKED pending isolated synthetic UI evidence and applicable fixed
   WINBRAT verification; no live desktop or employee-data capture.
6. Characterization: N/A; no application ViewModel/API refactor.

## Outcome

- Status: PARTIAL; source implementation/review complete; CI and live delivery pending.
- Baseline: all 11 checks green at 77fde270, worktree clean before this brief.
- Homelab: connected through the existing Harness control plane; canonical
  homelab skill and DNS/publication runbook read. Current Caddy is active.
  The live apex route separates VPN config API from the existing landing backend.
  Landing VM identity and running-service inventory observed through its guest agent.
- Discovery: landing is a running Next.js container with no existing intake in
  the inspected routes. Its source remains unchanged. Read-only host baseline:
  2 CPUs, load 0/0/0, available RAM 1229 MiB, swap used 103 MiB, free disk
  215.68 GiB, Python 3.11.2, no competing build jobs. Repeat before deployment.
- Implementation: classic Windows Framework EXE embeds the verified scripts;
  UI sends with one action, shows progress and acknowledgement, keeps failed
  reports, and cancels its owned child on close. No .NET 10 app startup required.
  Build settings and EXE contain an individual upload identity and stay private.
- Receiver: stdlib Python behind existing ingress, no public report download;
  hashed/revocable identities, bounded ZIP parsing/body/deadline/concurrency and
  private storage/retention. Issuance is locked/atomic; reports have private
  employee metadata and content-based per-identity deduplication.
- Review: support_exe_security, support_exe_windows and support_exe_test_review.
  Registry atomicity, absolute deadline, idle retention, orphan accounting,
  employee mapping, Windows staging ACL, failure text and timeout cleanup fixed
  in source; see SUPPORT-* entries in OPEN-DEFECTS.
- Source checks: Python AST, workflow YAML and `git diff --check` pass. No SDK
  installed and no build/package/full tests run on workstation/control plane.
- Verification limits: offscreen WinForms fixtures use a fake workflow and do
  not establish the real PowerShell bridge or live HTTPS delivery. Fixed WINBRAT
  and authenticated synthetic production delivery remain required.
- Unknowns: compiler/runtime results, selected route's final DNS/TLS/live state,
  deployed service identity and complete real one-button delivery evidence.
- Cleanup: no new production mutation or worker lease yet.
- Initial CI at 5c7e2702 (run 41471898133): receiver 10/11 passed, with the
  persistence fixture exposing response-before-cleanup ordering; native EXE
  compilation succeeded, then the builder failed on unavailable Get-FileHash.
  Correct both without changing the distributed VPN client. Existing script
  and routing checks pass; repeat exact-SHA CI before deployment.
- CI at 97f61e61 (run 41473348670): receiver fixtures all pass; EXE compilation,
  resource embedding and private Windows stage ACL pass. PowerShell Add-Type
  then rejects the EXE reference used by the fake workflow fixture. Compile that
  fake workflow with the same existing Framework compiler and use LoadFrom;
  no production executable behavior change is involved.
