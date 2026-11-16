# Corp diagnostic analysis and update delivery

Status: delivered on 2026-11-16. The preparation and implementation checkpoints
below preserve their original scope and observations. The final outcome and
verification gates at the end supersede their pending runtime statements.

## Intent

The owner reports that Windows says the installed version is current, has sent
support diagnostics, and requests reports and a Terra-like model explanation on
the same Corp administration site. Prepare concrete analyzer instructions from
the actual submitted report and the existing intake/reply contracts. Use
phase-task-launcher; no workstation/harness build or full tests.

## Confirmed scope and invariants

- Diagnose the current Windows report using its actual device/time/build data.
  Keep the prior Linux routing incident separate.
- The verified private Corp installer and trusted update manifest both say
  2.50.8; the client offers only a strictly newer semantic version. A new private
  build is not automatically published to the shared update channel.
- Preserve complete provisioning, support identity, credentials, reports and
  signed-policy journal/floor. Never rewrite an immutable version/tag or claim
  a newly numbered manifest is a delivered, tested update.
- Reports are already accepted by the authenticated support receiver. Its
  optional analysis receipt supports pending/ready/unavailable, but the receiver
  does not invoke a model. Preserve legacy POST and scoped GET compatibility.
- View Corp reports/analysis through admin.corp.ninitux.com after the existing
  verified VPN-IP and password gates. Uploads must still work when VPN is down;
  the existing trusted upload identity remains the authorization boundary.
- Terra is an analysis worker, not an operator: log content is untrusted data;
  no commands, downloads, network probes, credential requests or configuration
  changes are granted by a report. Keep raw reports private and retain full
  owner recovery data. Send a bounded, independently scrubbed analysis packet.

## Work and delivery

1. Find the latest submitted report through authorized management access,
   preserve its complete bytes/receipt, then prepare a sanitized evidence packet.
2. Write reusable model instructions and a structured verdict schema with
   user-facing Russian output and receipt-compatible short RU/EN summaries.
3. Define the minimal inbox/job/result contract for the existing Corp site,
   including ownership, failure/retry behavior and model/version provenance.
4. Validate the prompt on the real report using the requested Terra model;
   identify confirmed versus probable causes and missing evidence explicitly.
5. Publish the instructions, findings and observed validation through a green
   PR to business. Product integration/deployment claims require actual code,
   exact-SHA CI and authenticated end-to-end evidence; instructions alone do
   not establish a running analyzer or website inbox.

## Risk, rollback and unknowns

Main risks are exposing raw support secrets, mixing employee identities or
historical failures, treating same-version packages as newer, and presenting a
model guess as an established diagnosis. Existing collector masking is best
effort. Keep the original archive and prior credentials; remove no identity or
report to make a case appear healthy. Instruction rollback is a normal new
commit; runtime behavior is unchanged by this planning receipt.

Unknowns: actual submitted receipt/build, current receiver storage/worker path,
production model invocation contract and whether there is any existing analyzer
outside this source tree. Verify these rather than infer them from templates.

## Six gates

- Build: N/A, instruction-only change; no runtime code or local build.
- Tests: PASS, actual reviewed case schema/bounds, every evidence reference and
  receiver.public_analysis executed successfully. No full local test run.
- Documentation: PASS, reusable system instruction, result contract, website/job
  boundaries and acceptance examples in docs/business-diagnostics-analyzer.md.
- Review: PASS for this preparation, two requested Terra dry runs plus lead
  source review. Lead corrected excerpt-context and historical-verdict gaps.
  No independent runtime-code review or deployed worker is claimed.
- Runtime: PASS for intake evidence, real owner Windows upload found and full
  ZIP/receipt preserved in verified permanent private archives.
- Integration: PASS for existing public-reply compatibility and packet evidence;
  website inbox/continuous analyzer integration remains design, not deployment.

## Outcome

The actual Corp report was received on 15 November at22:19:45UTC,25,315bytes,
from a Windows device distinct from WINBRAT. Its full source archive is retained
before parsing; no raw report, credentials, device names or receipt IDs enter Git.
Both historical fatal reports are dated6November (versions2.50.6 and2.50.8);
source log data does not establish a current tunnel/policy failure. Client report
formation time is approximately11hours ahead of receiver time, with unknown cause.

Source and live trusted manifest confirm both the corrected private installer
and update channel use2.50.8. CheckAsync offers only a strictly greater version.
This establishes the private delivery gap, not the exact installed source on the
owner's Windows machine. The already verified private installer can be installed
directly; no new shared update/version was published by this task.

The first keyword-only packet omitted short crash headers. The corrected packet
preserves full bounded app/crash context; a repeated Terra run correctly dates
historical crashes. Lead tightened current-scope semantics after its broad
runtime verdict still relied on historical events. Original model outputs remain
private alongside the reviewed result; corrected instructions and the existing
receiver validator enforce the final contract.

