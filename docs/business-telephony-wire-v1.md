# Corp telephony policy v1

Telephony is a separate signed feed; [site policy v1](business-policy-wire-v1.md)
and its exact stored envelopes remain unchanged. An upgraded client derives the
phone endpoint by appending `/telephony` to its trusted policy endpoint path,
using the same reader token, provisioned keys and policy ID. No subscription
response may change this authority. Older clients keep their embedded phone
rules and do not fetch this feed. Editable telephony requires Corp2.50.10+.

## Wire

Use the site envelope, P-256/SHA-256 P1363 signature, canonical base64,
strict UTF-8, depth8, byte limits and integer/identifier rules. Payload has
exactly `schema`, `kind`, `policy_id`, `key_id`, `revision`, `published_at`,
`apply`, `rules`. Schema is integer1 and kind is `telephony`. Rules have exactly
`type`, `value`, `action`, `comment`; at most64 rules. Actions and comments
follow the site contract. Type is `domain_suffix` or `ip_cidr`.

Domains are canonical site domains, except the four formerly protected phone
suffixes are editable. The portal domain, ancestors and descendants remain
forbidden. IP ranges are canonical network CIDRs; input normalization masks
host bits and supplies /32 or /128 for literals. Reject IPv4-mapped IPv6.
The current Corp runtime captures IPv4 only; portal publication and client
application reject IPv6 IP rules until IPv6 capture is separately supported.
The portable wire parser still validates canonical IPv6. Duplicate typed
values and opposite directions on intersecting ranges/suffixes
are invalid. Same-direction overlaps are allowed.

## Persistence and application

Phone revisions are independent of site revisions. Both feeds reuse the durable
journal implementation with different scope hashes and independent rollback
floors. An absent feed before first acceptance keeps embedded defaults. Once
accepted, an empty list removes those defaults and survives restart/offline use.
HTTP404, bad signatures and fetch failures never erase an accepted phone feed.

The coordinator publishes one immutable composite snapshot after refresh.
Receipt identity includes both digests and independent revision floors, plus
the current acknowledged launch/configuration digest. Immediate intent remains pending until an applied receipt acknowledges its
category digest. After acknowledgement an unchanged apply-now phone revision
cannot turn a later next-connection site update into apply-now. A phone-only
journal composes with embedded sites when no main journal exists.
Existing same-session cancellation, Stop and process acknowledgement apply.

Compose portal proxy protection, phone rules, unrelated embedded IP exceptions,
managed sites and process rules in that order, preserving old exception priority. An accepted phone feed replaces only
the four built-in phone domains and195.211.120.35/32. Remove that exact historical
Business-owned physical exclusion; preserve all others. New phone direct IPs
use normal TUN direct routing. Proxy IPs inherit domain UDP transport behavior.
Reject proxy ranges intersecting remaining effective physical exclusions,
including mandatory and automatically detected ones, before launch.

## Portal and compatibility

Browser routes use `/api/v1/corp/portal/telephony/{state,normalize,rules,history,restore}`
and existing session/origin/CSRF/verified-egress authorization. Reader route is
`/api/v1/corp/policy/telephony` and retains reader-only authorization. Save uses
expected phone revision and current configuration version with atomic audit.
History restore publishes a new phone revision. Site and phone saves cannot
overwrite each other. Revision0 displays the five embedded defaults without
silently publishing them; an explicit first save may edit or remove them.
The portal distinguishes saved policy from actual client application.

## Validation

Synthetic strict-wire, journal, fetch/coordinator, routing/DNS/UDP and lifecycle
tests run in exact-SHA CI. Delivery additionally requires a real portal edit
and upgraded WINBRAT routing verification. Restore the owner's phone defaults
and preserve the managed-site list after acceptance.
