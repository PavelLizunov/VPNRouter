# PR187 Telegram URI preservation

Closed PR187 remote `jules/security-scrub-tg-proxy-uri-2483266409785227479`, tip `32f17420193e2316053da881f55bff184308263a`. Full merge-base scope is one added tg protocol alternative and one theory row only. Accepted main `2689ee77` CrashReporter.cs171 already includes tg in the same full-URI scrubber; replacement202 emits only scheme plus [redacted].

Current tests54-60 retain a tg theory row with the same assertions, while dedicated ScrubSecrets_RedactsTgUri64-71 independently asserts server, port and secret removal and exact tg://[redacted] presence. Literal fixture address and token changes add no distinct behavior; dedicated short-token coverage is not dependent on generic long-key scrubbing. Original production/test requirements are fully preserved; no useful unique work found. Source comparison only, no new runtime run.

Status: eligible under completed-work deletion authorization after fresh exact-tip/worktree and accepted-main checks. Full tip records identity, not an independent archive. Remote ref deleted with exact-SHA lease after fresh tip, main identity and no-worktree checks; fresh remote absence verified. No product/local-ref change.
