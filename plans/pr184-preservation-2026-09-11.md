# PR184 basic-auth preservation

Closed PR184 branch `sentinel-fix-basic-auth-scrubber-18275225611838626866`, tip `2bd2d0e384e45eaf64fece986671094bfe4018f6`. Full merge-base scope: CrashReporter regex/replacement, one test and four-line historical .jules note. Closure identifies merged #201 replacement.

Accepted main `2689ee77` CrashReporter.cs174-176 and203-206 separates optional userinfo from authority and never emits the userinfo capture. Its suffix handling additionally covers query/fragment without slash. Current ScrubSecrets_StripsHttpBasicAuthCredentials (tests75-83) asserts credential and path removal plus exact retained authority with port8443 and redaction suffix. This preserves original requirement and strengthens port coverage; literal secret/name differences are not unique functionality.

Historical note's useful lesson retained: preserving diagnostic hostname must not preserve optional userinfo@ credentials; keep scheme, credentials, authority and suffix distinct during redaction. The note's old incident date is not new runtime evidence. No additional original work remains unaccounted for; no new runtime check executed.

Disposition: completed and eligible for exact-tip cleanup under standing owner authorization. Full tip is an identity receipt, not independent object backup. Remote deleted with exact-SHA lease after fresh tip, no-worktree and accepted-merge ancestry checks. Fresh remote absence verified; no local branch or product change.
