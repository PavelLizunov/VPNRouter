# PR236 unresolved preservation

Closed PR236 `sentinel-redact-pbk-uuid-params-17133188558712116731`, tip `7a40c402cd152f59dc7278873ef034a932e01d82`. Full net diff inspected: CrashReporter query alternatives for public-key/pbk/uuid, DiagnosticsRedactor log alternatives for pbk/public-key, and three query test rows with short synthetic values.

Closure says only 'Conflicts with merged #252', not that the work was incorporated or deliberately rejected. Accepted main2689ee77 lacks the explicit pbk and query uuid alternatives and diagnostics public-key alternatives. Existing generic UUID/long-key scrubbing does not establish equivalence for the short synthetic test values. Generic query key prefix handling may already cover public_key, but cannot by itself account for all three inputs.

Disposition: retain unique policy/test work. Public key classification and invalid/short UUID handling need scoped review; no runtime vulnerability or need to adopt every proposed key is asserted. Do not merge disputed redaction policy as housekeeping, and do not delete a conflicts-only branch as completed. No product edits, tests or deletion performed.
