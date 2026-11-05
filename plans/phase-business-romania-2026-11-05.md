# Business Romania customer delivery

## Why and scope

The owner reported a bare RO country code and missing flag in Business, then
requested a configured Windows EXE to send to a customer. Preserve the existing
Business provisioning, subscription, diagnostics and trusted update feed. This
is a private installer delivery, not a public Standard release or a stable cut.

## Implementation and invariants

Add Romania to RU/EN names, explicit country labels and the existing vector flag
renderer. Keep other metadata, connection state and selection behavior unchanged.
Cover authoritative RO metadata, its flag, both languages and provider labels.
Advance the private source version to 2.50.7 so the corrected package cannot be
confused with the already issued 2.50.6 payload. Retain the current Business parent
and its final review records. Existing configured inputs remain outside Git.

## Verification and rollback

Use exact-head Linux/Windows tests, Business headless tests/screenshots, Windows
updater checks and the existing encrypted private-installer workflow. Its native
core must accept the generated split/full configs before delivery. Verify source
receipt and installer checksum after decryption. Previous private 2.50.6 installer
remains the rollback path; existing provisioning/config must be retained.

## Observed outcome

Source fix is in PR #652. Initial Business surface and Windows updater jobs passed
at dc81138a; full tests and the final versioned source gates remain pending.
Accessible local workers have no .NET SDK, so no control-plane build or SDK install
was attempted. Required packaging uses the preflighted hosted Windows workflow.
New-node Activity: no data is a separate full-day history limit, recorded in the
ledger; it is not replaced with an unmeasured low-traffic claim. No private values,
raw reports or user configuration are stored in this record.
