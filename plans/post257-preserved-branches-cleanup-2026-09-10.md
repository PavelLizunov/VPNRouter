# Branch cleanup after accepted PR257

Owner approved PR257 exact-head merge and subsequent removal of branches whose useful tests are preserved. Merge `2689ee77a06cbb0274e3dc3adf141570c4377da8` contains tested `e20869abae20c15a1750c38e00b4f346fc6e2aeb`; ancestry verified after fetching origin/main.

Targets:
- `jules-4439400610055714346-7c325a2f`: `dc5cbd10ef4986c7231b3b349fa5393e8f9c609c` (PR172).
- `jules-7563407372259111856-695ecb2c`: `484e846a5c359c5c6bbcb9fe279e3ef751ac54a0` (PR182).
- `sentinel-redact-prefixed-secret-keys-11930939174604721738`: `ff75497e2e3e330230b3d79f951c2eb91c494e2f`.

Full RuleSet branch scopes rechecked: only known redaction/test changes, plus obsolete SDK downgrade in PR182. Prefixed-key full scope was verified in prefixed-key-test-preservation-2026-09-10.md. Keep accepted explicit key allowlist, not rejected generic bare-key matching. Useful assertions are now retained by existing fixtures; original duplicate fixtures need not be restored. See redaction-test-preservation-verification-2026-09-10.md for exact-SHA 2/2 worker and CI evidence. Full tips identify original history but are not standalone Git object backups.

Status: fresh remote tips, no-worktree binding and preservation-commit ancestry checked; all three remote refs deleted atomically with per-ref exact-SHA leases; fresh remote absence verified. No product changes or broader verification claimed.
