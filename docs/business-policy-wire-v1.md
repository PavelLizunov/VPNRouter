# Business policy protocol v1

This contract is shared by VPNRouter and the isolated Corp surface in vpnctl.
Implementation scope: [Corp task](../plans/phase-corp-policy-implementation-2026-11-14.md).
It does not grant deployment or provide production credentials.

## Signed response

`GET /api/v1/corp/policy` returns `application/json`, `Cache-Control: no-store`.
The read-only bearer credential is supplied in `Authorization`, never a URL.
The endpoint is fixed by trusted provisioning. It does not use browser cookies
or the browser IP gate, so a stopped client can retrieve rules. Authorization
selects exactly one enabled corporate policy; no request parameter selects it.

The envelope contains exactly two string fields, `payload` and `signature`, both
canonical padded RFC 4648 standard base64. No whitespace inside either string.
Decoded payload is strict UTF-8 JSON; sign its exact bytes, not a reserialization.
Signature: ECDSA NIST P-256 with SHA-256, IEEE P1363 `r || s`, exactly 64 bytes.
The verifier accepts only a provisioned P-256 SubjectPublicKeyInfo DER public key.
There is no negotiable algorithm. The payload's `key_id` selects a provisioned
key and is itself signed. Accept standard ECDSA signatures; payload SHA-256, not
signature bytes, defines equal-revision identity.

Payload contains exactly these fields (order/insignificant whitespace need not
be canonical; producer emits deterministic compact JSON):

| Field | Type and constraints |
|---|---|
| `schema` | integer `1` |
| `policy_id` | 1-64 ASCII letters, digits, `-`, `_`, `.`; exact provisioned identity |
| `key_id` | same identifier syntax; exact provisioned key |
| `revision` | integer 1 through 9007199254740991 |
| `published_at` | integer Unix seconds 1 through 253402300799 |
| `apply` | `next_connection` or `now` |
| `rules` | 0-512 objects with exactly `domain`, `action`, `comment` |

`domain` is a normalized lowercase ASCII DNS name (IDNA A-label form), at most
253 bytes. `action` is `direct` or `proxy`. `comment` is a UTF-8 string of at most
256 bytes, with no control characters. No embedded rule IDs or process/IP rules
can be set through this protocol. All domains mean the root and subdomains.

Reject duplicate properties at every object level, unknown fields, invalid JSON
types, fractional/exponent-form integers, trailing data, comments and trailing
commas. Maximum JSON depth is 8. Bound the entire decompressed HTTP response to
65536 bytes and decoded payload to 49152 bytes; the envelope limit remains
authoritative when base64 overhead is larger. Bound parsing before allocation.
The server checks final envelope size before atomic publication.

`published_at` is authenticated publication metadata, not a lease or clock-based
authorization. No automatic expiration or wall-clock rollback changes revision
acceptance. A refresh updates last-contact separately from publication age.

## Domains and protected rules

The server accepts a site or HTTP(S) URL in the editor, extracts the hostname,
normalizes with IDNA, removes one terminal DNS dot and shows the result before
publication. It rejects userinfo, IP literals, wildcard/regex, empty labels,
underscores, labels over 63 bytes and labels beginning/ending with `-`.
Wire payloads must already be normalized; clients never silently repair them.
At least two labels are required. Reject numeric IP spellings and public suffixes
using the same pinned Mozilla Public Suffix List (ICANN and PRIVATE sections,
including wildcard and exception rules); no online lookup during validation.
The pinned source and digest are recorded in
[public_suffix_list.source.txt](../VPNRouter.Core/Assets/public_suffix_list.source.txt).
The dataset/version travels with each implementation, not the policy response.

Duplicate domains are invalid. Opposing directions on intersecting parent/child
suffixes are invalid. Same-direction parent/child entries are allowed. Comparison
is label-boundary suffix matching; `notexample.com` does not match `example.com`.

Protected domain suffixes are `uiscom.ru`, `uiscom.co`, `comagic.ru`,
`callbackhunter.com` (direct) and `admin.corp.ninitux.com` (proxy). They and their
intersecting ancestors/descendants cannot appear in the editable list. The client
adds those rules before managed sites, retains the existing UIS IP exception and
embedded process rules, and keeps the portal through VPN in both modes.

Before the first accepted policy, the embedded domain preset seeds behavior.
After acceptance, the signed list completely replaces editable seeded domains;
deletions remain deleted across restart. An empty signed list retains protections
and process/geographic defaults only. Explicit sites precede geographic bypass
and applications consistently in both routing and DNS.

## Provisioning and persistence

