# Corp company analyzer enrollment

## Why and what

Company-scoped receiver uploads/API are implemented, but the analyzer only
selects the configured legacy identity. Automatically process completed enabled
company registrations while retaining receipt ownership and legacy behavior.

## Frozen interface and invariants

- Companies.active_identities()->list[str] returns sorted registered identities
  with pending=false and enabled upload keys. No credentials or registry writes.
- company_analysis.run_once(root, config, run_one)->dict calls the existing
  bounded identity-specific analyzer, processing at most one report per call.
- Config may contain company_prompt_file. load_config reads it with the existing
  private-file constraints and retains _company_prompt and _company_prompt_sha.
  Missing optional field keeps the exact legacy-only processing behavior.
- With a generic company prompt, select registered active identities plus the
  configured legacy identity when its key is enabled. New companies use this
  generic prompt and empty _facts; omit operator_context_file and select the
  generic prompt_file. Legacy keeps its existing prompt, facts and job revision.
- Use a fixed nonblocking process lock for enrollment orchestration. Retain the
  existing analyzer process/receipt locks. A fixed bounded durable cursor rotates
  the start after every processed result; constant legacy backlog cannot starve
  another registered company. Unknown, pending and disabled identities never run.
- Respect existing retry/lease/generation/disable/delete checks inside run_one.
  Do not add tools, network destinations, repair actions or model fallback.
  Return only the existing bounded state/processed summary, no identities/secrets.
- Registry/key failures fail closed. Registry is reread per call so new companies
  enroll without daemon restarts or per-company manual services.

## Verification and six gates

1. Independent spec tests: real synthetic ZIP/receipt fixtures, two registrations,
   captured provider packets, legacy retention, disabled/pending/foreign keys.
2. Fairness: continuous legacy work still reaches the new company on next call;
   busy orchestration stays nonblocking and model requests do not overlap.
3. Existing receiver/analyzer/categories regression slices pass without network.
4. Independent diff review; record findings before corrections.
5. AST syntax, scoped diff and secret scan clean; source commit/push recorded.
6. Production cutover verifies original credentials/reports/context/model intact
   and uses the new generic prompt only for new company identities.

## Risk and rollback

The primary risk is reusing legacy operator facts or prompt for new companies.
Use a separate generic prompt file and no inherited facts. Preserve original
worker config/keys/reports in a verified private archive before activation.
Interpreted-service rollback restores that complete prior config/source only
before new-company use; do not revoke or regenerate established credentials.

## Scope of checks

No Windows/client or Rust change/build belongs to this correction. Backend
70bc69f3 retains all13green exact-head checks. Publish the interpreted receiver
branch without opening a client-build-triggering PR under the owner's current
no-client-build constraint; retain the scoped Python verification/review evidence.

## Outcome

Implemented registry enrollment, generic prompt isolation and durable round-robin
scheduling. Existing identity-specific analyzer AST is unchanged. All84 scoped
Python tests passed, including five independent enrollment/provider isolation
scenarios. Independent diff review returned no findings. AST/diff/secret gates
are checked before publication. Live service cutover remains pending; backend
70bc69f3 and its verified hosted artifact remain unchanged.
