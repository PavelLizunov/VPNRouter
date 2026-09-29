# H-8: second, harder test reduction pass

## Why

After H-7 the owner judged the first classification too lenient. A second,
stricter pass over the 617 uncertain methods (KEEP-medium and MERGE) used the
rule "doubt means delete unless the test guards security, shutdown ordering and
races, kill switch and firewall, user-input parsing, sing-box config output, or
is the only test of a public behavior". The list is kept outside the repository.

## What

Delete 297 test methods in 82 files (94 DELETE, 203 MERGE_DELETE duplicate
variants of a scenario); three classes became empty and were deleted.

## Held back

125 MERGE_DELETE entries whose reason concerns parsing, serialization, cache
recovery, configuration, redaction, firewall or shutdown (variants there often
differ by an edge case), 15 entries in release, tooling and screenshot tests,
and 186 KEEP entries with a stated guard class. Two entries no longer exist.

## Verification

Structure check (brace balance, attribute placement); exact-head CI.

## Outcome

Pending CI.
