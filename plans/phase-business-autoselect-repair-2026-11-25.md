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

The shared Business support channel returns bounded advisory text. Opening
recovery sends nothing. The employee can request analysis of a fresh report
and independently approve a verified connection attempt. A connected client
can request analysis without interrupting its tunnel.

The isolated loopback fixture reached the same TCP port with two synthetic
VLESS identities. Only the valid identity reached the HTTP trace responder
through the proxy. The fixture also checked that the direct listener received
no request. The same scenario runs in the Business Linux CI job.
