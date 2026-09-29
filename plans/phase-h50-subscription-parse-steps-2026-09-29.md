# H-50: split SubscriptionFetcher.ParseBody into steps

## Why

`SubscriptionFetcher.ParseBody` (102 lines) decoded the response (JSON wrapper, base64 or plain), split it into share
URIs (Clash YAML mapping or lines), parsed each URI with placeholder handling and de-duplicated the servers, all in
one body.

## What

`ParseBody` keeps the empty-body check, the "large subscription" warning and the return, and calls `TryDecodeBody`
(returns `false` for a JSON response without a `config` field, exactly where the old code returned the empty
list), `SplitToShareUris`, `ParseShareUris` (owns the dropped-placeholder counter) and `DeduplicateServers`. The
statements moved unchanged; the multiset comparison of trimmed lines shows only `string decoded;` and
`result = deduped;` removed and headers, calls, `return`s and the two initialisations that moved into
`ParseShareUris` added.

## Verification

Exact-head CI (full suite, including the subscription parsing, redaction and placeholder tests); read of the diff.

## Outcome

Pending CI.
