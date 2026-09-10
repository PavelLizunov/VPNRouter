# PR #232 integration security review

## Scope

Original PR head 6256e9f129d6cef0ddfffa49fc833e3e51f5df9b versus merge-base b7ce0e4f140b7ed4257673aa67a2b359c535ef7f. Independent read-only reviewer used security-review and ponytail; lead inspected ShareLinkHelper, ServerUriParser diff and SubscriptionFetcher exception logging. This is source evidence, not executed differential .NET tests.

## Confirmed findings

1. MEDIUM: duplicate query keys changed from HttpUtility/NameValueCollection aggregation to dictionary last-wins. ALPN loses values; allowInsecure=0&allowInsecure=1 changes generated TLS setting from false to true. Provider already controls explicit insecure settings, so this is not an independent new provider privilege. Repository ledger conservatively tracks the security-setting compatibility regression as P1.
2. MEDIUM: Uri.UnescapeDataString does not apply query form-style raw-plus decoding. Transport paths and authentication values change; %2B and raw + must remain distinct. AWG's protocol-specific plus-preserving parser is unrelated and must remain intact.
3. MEDIUM: custom authority extraction drops URI path boundaries and host/bracket validation. Explicit-port paths can be rejected, path text can enter hostname, malformed bracket suffixes can be ignored.
4. MEDIUM: invalid-port exceptions include raw input. SubscriptionFetcher logs exception text independently of its scrubbed URI argument, potentially persisting token-bearing malformed authority/path text. Existing generic standard-library wrapper errors avoid this introduced reflection.

## Decision

Owner approved narrowing: retain span scheme filtering before per-line allocation, restore existing standard-library component/query parsing, remove unused custom helper, add public behavior regressions. Previous benchmark figures are historical and not valid evidence for this narrower implementation. No new benchmark is authorized or claimed.

## Corrective implementation

VlessUriParser restored byte-for-byte to accepted main; ServerUriParser differs from main only by span overload and supported-scheme filtering before string allocation. Custom ShareLinkHelper removed. Public behavior regressions replace helper-specific tests, including captured subscription exception logs with synthetic secrets. Windows CI now includes PerformanceShareLinkTests; Ubuntu already includes it. Scoped whitespace check passed. No revised performance measurement claimed.

## Corrective source review

Independent reviewer re-read the complete rewritten tests, restored parser and subscription logging path against main. Source verdict PASS: original four introduced blockers removed; no mistaken test assertions found; Windows selection confirmed and git diff --check passed. Runtime URI behavior still requires CI. Unicode/malformed-escape/Naive matrices are not comprehensive, but unchanged stdlib behavior remains in place.

## Verification pending

Independent corrective diff review and actual CI regression execution required before merge. Test matrix: plus/percent decoding, duplicates and insecure flags, ALPN, authority/path/brackets/ports, exception token exclusion and subscription capture logging, scheme filter/string parity. No actual hostile endpoint, deployment or live VPN tests. User-authorized merge remains conditional on green checks and successful review.