Trusted `business.policy` is optional and has exactly `policy_id`, `endpoint`,
`read_token`, `keys`. Endpoint is HTTPS without userinfo/query/fragment. The
reader token has 32-512 ASCII bearer-token characters; it is never a portal
password. `keys` is a nonempty array of at most four objects with exactly `id`
and `spki` (canonical base64 DER P-256 public key), with unique IDs. Malformed,
duplicate or missing policy fields disable policy sync only; existing VPN startup
remains usable. Other Business services retain their existing validation.

Trust/cache namespace is the SHA-256 of UTF-8 policy ID followed by canonical
endpoint, each prefixed by its byte length as an unsigned 32-bit big-endian integer. Adding/removing an overlapping provisioned verification key
or rotating the read token does not reset the revision floor. Key ID changes
never authorize lower revisions. A different policy ID/endpoint is an explicit
trusted migration, never derived from network data.

Journal stores namespace, highest accepted revision, payload SHA-256 and exact
signed envelope in one protected atomic file. Reverify its signature and identity
on startup. Keep revision/digest checks even across a provisioned key rotation;
do not erase the journal automatically when its signer is no longer trusted.
Unverifiable cache disables acceptance until trust is repaired, rather than
silently falling back to an empty revision floor. A missing first-use journal
starts with the embedded seed. A corrupted existing journal is a visible local
policy error, never an invitation to accept an older revision. Keep a running
session on its captured verified rules. If startup cannot recover a verified
policy, block new policy-managed connections with a recovery message instead of
reinstating deleted seed exceptions. This does not change the missing/invalid
optional-provisioning fallback or the valid first-use missing-journal behavior.

Accept a higher revision only after the atomic journal write succeeds. An equal
revision is a no-op only for identical payload SHA-256. Reject older revisions.
No lower-policy retry after a transport, DNS, generation or core-load failure.
An administrator restores content by publishing a new greater revision.
OS backups restoring the entire old journal are outside a local-only replay
guarantee; reconcile them with the current trusted service during recovery.

## Apply lifecycle

Accepted/pending state is separate from the running engine's immutable settings
capture. `next_connection` never changes a running session. `now` requests an
owned apply only after download and durable acceptance; defer while a connection,
stop, update or other apply transition is active. Recheck the current session and
user intent at the engine gate. Never invoke Stop+Start as a policy fallback.
If stopped, wait for an explicit connection. Poll once per minute with jitter,
one request at a time, a 10-second total deadline including body reads, and no
redirect/cookie handling. Cancel/drain on app disposal; visibility is irrelevant.

Only actual core acceptance of the generated configuration marks a revision
applied, paired with configuration digest and engine-run identity. Disconnection
clears active application state, retaining history. Generation/load failures leave
the accepted revision pending. The portal says published, never applied to all.

The desktop receipt uses a fresh private configuration/log pair for each owned
launch. With file logging and timestamps disabled, the bundled sing-box
`v1.14.2-vpnctl.2` emits `INFO sing-box started (...s)` after successful startup
(`log/log.go`, `log/format.go`, `box.go` at `f62085aa7cc0f3ee665ac3043d35698760a386e9`).
Pair that acknowledgement with the exact live handle, session and effective JSON
digest. Shared diagnostic logs are never acknowledgement evidence. Policy-owned
health recovery reuses the current typed configuration; unversioned health or
process-monitor writes cannot replace it. An older queued apply cannot lower the
highest acknowledged policy revision within the same session.

## Browser surface

The canonical host is `admin.corp.ninitux.com`, with an isolated router; an
operator route is not exposed under that host. Gate browser requests by verified
eligible subscription egress before login and on every protected request. Use
fixed Business capabilities, not browser headers, to compute subscription
eligibility. Derive client IP only from the trusted-peer path. If eligibility or
egress cannot be established, deny access with a connect-to-Corp explanation.

Use separate Corp password hash, revocable server-side sessions and Secure,
HttpOnly, SameSite=Strict cookies scoped to this host. Require matching Origin and
session CSRF for writes/logout; rate-limit password attempts. An operator session
does not authenticate Corp and a reader credential cannot publish. Publishing
checks the expected current revision, validates the complete list, signs it and
commits the envelope plus audit atomically. Restore obeys the same process.

## Shared verification

Shared fixtures are [v1.json](../VPNRouter.Tests/Fixtures/BusinessPolicy/v1.json)
and [domains.json](../VPNRouter.Tests/Fixtures/BusinessPolicy/domains.json).
The backend consumes these same public synthetic P-256 and domain cases. No production private key or reader token belongs in
fixtures. Cover altered bytes/signature, wrong identity/key/curve, duplicate
fields, schema/integer/size bounds, empty list, replay/equal revisions, URL/IDNA,
PSL wildcard/exception, protected overlaps, conflicting directions, and both
timing modes. Verify parser tests separately from route/DNS and real lifecycle
tests; neither alone establishes live site reachability.
