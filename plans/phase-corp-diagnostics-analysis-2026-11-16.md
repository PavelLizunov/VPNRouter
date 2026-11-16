# Corp diagnostic analysis and update delivery

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