Initial brief/ledger commit83a7a93d passed all9 exact-head checks. The final
instruction commit receives fresh CI. No receiver service, website routes,
production model worker, credential, upload identity or public analysis receipt
was changed. Immediate automatic integration versus preparation-only scope is
being clarified with the owner; this document cannot establish activation.

## Owner-confirmed runtime continuation

The owner explicitly selected immediate automatic analysis and the Diagnostics
section, then requested a new version number and mkvt24.ru direct routing.
Implement the companion runtime contract, preserve legacy intake/replies, and
complete real model-to-inbox verification before claiming activation. Prepare
private Corp2.50.9 and its same-source shared artifact; preserve2.50.8 and all
credentials/history. The older mkvt24 PR#674 conflicts with current business;
port its narrow preset/regression changes without importing historical ledger
content. Also update the authoritative signed company policy, because accepted
remote rules replace embedded sites. CI-only builds/tests and trusted WINBRAT
preflight/lease still apply. Backend target remains vpnctl main; client/support
packaging target remains business. Runtime code/spec review and deployment gates
are pending; the preparation-only gate results above are historical evidence.

## Runtime implementation checkpoint

The receiver now implements the private identity-scoped inbox/retry interface.
The independent stdlib worker builds scrubbed, bounded evidence packets and
publishes only validated model results, preserving original ZIPs, receipt fields,
retention timestamps and prior results. The vpnctl task branch implements the
existing Corp-gated Russian inbox and typed private service bridge without a
schema migration. Sixteen independent spec-only HTTP/configuration tests cover
the bridge; separate CI-only browser fixtures cover narrow320/420 layouts,
navigation, evidence, retries, hostile text and unavailable states.

Three independent review passes covered backend correctness, cross-component
security and support concurrency/lifetime. Confirmed findings were ledgered
before correction: ZIP symlinks, synthetic session/CSRF shape, packet citations,
Responses reasoning output, lock acquisition failures, stable masking aliases,
provider total deadline, additional private paths, generated commands, stale
receipt scope and upload quota headroom. Small final corrections received lead
source review; runtime tests are not yet claimed green.

Local static checks pass: Python AST, Rust formatting, JavaScript syntax,
whitespace and redacted gitleaks over both complete task diffs. Compilation,
packaging, full tests and browser execution are not run on the control-plane
workstation; exact-head GitHub CI is the publication gate for both repositories.
The prior instruction-only head a4a6a442 passed all9 checks.

The persistent gateway advertised gpt-6.1-terra and completed a minimal Responses
request using that exact model with store=false. Its full current configuration,
model key and new separate bridge bearer were permanently retained through
owner-access before use. No production worker, inbox deployment, report result,
Windows build or feed activation is claimed by this implementation checkpoint.
New source says2.50.9 and the embedded preset includes mkvt24.ru; signed company
policy publication and actual Windows direct route/DNS verification remain open.

Rollback: deploy the prior exact receiver/backend binaries, stop the optional
worker and remove only its private integration configuration if required;
preserve every credential version and report. The new Windows build must retain
complete provisioning, subscription/support fields and signed policy floors.

## Real deployment acceptance follow-up

PR679 was accepted into business as4f5ed340 after all14 applicable checks passed
at38ae56d8 (three manual private delivery jobs skipped). The receiver was staged
at that exact source, remains healthy, and uses a separately retained service
bearer. No worker was enabled before a real packet dry run. That run discovered
a source-line mapping failure on trailing blank lines; the model received no
packet and no result was published. A narrow follow-up preserves source line
boundaries with synthetic empty/trailing-line coverage and fresh CI. Windows
private/shared2.50.9 builds remain pinned to accepted38ae56d8; this support-only
repair changes no client binary, provisioning, version or embedded rules.

The corrected real packet prepares8files/220cited lines, including40historical
and zero current lines. A retained unaltered Terra response exposed a second
strict-publication gap: the model correctly cites the existing file inventory
for missing-current-data evidence, while validation initially admits only
scalar metadata. A narrow follow-up admits files as unknown-scope evidence only;
the runtime-failure proof guard remains unchanged. No failed result was published.
The exact UTF-8 original was recovered through an ASCII-safe management transfer;
the earlier decoded transport capture is retained separately. It also demonstrates
prompt ambiguity around current clock observations versus unknown packet scope.
The executable scope rule is now explicit in the instruction; fresh automatic
analysis must validate before any ready/publication claim.

