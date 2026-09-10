# PR #232 integration security review

## Scope

Original PR head 6256e9f129d6cef0ddfffa49fc833e3e51f5df9b versus merge-base b7ce0e4f140b7ed4257673aa67a2b359c535ef7f. Independent read-only reviewer used security-review and ponytail; lead inspected ShareLinkHelper, ServerUriParser diff and SubscriptionFetcher exception logging. This is source evidence, not executed differential .NET tests.

## Confirmed findings

1. MEDIUM: duplicate query keys changed from HttpUtility/NameValueCollection aggregation to dictionary last-wins. ALPN loses values; allowInsecure=0&allowInsecure=1 changes generated TLS setting from false to true. Provider already controls explicit insecure settings, so this is not an independent new provider privilege. Repository ledger conservatively tracks the security-setting compatibility regression as P1.
2. MEDIUM: Uri.UnescapeDataString does not apply query form-style raw-plus decoding. Transport paths and authentication values change; %2B and raw + must remain distinct. AWG's protocol-specific plus-preserving parser is unrelated and must remain intact.
3. MEDIUM: custom authority extraction changes standard-library authority/path behavior. Explicit-port paths can be rejected and path text can enter hostname. The original claim that ignoring the tested bracket suffix differs from System.Uri rejection is withdrawn: runtime CI showed System.Uri accepts that suffix; the corrected fixture asserts parity (see CI correction below).
4. MEDIUM: invalid-port exceptions include raw input. SubscriptionFetcher logs exception text independently of its scrubbed URI argument, potentially persisting token-bearing malformed authority/path text. Existing generic standard-library wrapper errors avoid this introduced reflection.

## Decision

Owner approved narrowing: retain span scheme filtering before per-line allocation, restore existing standard-library component/query parsing, remove unused custom helper, add public behavior regressions. Previous benchmark figures are historical and not valid evidence for this narrower implementation. No new benchmark is authorized or claimed.

## Corrective implementation

VlessUriParser restored byte-for-byte to accepted main; ServerUriParser differs from main only by span overload and supported-scheme filtering before string allocation. Custom ShareLinkHelper removed. Public behavior regressions replace helper-specific tests, including captured subscription exception logs with synthetic secrets. Windows CI now includes PerformanceShareLinkTests; Ubuntu already includes it. Scoped whitespace check passed. No revised performance measurement claimed.

## Corrective source review

Initial corrective reviewer re-read the complete rewritten tests, restored parser and subscription logging path against main. Historical source verdict PASS reported the original blockers removed and no mistaken test assertions; Windows selection and git diff --check passed. That assertion review was superseded by the runtime bracket-suffix failure below; at this stage URI behavior still awaited CI. Unicode/malformed-escape/Naive matrices are not comprehensive, but unchanged stdlib behavior remains in place.

## CI correction

Run 34486559602 on 7b6d4fd2: Ubuntu 3033 passed / 1 failed / 57 skipped; Windows 194 passed / 1 failed / 0 skipped. It failed exactly one fixture on each OS: .NET accepts the tested bracket suffix, contradicting the source-only assumption that it rejects every suffix. That portion of the authority finding is withdrawn; valid-path and query regressions remain. Replaced the mistaken rejection expectation with direct System.Uri parity for all five protocols. Other malformed-port/bracket generic-error and secret-log assertions remain enabled. This is compatibility verification, not new stricter URI validation.

## Final verification and integration

Independent corrective review PASS on final `4b082e34`, including the revised System.Uri bracket-suffix parity fixture. CI `34487206556`: Ubuntu 3038 passed / 57 skipped; Windows 199 passed; all four checks green. PR #232 merged under owner authorization as `9854399d`. The three ledger items are resolved for the narrowed implementation, which retains only span scheme filtering and restores standard-library parsing.

Test matrix: plus/percent decoding, duplicates and insecure flags, ALPN, authority/path/brackets/ports, exception token exclusion and subscription capture logging, scheme filter/string parity. No actual hostile endpoint, deployment or live VPN tests were performed; no revised performance benchmark is claimed.
