# URL validation integration — PR #254

## Why and approved scope

Owner approved integrating #254 after review/green CI and closing duplicate #253 only after preserving useful test cases. Original head 441b760c; accepted main 9854399d merged as f569abe0, all four baseline checks passed. Production guards remain unchanged during this follow-up. They reject non-absolute and non-HTTP(S) URLs before subscription/free-source mutation with localized feedback. This is not SSRF protection and does not restrict private hosts, DNS, redirects or legacy stored sources.

## Implementation

Correct plain Theory ViewModel fixtures to Avalonia dispatcher attributes, dispose FreeConfigs VMs, use the existing fake HTTP seam and serialized collection. Preserve data-scheme rejection and unnamed valid-source cases from #253, add persistence/HTTP side-effect checks and trimmed/mixed-case positive coverage. Include class in Windows CI; Ubuntu already includes it. No live network or deployment. Scope: existing two product guards, test file, CI selection, this brief and review report.

## Risk and rollback

Keep standard Uri.TryCreate scheme handling; no new URL framework. Restore static SubscriptionFetcher.Http in finally. TestEnvironmentSafety owns temporary paths/background-service suppression. Revert only task commits if necessary; no release/tag rewrite. No benchmark or general security guarantee.

## Six gates

1. Build: baseline CI green, corrective CI pending.
2. Tests: isolated dispatcher/fake-backed regressions pending on Linux and Windows.
3. Docs: review report records limitations; owning README note to be reconciled with cleanup PR #256 after integration.
4. Review: production source review passed; corrective test review pending.
5. UI/deployment: no live GUI/VPN changes executed; tests use headless dispatcher, deployment N/A.
6. Integration: exact-SHA CI, owner-authorized merge, then explicit #253 preservation comparison before closure.

## Outcome

IN PROGRESS. No product code edits during test hardening. Original test defects: plain Theory for ViewModels and undisposed FreeConfigs fixtures; documented in pr254-integration-security-review-2026-09-10.md. No #253 closure yet.
