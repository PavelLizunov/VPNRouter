# Phase 4 — CI grep-gate for placeholder pubkey

**Owner**: Wave 17 single agent
**Roadmap ref**: Phase 3D PlaceholderDefense consolidation enabled this
**Effort**: 2 hours
**Risk**: LOW (CI workflow addition; cannot break production)

## Why

Phase 3D consolidated all 6 placeholder-defense layers under
`VPNRouter.Core/Services/PlaceholderDefense.cs`. The pubkey
`DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` (and short_id `78ca7952`,
server `195.135.255.216`) now appears in ONE production file. If a future
agent or developer accidentally hardcodes the fingerprint elsewhere,
the v2.32.3 Z:\kanareik incident class returns.

CI grep-gate fails any PR/push that introduces these fingerprints
outside the allowed locations.

## What

Add `.github/workflows/grep-placeholder-fingerprints.yml`:

```yaml
name: grep placeholder fingerprints

on:
  push:
    branches: [main, v3.0-prep]
  pull_request:
    branches: [main, v3.0-prep]

jobs:
  grep:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@<sha>
      - name: Verify single-source-of-truth for placeholder fingerprints
        run: |
          set -e
          # Allowed paths (production code single-source + test pins):
          #   VPNRouter.Core/Services/PlaceholderDefense.cs
          #   VPNRouter.Tests/*.cs (regression pins)
          #   plans/*.md (docs)
          #   .github/workflows/*.yml (this file)
          ALLOWED='VPNRouter\.Core/Services/PlaceholderDefense\.cs|VPNRouter\.Tests/.+\.cs|plans/.+\.md|\.github/workflows/.+\.yml'

          for FINGERPRINT in 'DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU' '78ca7952' '195.135.255.216'; do
            echo "=== Checking $FINGERPRINT ==="
            HITS=$(grep -rln -- "$FINGERPRINT" . --include='*.cs' --include='*.axaml' --include='*.yaml' --include='*.yml' --include='*.md' 2>/dev/null | grep -vE "^\./($ALLOWED)\$" || true)
            if [ -n "$HITS" ]; then
              echo "::error::Placeholder fingerprint '$FINGERPRINT' found in disallowed files:"
              echo "$HITS"
              exit 1
            fi
            echo "OK — only in allowed paths."
          done
```

## How

**Step 1**: Create the workflow file in `.github/workflows/`.

**Step 2**: Verify it runs locally with our current main HEAD — all 3 fingerprints should be confined to PlaceholderDefense.cs + tests + plans.

**Step 3**: Add a deliberate test commit on a throwaway branch that adds the pubkey to a random Core file — the workflow MUST fail. Then revert.

**Step 4**: Update `plans/v3.0-execution-methodology.md` to mention this CI gate so future agents know.

**Step 5**: Update `plans/phase3-3D-placeholder-defense-consolidation-2026-05-18.md` Follow-up section to mark this item DONE.

## Verification gate
- [ ] Workflow file created
- [ ] Local grep verifies the workflow's logic (current HEAD passes)
- [ ] Test commit confirms it FAILS on disallowed fingerprint
- [ ] Methodology doc updated
- [ ] **Hook gates** pass (workflow YAML lint passes)

## Outcome
*(filled by agent)*

## Follow-up

- Phase 5 may extend the gate to other fingerprint types (PII, secrets,
  test API keys) once we audit what else deserves single-source-of-truth
  enforcement.
