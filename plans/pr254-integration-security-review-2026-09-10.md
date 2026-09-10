# PR #254 integration review

Read-only security-review and ponytail against exact 441b760c750cc79ac91429cfa291882c1e50d3e6, merge-base 43138f7c, accepted main 065a2083. Historical object inspection confirmed two production files match main parent blobs, so only three PR files are in scope. No runtime tests or remote operations by reviewer.

## Source conclusions

No confirmed new security vulnerability in the two guards. AddSubscriptionAsync rejects non-absolute/non-http(s) input before mutation, refresh and persistence. AddUserSource rejects before add/save. Both use localized feedback without logging rejected input. This is supported-scheme validation, NOT SSRF prevention: private/loopback HTTP(S), DNS and redirects are not restricted; legacy persisted paths are outside these add-command guards.

## Required test corrections before acceptance

LOW: plain Theory attributes instantiate ViewModels contrary to Tests zone dispatcher contract; use AvaloniaTheory. FreeConfigsPageViewModel implements IDisposable; dispose both positive/negative fixtures. Preserve data: rejection from #253 (parent read GitHub diff, reviewer lacked local #253 object). Add whitespace/mixed-case/optional-name coverage, save-count baseline and fake HTTP assertions for subscription positives; use existing SubscriptionFetcherCollection and restore static Http in finally. Do not perform live URL fetches.

TestEnvironmentSafety redirects AppPaths and disables background services, so there is no finding that constructors inherently contact live update services in this test environment. Positive subscription tests must still inject fake HTTP and retain side-effect checks.

## Corrective implementation and preservation

Main 9854399d integrated as f569abe0; baseline CI 34487821429 passed all checks. Four AvaloniaTheory methods now cover 30 rows, including original cases, data:/relative rejection, blank-name HTTP(S), mixed-case/trimmed URLs, disposal, save baselines and serialized fake HTTP with finally restoration. Windows CI explicitly includes the class. Parent freshly retrieved #253 test patch via GitHub API: its distinct data: rejection and unnamed valid-source behavior are retained; alternate Windows filename/malformed text/HTTPS hostname do not add distinct behavior. No live HTTP was used by implementation.

## Acceptance

Pending implementation on #254, exact CI and corrective review. #253 closure remains conditional on preserving useful cases and accepted #254. Production guards unchanged by this follow-up; test fixes await exact-head CI and corrective review. This report records evidence for later authorized integration and does not authorize branch deletion or release.
