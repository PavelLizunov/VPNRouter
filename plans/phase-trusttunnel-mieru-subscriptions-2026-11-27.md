# TrustTunnel and Mieru from vpnctl subscriptions

Base `c12c9d91897de12d58cbe3dc62393cd3f3ff2460`. Core release
`v1.14.5-vpnctl.1` is built from `74153cb4e936af1b846cfa26053981daedd61f81`.

## Why

Users should be able to add or refresh a vpnctl subscription, select a
TrustTunnel or Mieru server and connect using the bundled core.

## What

- Keep HTTPS capability negotiation in `SubscriptionFetcher` and use the
  selected runtime's TrustTunnel and Mieru support. Preserve the legacy header
  and profile output for existing cores and HTTP sources.
- Teach `ServerUriParser`, the subscription parsers and the cached profile
  model to carry the two direct profile types to `ConfigGenerator`.
- Map the TrustTunnel TLS server name, pin, QUIC and UDP options. Map the
  Mieru transport, multiplexing and handshake mode. Keep credential values
  in the existing redaction and persistence boundaries.
- Pin the released sing-box archives and Android AAR in the existing release
  paths and remove Cronet from the bundled paths. Use the existing UI for
  displaying, selecting and refreshing profiles.

## How

1. Add capability checks for desktop and embedded cores.
2. Route the shared import and refresh paths through strict URI readers, then
   serialize the fields through the existing model and outbound builders.
3. Update artifact pins from the attested release manifest and align packaging
   checks with the new core.
4. Exercise HTTPS subscription fetch and refresh with synthetic profiles,
   reject invalid options and HTTP downgrade, and check that emitted configs
   are accepted by the tagged binary.

## Risk and rollback

Feature detection and TLS pin conversion affect connectivity. Validate exact
tags and preserve certificate verification. The change is additive for saved
profiles. Rolling back the app restores the previous capability header so
vpnctl stops issuing these links to that client.

## Gates

| Gate | Status | Evidence planned |
| --- | --- | --- |
| Build | BLOCKED | Exact-head desktop CI and Android Release APK with the new AAR |
| Tests | BLOCKED | Capability, parser, refresh, generation and existing test suite |
| Documentation | BLOCKED | Record outcomes and relevant compatibility rules |
| Independent review | BLOCKED | Review the scoped implementation and archive changes |
| Remote verification | BLOCKED | Subscription selection and connection with synthetic profiles |
| Integration | BLOCKED | Prove the generated outbounds with the released core |

## Outcome

The upstream core release is published and its checksum manifest attested.
Client implementation and acceptance checks are pending.
