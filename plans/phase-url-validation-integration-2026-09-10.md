# URL validation integration — PR #254

## Why and approved scope

Owner approved integrating #254 after review/green CI and closing duplicate #253 only after preserving useful test cases. Original head 441b760c; accepted main 9854399d merged as f569abe0, all four baseline checks passed. Production guards remain unchanged during this follow-up. They reject non-absolute and non-HTTP(S) URLs before subscription/free-source mutation with localized feedback. This is not SSRF protection and does not restrict private hosts, DNS, redirects or legacy stored sources.

## Implementation

Correct plain Theory ViewModel fixtures to Avalonia dispatcher attributes, dispose FreeConfigs VMs, use the existing fake HTTP seam and serialized collection. Preserve data-scheme rejection and unnamed valid-source cases from #253, add persistence/HTTP side-effect checks and trimmed/mixed-case positive coverage. Include class in Windows CI; Ubuntu already includes it. No live network or deployment. Scope: existing two product guards, test file, CI selection, this brief and review report.

## Risk and rollback

Keep standard Uri.TryCreate scheme handling; no new URL framework. Restore static SubscriptionFetcher.Http in finally. TestEnvironmentSafety owns temporary paths/background-service suppression. Revert only task commits if necessary; no release/tag rewrite. No benchmark or general security guarantee.

## Six gates

1. Build: PASS, all four corrective CI checks green on final `23409668`, run `34489036398`.
2. Tests: PASS, Ubuntu 3068 passed / 57 skipped; Windows 229 passed, including isolated dispatcher/fake-backed regressions.
3. Docs: review report records acceptance and limitations; owning README URL note remains assigned to cleanup PR #256, not a claim of completed public documentation here.
4. Review: PASS, production source review and independent corrective review of `23409668`.
5. UI/deployment: no live GUI/VPN changes executed; tests use headless dispatcher, deployment N/A.
6. Integration: PASS, owner-authorized #254 merge `1d21404a`; fresh GitHub API test comparison confirmed #253 data: rejection and blank-name HTTP(S) cases preserved before #253 closure. Its branch is retained.

## Outcome

ACCEPTED AND MERGED as `1d21404a` after review of `23409668` and green CI `34489036398`. No product code edits during test hardening. Both test ledger defects are resolved: dispatcher attributes and IDisposable fixture cleanup, with save-count and serialized fake HTTP side-effect coverage. See pr254-integration-security-review-2026-09-10.md. #253 is closed after preservation comparison, branch retained. This does not establish SSRF protection, live endpoint/deployment acceptance, release authority or permission to merge cleanup PR #256; overall repository cleanup remains IN PROGRESS.
