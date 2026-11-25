# Business server selection and connection recovery

The Business picker currently measures reachability without proving that a VPN
profile can carry traffic. A Windows support report showed repeated tunnel
warm-up failures. Employees need a working automatic choice at startup and an
accessible recovery action below Connect.

Use a bounded, cancellable end-to-end check through each eligible configured
profile. Select the fastest successful profile in automatic mode, preserve an
explicit manual choice and report when none worked. Reuse the existing engine
and normal Business connection flow. Keep subscription credentials local.

Use the existing Business support and DeepSeek analysis channel for the
recovery action. Present the proposed action and use a bounded, local recovery
operation with explicit confirmation. Keep corporate routing and policy under
the configured operator's control.

Verify with focused regression tests, the Business build, the existing suite,
headless UI checks, independent review and installed scenario when authorized
for shipping. Revert the feature commits to roll back. Production policy,
published packages and credentials are outside this change.

## Implementation and verification

- Implement profile selection in the Business client and pin the same verified
  outbound for the normal connection. Exercise both automatic and manual mode.
- In parallel, wire the home recovery action to the existing company report
  receiver. Show its reply, request separate confirmation for a local connection
  attempt, and preserve the active session while gathering diagnostics.
- Verify the transport independently with a reachable but unusable endpoint and
  a working proxied request. Review concurrent starts, cancellation, profile
  changes, and policy capture against the integrated diff while PR CI runs.

On first use and after upgrading a legacy installation, Connect automatically
uses the fastest verified Business choice. The server menu retains an explicit
manual override. An employee can press Fix connection once to retry locally.
If that fails, a second, labelled action sends a fresh diagnostic report to
company support for DeepSeek analysis. An already connected client can use
that action while its current tunnel stays active.

The isolated loopback fixture reached the same TCP port with two synthetic
VLESS identities. Only the valid identity reached the HTTP trace responder
through the proxy. The fixture also checked that the direct listener received
no request. The same scenario runs in the Business Linux CI job.

## Subscription refresh before connection

Connect can currently cancel a scheduled subscription fetch at the start of a
connection, then use the previous pool. Share the Business window's current
HTTPS refresh with Connect and Repair. If there is no current refresh and the
last successful one is older than the hourly interval or absent, try one fetch
before selecting a profile. Keep the accepted settings and profile pool on
fetch, validation, or persistence failure. Preserve the manual DNSTT settings
guard while allowing an automatic pool to rotate its previous DNSTT entry.

Exercise delayed success, cache fallback, and manual preference with synthetic
Business fixtures. Verify exact-head build and focused regression results in
PR CI. Revert this follow-up to restore the previous refresh scheduling.

## Business Windows runtime

The current Windows Business client contains sing-box 1.14.4-vpnctl.2. The
operator assigns some nodes only to clients using exactly 1.14.4-vpnctl.3.
Use that release in Business Windows packages and report the actual selected
core version and Windows platform on the authenticated subscription request.
Verify the official archive, the core in a credential-free Business package
and selection of the correct feed in the separate vpnctl change. Use 2.51.1
for the Business candidate so the updater can distinguish it from 2.51.0.

The Business GET and preconnect verifier use startup's deployment selection.
Windows Business compares the bytes of equal-length executables before
choosing the deployed copy. Mock HTTP and process tests cover a same-size
.2 to .3 update and an identical deployed .3, plus fresh, different-size,
upstream, malformed and missing cores. On Windows the fixture also checks
that the selected executable has its companion library.

Static checks confirmed the Business build filter includes the new tests.
The subscription tests still require an exact-head CI run.
