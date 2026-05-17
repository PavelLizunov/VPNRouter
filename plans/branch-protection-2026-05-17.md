# Branch protection on `main` — applied 2026-05-17

Per `plans/v3.0-execution-methodology.md` §13. Applied via `gh api PUT
repos/PavelLizunov/VPNRouter/branches/main/protection` (token has `repo`
scope, account `PavelLizunov`, admin on owned repo). State before request
was `404 Branch not protected`.

## What was set

```json
{
  "required_status_checks": {
    "strict": true,
    "checks": [
      {"context": "verify"}
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "required_linear_history": false,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "block_creations": false,
  "required_conversation_resolution": false,
  "lock_branch": false,
  "allow_fork_syncing": false
}
```

### Why the status check is named `verify`, not `Verify Release Integrity`

GitHub's branch-protection API expects the **check-run name** (which is
the job's `name:` field, or the `job_id` when no `name:` is set). The
workflow file `.github/workflows/verify-release-integrity.yml` declares
one job:

```yaml
jobs:
  verify:        # ← this is the job_id; no `name:` override
    runs-on: ubuntu-latest
```

The workflow's top-level `name: Verify Release Integrity` is only used
for the Actions UI grouping, not as a check-context name. Verified
against recent runs via:

```bash
gh api repos/PavelLizunov/VPNRouter/commits/d041ec8/check-runs \
  --jq '.check_runs[].name'
# → "verify", "publish", "test-update", "build", "aggregate"
```

So the protection's `checks[].context = "verify"` is correct. (Trying
`"Verify Release Integrity"` would set protection but no actual run
would ever satisfy it — effectively locking the branch.)

## Verification output

`gh api repos/PavelLizunov/VPNRouter/branches/main/protection --jq '{...}'`:

```json
{
  "allow_deletions": false,
  "allow_force_pushes": false,
  "enforce_admins": false,
  "required_status_checks": "verify",
  "strict": true
}
```

`git push --dry-run github HEAD:main` → `Everything up-to-date` (no
local changes vs remote, so dry-run can't exercise the rule — but the
API state is active).

## Bypass for hotfix

Since `enforce_admins: false`, the repo owner (`PavelLizunov`) can:

1. **Direct push** continues to work for admin: the protection rule
   blocks non-admin contributors but admin bypass is implicit.
2. **Tighten temporarily** if the team grows:
   ```bash
   gh api -X POST repos/PavelLizunov/VPNRouter/branches/main/protection/enforce_admins
   # ... do strict work ...
   gh api -X DELETE repos/PavelLizunov/VPNRouter/branches/main/protection/enforce_admins
   ```
3. **Emergency removal** of the whole rule:
   ```bash
   gh api -X DELETE repos/PavelLizunov/VPNRouter/branches/main/protection
   ```

The `verify` check is `strict: true`, meaning the branch must be **up to
date with main** before a non-admin merge. Admin pushes bypass this.

## Adding `dotnet-test` workflow once Phase 1 creates it

Phase 1 of the v3.0 plan adds an xUnit-on-PR workflow. Once that workflow's
first run lands on `main` and the check-run name is confirmed (likely
`test` or `dotnet-test` — same logic as `verify` above), append it to the
checks array:

```bash
# Read current state, append context, PATCH.
gh api -X PATCH repos/PavelLizunov/VPNRouter/branches/main/protection/required_status_checks \
  --field 'contexts[]=verify' \
  --field 'contexts[]=dotnet-test' \
  --field 'strict=true'
```

Or fully replace via PUT:

```bash
cat > /tmp/bp2.json <<'EOF'
{
  "required_status_checks": {
    "strict": true,
    "checks": [
      {"context": "verify"},
      {"context": "dotnet-test"}
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "required_linear_history": false,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "block_creations": false,
  "required_conversation_resolution": false,
  "lock_branch": false,
  "allow_fork_syncing": false
}
EOF
gh api -X PUT repos/PavelLizunov/VPNRouter/branches/main/protection --input /tmp/bp2.json
```

The PATCH form is safer (preserves other fields if GitHub adds new ones
to the API), but the PUT form is what we used initially and is fully
explicit.

## Caveats / known interactions

- `verify-release-integrity.yml` triggers on `release: published/edited`
  and `workflow_dispatch` — **not** on `push` to `main`. That means
  direct pushes to `main` won't have a `verify` check-run, so the
  `strict: true` requirement is essentially a no-op for the
  direct-commit model we use today. It only matters if/when the
  workflow is extended to trigger on `pull_request` or `push: main`,
  or if a PR-based model is adopted later.
- Until then, the protection mainly enforces: no force push, no
  deletion, no fork-syncing — which is the safety floor we want.
- `enforce_admins: false` means I (admin) can still push directly
  without satisfying any check — preserving the autonomous ship cycle
  for `-rN` candidates documented in `CLAUDE.local.md`.
