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

1. Build: PASS at cbdea026b3c25e27ef035559a9bb4caaf705626c. Public CI
   41474244965 builds the shared solution and support fixture; private Windows
   run 41477817744 builds the configured EXE with the existing Framework compiler.
2. Tests: PASS. Eleven receiver fixtures and 22 Windows Business tests pass;
   one private-profile fixture is intentionally skipped in public CI. All eleven
   applicable public PR checks pass at that source SHA.
3. Docs: PASS for implementation scope; intake runbook, employee instruction and
   Business guide explain private one-file delivery and the preserved admin fallback.
4. Review: PASS for source inspection by separate security, Windows/lifetime and
   tests/CI reviewers. Confirmed findings were recorded before fixes and closed
   against the subsequent successful CI; see SUPPORT-* in OPEN-DEFECTS.
5. UI: PASS. Isolated synthetic WinForms screenshot inspected from public CI;
   the real configured EXE completed one-button delivery on fixed WINBRAT.
   No live desktop capture was made. See the adapter limitation below.
6. Characterization: N/A; no application ViewModel/API refactor.

## Outcome

- Status: DELIVERED; configured private EXE and HTTPS receiver verified together.
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
- Verification limits: offscreen WinForms fixtures use a fake workflow. The
  separate WINBRAT run below establishes the real PowerShell bridge and HTTPS
  delivery. Failure/retry/cancel behavior has fixture coverage; a real network
  interruption and cancel during upload were not exercised on WINBRAT.
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

## Deployment and real delivery

- Source: cbdea026b3c25e27ef035559a9bb4caaf705626c; receiver SHA256
  c5ff30307279fe038a77ccfe152d694d918ae15e586b075d992fbe82eaacee41.
  Private EXE build run: 41477817744. Source/build sidecars checked after decrypting
  the private CI artifact; no plaintext configured EXE was uploaded publicly.
- Personal EXE: VPNRouterBusiness-Diagnostics.exe, 28672 bytes, SHA256
  4b77304721ae7b90c5ac8ab570e66c8349a78ca2e43494599973f7a9bc6253ce.
  Delivered privately outside Git with matching hash and sanitized verification
  receipts. It is scoped to the first employee; issue a separate identity/file
  for each additional employee. The executable is unsigned.
- Hosting: dedicated service account and systemd unit on the existing landing
  host, existing Python runtime, private state/registry/report permissions.
  Fresh deployment preflight: 2 CPUs, load 0.009/0.016/0, available RAM
  1238 MiB, swap used 103 MiB, free disk 215.68 GiB, Python 3.11.2.
  No SDK, container rebuild or new VM was required.
- Ingress: one new hostname on existing Caddy; configuration validated before
  reload. Existing wildcard DNS already resolves it, so DNS was not edited.
  Backend INPUT policy initially caused HTTPS 502; a single persistent rule now
  allows only the observed ingress source to this service port. Other firewall
  rules and landing/API sources were preserved. Expanded host details remain
  outside tracked content.
- Public checks: trusted HTTPS health 200, existing landing 200, unauthorized
  upload 401, report GET 404. A full oversized public upload closed without
  acceptance; receiver CI separately verifies the 413 size rejection. Private
  state/reports are 0700 and registry/settings were 0600.
- WINBRAT preflight immediately before the scenario: 8 logical CPUs, CPU load
  1 percent, available RAM 13556 MiB, swap 0, free C: 116.1 GiB, no competing
  build/VPN processes or TUN; existing Framework runtime and interactive tester.
  The sequential worker pool lease was acquired through the official mutex.
- Exact-byte EXE launched under the interactive tester with Limited run level.
  The test created one owned synthetic Business log, pressed the visible Send
  button, observed the delivered acknowledgement and found one 1614-byte report.
  The production receiver independently verified the same archive SHA, synthetic
  marker and employee mapping. No VPNRouter app start or VPN connection was needed.
- Test adapter: an identical EXE was named VPNRouter.App.exe in the owned test
  directory for the fixed verifier's process lookup. This Windows Framework UIA
  provider reports native controls as Pane without InvokePattern. A task-only
  verifier adapter clicked the exact process/name/native button using bounded
  BM_CLICK; identity guards were retained. Status acknowledgement used UIA.
  Neither adaptation changes the delivered EXE or shared verifier source.
  An initial temporary scenario encoding failure was corrected before this run.
- Cleanup: private extracted script/token staging was absent after success.
  Synthetic server report/metadata, temporary plaintext settings, Windows fixture,
  test binary, tasks and task directories were removed. Existing legacy launch
  data was preserved. Controller artifacts and both ephemeral CI secrets were
  removed. Worker lease released successfully; no VPN process/TUN remained.
- Limits: on-demand logs, not continuous connection telemetry. Automatic masking
  is best effort and reports may retain domains/IPs/application names. No real
  employee/UIS scenario, code signing or long-run memory claim is established.
