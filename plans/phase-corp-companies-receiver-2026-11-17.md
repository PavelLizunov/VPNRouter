# Company-scoped support receiver

Owner resumes separate Corp username/password companies. This task adds trusted
policy-id to upload-identity bindings while preserving existing credentials,
reports, categories and diagnostics model. Root owns service operations. Child
implementation owns Python source only; no worker/production/Windows operations.
The frozen wire/API contract is the vpnctl corp-companies-implementation spec.
Independent tests/review, focused Python tests, exact-source deploy and real
cross-company browser acceptance are required. No client rebuild is in scope.

## Verification and review outcome

Implemented trusted per-policy registry/provisioning, opaque existing policy
IDs, selected-company report/category ownership and strict cutover header mode.
Independent HTTP contract tests plus registry tests pass12/12; category regression
passes15/15 and original receiver regression passes26/26. No compiled build ran.
Independent diff review found disabled root-capability handling and a pending
key-write revocation window; both were recorded before fixes. Root enabled state
is checked before provisioning/dispatch. Uncertain missing-key recovery retains
the full token and fails closed rather than recreating removed authority.
The existing analyzer/model/client bundle is unchanged. Production activation
awaits the matching reviewed backend/edge and a complete private backup.

Source is published as a task branch without a client-build-triggering PR;
necessary server builds belong to the matching vpnctl change. This is a Python
service change and does not authorize Windows rebuilds.
