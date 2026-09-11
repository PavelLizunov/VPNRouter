# PR191 userinfo preservation

Closed PR191 remote `jules-5205101294095015899-c2c0193b`, tip `fe8e5c9c28aa378c0f2235cedffe72789b543efe`. Entire merge-base diff inspected: HTTP userinfo regex/replacement and one regression test only. No hidden ancillary files.

The same requirement was compared against accepted main2689ee77 in pr184-preservation-2026-09-11.md: current scrubber discards optional userinfo, retains authority and strips path/query/fragment. Current ScrubSecrets_StripsHttpBasicAuthCredentials explicitly rejects credential and path and asserts the redacted result with port8443. PR191's literal username/password and path variants do not exercise a distinct unpreserved branch. Useful work fully preserved by accepted #201; no new runtime verification claimed.

Status: eligible for completed-work deletion after fresh exact-tip/worktree and accepted-merge checks. Full tip identifies original history, not independent archive. Remote ref deleted with exact-SHA lease after fresh tip, no-worktree and accepted #201 ancestry checks; fresh absence verified. No product or local-ref change.
