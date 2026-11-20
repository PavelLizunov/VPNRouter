# Business diagnostics receipt size

- Branch: dsh/business-diagnostics-result-size-20261120
- Base: 0746df68f2ce0b090871c60b1fdea9aecf925d22
- Risk: Low. Receipt serialization is internal UTF-8 JSON.
- Rollback: Revert the receiver change. Previous readers accept UTF-8 JSON.

## Why

Business reports can be accepted while their summary remains unavailable. A
synthetic, schema-valid Russian result at 23,953 UTF-8 bytes was rejected when
writing a receipt because the escaped result alone grew to 65,540 bytes.

## Scope and verification

Write the exact receipt bytes counted by the existing limit and disk quota.
Keep both configured limits, result validation, receipt retention and atomic
replacement. Exercise the production worker path with a synthetic boundary
fixture and run the stdlib diagnostics tests and Business CI. Use DeepSeek for
independent review. The change is server-only and needs no app package or UI
snapshot.

## Outcome

The baseline worker returned unavailable for the synthetic boundary fixture.
The receiver now atomically writes the exact counted UTF-8 bytes. The fixture
publishes the analysis, preserves mtime and restores the original result from the
saved receipt. An independent DeepSeek Max review found no production blocker. The regression
now has margin beyond the old bound, exercises actual UTF-8 quota accounting,
and covers a lone surrogate in legacy metadata. All 54 focused analyzer/receiver
tests and the diff check passed.

The latest production report had a model response and no published analysis.
A separate synthetic call to the configured provider passed validation. The
original result was discarded, so its exact rejection reason is unavailable.
The first implementation head passed hosted CI. Updated fixture CI and deployment are pending.

## Client receipt follow-up

The affected Windows upload was received at 11:18:17 UTC and the deployed
server published its analysis at 11:19:04 UTC. The client polled three times
at one-second intervals, then displayed unavailable while the result was
still pending.

Keep one upload and the existing HTTPS, authorization, content and JSON
limits. Poll the same receipt every three seconds for up to two minutes.
Display pending when this wait ends with the server still pending. Preserve
terminal responses. A deterministic clock fixture covers readiness after
48 seconds and expiry with a pending receipt. Run the exact-head Business
suite and review. The runtime gate is a real Business menu send through
ready and the visible summary on Windows. Rollback is the scoped client
commit and leaves accepted uploads intact.

The receiver also returned unavailable before the worker populated analysis.
An opted-in upload for the configured analysis identity now returns pending
while queued, running or retrying, and unavailable on terminal failure.
The receiver/worker fixture observes pending before and during analysis and
the sanitized ready result afterward. The client distinguishes a wait that
ended from a terminal server failure and retries transient poll errors.
