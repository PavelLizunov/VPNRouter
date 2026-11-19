# Phase - DNSTT subscription support

- Branch: `dsh/dnstt-subscription-20261119`
- Base: `f22ccc1077a14753b514d8eff7308d7937ddc790`
- Risk: medium due to shared subscription and outbound generation paths.
- Rollback: revert the capability and parsing changes. Existing links and client selection stay available.

## Why

Deliver a managed Senko DNSTT and SSH route through a user's existing VPNRouter subscription URL.

## What

Map a versioned `dnstt://` share link to one selectable TCP-only entry. The renderer emits a native DNSTT outbound to a pinned DNS tunnel and an SSH outbound using it as its detour. Include it only when the installed or bundled sing-box supports this protocol. Preserve existing links, direct configurations and selection behavior.

## How

Add a narrowly validated parser and typed profile at the existing subscription and selection entry points. Use `X-VPNRouter-Capabilities: dnstt-v1` for capable clients, alongside existing capability tokens. Preserve bootstrap DNS routing and deny accidental UDP fallback. Validate exact config on the release kernel.

## Verification

1. Build: Release solution and applicable Android build on an authorized worker or CI.
2. Tests: valid and malformed links, supported and unsupported core, exact linked outbounds and UDP behavior, unchanged legacy profiles.
3. Docs: track observed results and the release boundary in the outcome.
4. Review: independent review of parsing, handling of credential fields and routing behavior.
5. UI: verify one selectable route on the supported app. Use WINBRAT for desktop live verification after an authorized ship.
6. Integration: the same subscription URL on old and new clients, plus HTTPS egress via the managed server.

## Outcome

Pending implementation and verification.
