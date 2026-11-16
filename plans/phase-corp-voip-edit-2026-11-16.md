# Editable Corp telephony

## Intent and authority

The owner needs to edit Telephony (VoIP) in the Corp portal. On 2026-11-16 the
owner explicitly allowed builds necessary for this feature, superseding the
prior no-build constraint for this scope. Target VPNRouter `business`.
Preserve private provisioning, site rules, diagnostics and credentials.

## Scope and approach

Add a separate signed typed telephony feed at the trusted main endpoint plus
`/telephony`, using the same reader/key authority and an independent durable
revision floor. Preserve the existing site wire bytes and journal. The portal
edits domains and IP ranges, route directions and optional notes. An accepted
empty list removes phone rules; an absent feed retains embedded defaults.

Capture both immutable policies for a connection, compare both revision/digest
floors in applied receipts, and compose portal protection, telephony, unrelated embedded IP exceptions,
managed sites and process rules. Remove only the Business-owned legacy
phone physical exclusion when a phone feed is accepted. Retain IP UDP behavior
and reject proxy overlaps with remaining physical exclusions. Changes use the
existing same-session reconnect and Stop gates.

Backend additions cover strict bounded signatures, normalization, atomic CAS,
history/restore and existing browser/reader gates. Full tests/builds run in
exact-SHA CI, never on the development workstation.

## Risk and rollback

Do not regress either independent floor, erase last-good data on fetch errors,
or falsely acknowledge a mixed configuration. The portal remains protected.
Old clients keep embedded phone rules; the portal explains the new version
requirement. Restore the original five phone defaults after live acceptance
and preserve existing sites. Roll back server/assets without deleting journals.

## Six gates

1. Build: pending exact-SHA CI and private Windows installer.
2. Tests: pending strict wire, store, HTTP, composite-floor and routing cases.
3. Documentation: brief and confirmed findings recorded before changes.
4. Review: pending independent diff review and spec-only backend tests.
5. UI/live: pending portal save and fixed WINBRAT domain/IP routing acceptance.
6. Integration: preserve v1/provisioning, DNS/UDP and Stop/restart contracts.

## Outcome

Source implementation and independent correctness/lifetime/security reviews
completed. Confirmed findings are recorded in OPEN-DEFECTS.md and corrected:
phone-only embedded-site fallback, immediate intent until acknowledged,
unrelated IP priority, unsupported IPv6 before persistence and signed phone
history restoration. Independent backend spec tests were written from the
contract; normalization ambiguity was clarified before their correction.
Local diff checks pass. Build/full tests remain not-run locally under the
control-plane prohibition; exact-SHA CI and live acceptance are pending.
Version2.50.10 is reserved for this new private Corp build.
