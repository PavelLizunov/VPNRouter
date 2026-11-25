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
