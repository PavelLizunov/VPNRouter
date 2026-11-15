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

- Build: N/A for instructions; runtime code/builds require separate exact-SHA CI.
- Tests: pending schema/examples and actual-report prompt dry run.
- Documentation: in progress, reusable prompt and concrete integration contract.
- Review: pending bounded Terra validation; do not claim Claude reviewed it.
- Runtime: pending actual report retrieval; no new intake/model deployment claimed.
- Integration: pending ownership and compatibility checks against current source.

## Outcome

In progress. This initial record states source-confirmed behavior and bounded
work before changes. Secrets, upload URLs/tokens and raw reports stay outside Git.