Automatic Terra analysis and authenticated HTTPS list/detail are now verified
ready for the original owner report, with unknown current runtime/tunnel health
and a separately identified evidence-quality problem. Final retry source review
found that a failed later attempt can misattribute a retained result's provenance.
The narrow repair preserves the result/provenance pair and stores last-attempt
metadata separately; synthetic tests cover invalid output and receipt rollback.
Windows private upgrade and physical mkvt acceptance continue independently.

## Final delivery outcome

The owner-authorized runtime continuation is complete. The Corp portal now serves
the Russian Diagnostics inbox at https://admin.corp.ninitux.com/diagnostics after
the existing Corp VPN egress and password gates. Reports retain their original
upload identity, archive and receipt time. The continuous worker sends bounded,
scrubbed evidence to gpt-6.1-terra through the persistent gateway Responses API
with store=false, validates its structured reply, and publishes the explanation,
evidence and model provenance. The model receives no operator capabilities.

Both distinct real reports reached ready automatically, including the owner's
original report and a new report sent through the Windows UI. A final read at
01:56:20 UTC confirmed both ready after the verified current-feed context changed
to 2.50.9. Requested and returned models both equal gpt-6.1-terra. These are actual
automatic results; manually reviewed draft output was never substituted for them.
The original report's historical crashes and client/receiver clock disagreement
do not establish a current runtime or tunnel failure. Current health remains
unknown when the report lacks fresh evidence.

Windows Corp 2.50.9 was built from accepted source
38ae56d8f1a4fca968824550802c02f12218f7fb. The private installer and same-source
shared update retain separate delivery contracts. The full private installer
preserves the original complete provisioning; the shared update preserves
existing private data and cannot provision missing policy trust. The owner's
exact old installed source is unknown, so the full private installer remains the
appropriate first migration path when that trust is absent.

The trusted Business feed was activated at 01:36:50 UTC. WINBRAT exercised the
ordinary update UI from 2.50.8 through offer, download, apply and relaunch; all
278 installed shared files matched the verified artifact. Reconnection then
verified the current owned core/config acknowledgement, signed revision 7 and
authenticated portal access. Full provisioning, every nonempty server identity,
selected endpoint, mode/language, support/report identity and policy journal/floor
were retained. Four protected files remained byte-identical through the update
and reconnect. The Standard installation was unchanged. This verifies WINBRAT;
it does not assert that the owner's personal Windows machine was updated.

The signed company policy now contains revision 7 with 33 rules: the original
32 rules and comments plus mkvt24.ru direct. The policy identity and signer are
unchanged. The explicit suffix rule covers the root and subdomains and precedes
the geographic rule; matching DNS uses the local/direct resolver in split and
full modes. Real root-domain HTTPS returned 302 with certificate verification
and native curl exit code 0 in both modes. Physical captures showed bidirectional
direct payload, a separate successful VPN HTTPS control and zero capture drops.
The suffix route and DNS configuration are also covered by config regressions;
a live subdomain endpoint was not used as an independent traffic probe.

Production backend 0.9.0+561f273 was deployed once at 00:33:25 UTC and verified
healthy at 00:33:35 UTC. Eleven protected tables, SQLx migration count/history
and all credentials were unchanged at deployment. The active support worker is
9258a79d; its receiver remains at 38ae56d8 and prompt at 0e39c5e7. The prompt
SHA256 is 7c458271ada117cca04e7e384a5be871eff866690028fceb37ce16721de50c3d.
The compatibility deployment introduced no database migration.

Acceptance also exercised a real CSRF-protected retry: HTTP 202/pending retained
the previous validated result and provenance, followed by a new ready result
and run without changing receive time. Final retry hardening preserves the old
result/provenance pair on invalid output and receipt-budget rollback, recording
the unsuccessful attempt separately.

## Accepted source and final verification gates

| Component | Accepted PR and exact source | Observed CI |
|---|---|---|
| Client, receiver, analyzer, version and embedded direct rule | [VPNRouter #679](https://github.com/PavelLizunov/VPNRouter/pull/679), 38ae56d8 | 14 passed, 3 manual jobs skipped |
| Source-line preservation | [VPNRouter #680](https://github.com/PavelLizunov/VPNRouter/pull/680), 15d2ab85 | 12 passed, 3 manual jobs skipped |
| Packet citations and temporal instruction | [VPNRouter #681](https://github.com/PavelLizunov/VPNRouter/pull/681), 0e39c5e7 | 12 passed, 3 manual jobs skipped |
| Retained-result provenance | [VPNRouter #682](https://github.com/PavelLizunov/VPNRouter/pull/682), 9258a79d | 12 passed, 3 manual jobs skipped |
| Corp inbox, private bridge and spec/browser tests | [vpnctl #333](https://github.com/PavelLizunov/vpnctl/pull/333), 561f2732 | All 13 passed |

- Build: PASS. Exact-source hosted build, headless publish and Business Windows
  artifact workflows passed. Shared package workflow 43778860872 used 38ae56d8;
  artifact digest, source metadata, archive and HTTPS download were verified.
  No build, package or full test run was performed on the control plane.
- Tests: PASS. The final worker source CI discovered 4,013 Core tests:
  3,932 passed and 81 skipped. All 44 receiver/analyzer fixtures passed. Backend CI includes 16 independent spec-only
  bridge tests and browser verification. Relevant regressions cover source lines,
  unknown-scope inventory citations, freshness, deadlines, masking, upload quota,
  symlinks and retained result/provenance on failure and budget rollback.
- Documentation: PASS. Analyzer instructions, runtime contract, this outcome and
  the task's confirmed-finding dispositions are recorded. Final receipt edits
  change documentation only; their PR must receive its own exact-head CI.
- Review: PASS. Independent backend correctness, cross-component security and
  support lifetime reviews were completed and findings source-verified before
  fixes. Narrow follow-up fixes received lead source review and regressions;
  no additional independent review is claimed for these documentation edits.
- UI/runtime: PASS. CI browser fixtures at 320/420 widths were visually inspected.
  Real WINBRAT no-VPN access returned 403; Corp VPN login returned 200, password
  submission 303 and authenticated list/detail 200. Missing session returned 401,
  missing CSRF 403 and unknown report 404. Internal support routes remain 404
  publicly. Actual diagnostic upload, analysis, update and reconnect passed.
- Integration/preservation: PASS. Verified full private installer, ordinary
  shared update, signed revision 7, split/full direct DNS and physical HTTPS,
  positive VPN control, original policy preservation and protected-data hashes.
  Mechanical split characterization is N/A; no mechanical extraction occurred.

## Evidence, cleanup and recovery

Private installer: VPNRouterBusiness-Setup-v2.50.9.exe, 63,092,804 bytes,
SHA256 86efd476ba90b6a9911054b7a68a80f3077780491f601be5d4cc23c21d653ca9.
Shared update: VPNRouterBusiness-update-v2.50.9-win-x64.zip, 73,626,892 bytes,
SHA256 ccfb5b6564afdfd2370407632c52b04567b6351db7c83cb337bdcbacbf37670e.
Old 2.50.8 assets, feed backup, original reports, model responses, provisioning,
all credential versions and the pre-deployment integrity-checked database backup
remain privately retained. No public stable tag or general release was created.

The complete Windows receipt/evidence archive was verified, including final
app/core/TUN/publisher counts of zero and removal of the owned traffic probe.
Lease 42 was canonically released at 01:51:58 UTC; no Windows action followed.
The final real report/provenance archive was also permanently saved and verified.
Raw reports, device identifiers, credentials and expanded operational evidence
remain outside Git under the owner's permanent private archive.

Several acceptance failures were task-oracle defects: Windows ZIP separators,
current lease parsing, canonical subscription labels/pool size, a null process
ExitCode, a PowerShell alias collision and an empty legacy fallback UUID. Each
was recorded before correction. Final verification retained strict checks for
all actual identities, endpoint fields and protected data; no product behavior
or subscription data was changed to satisfy those oracles.

Rollback retains the exact prior backend/support binaries and recovery data.
Stop the optional analyzer if needed without deleting reports or credentials.
Do not roll back signed policy floors or rewrite an immutable Windows asset;
any later client correction requires a newly numbered delivery. No further
runtime operation is required for this task.

## Owner-requested answer hierarchy follow-up

On 16 November the owner requests clearer bot instructions, plain user answers,
report levels and an immediate solution where one is established. Refine only
the system instruction: keep corp-diagnosis/1 and all existing evidence/freshness
and advisory-only guards. The companion vpnctl change presents short answer,
action and disclosed technical detail. A supported remedy comes first; unknown
cases receive one diagnostic action without a speculative reinstall/reset.

Verify prompt diff/bounds/compatibility and exact-head documentation CI; repeat
automatic analysis of both retained real reports after activating the accepted
prompt. No Windows build, version bump, provisioning or policy change is needed.
Rollback restores the retained exact prompt and preserves all report results and
credential versions. Outcome and real semantic review remain pending.

The owner then explicitly selected DeepSeek from OpenCode Go and supplied its
token through the secret card. The full token was permanently retained and
verified before use. Official Go documentation and the live model catalog expose
deepseek-v4-pro through the OpenAI-compatible chat endpoint. A minimal request
confirmed the service requires a stable conversation session header; with that
header and our own User-Agent it returned HTTP 200 and valid JSON from the exact
requested model. Add only the missing worker transport metadata, then repeat
synthetic tests, independent review, exact-source CI and real-report acceptance.
No fallback provider or new Windows version is authorized by this selection.
