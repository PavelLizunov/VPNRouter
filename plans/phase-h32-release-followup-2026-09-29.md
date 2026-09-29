# H-32: release follow-up after v2.50.0-r10

## Why

The draft-time run of `verify-release-integrity.yml` cannot succeed: it reads the release through
`releases/tags/<tag>`, which returns 404 for drafts, so the "completed-draft integrity gate" of the release
procedure never passes and candidates get published without it (r9 and r10). The README examples and
`CURRENT_STATE.md` still name r9.

## What

- The workflow reads the release from the releases list (`releases?per_page=100`, exactly one match for the
  tag) in both inventory steps, so drafts are found (`contents: write` is already granted).
- README examples and `CURRENT_STATE.md` name v2.50.0-r10; the H-31 brief gets its outcome.

## Not covered

The fix only applies from the next candidate: a workflow run uses the file at its tag.

## Verification

`ReleaseIntegrityWorkflowTests` and the other release tooling tests (exact-head CI). The list-endpoint query
was run against the published `v2.50.0-r10` release and returned it (16 assets).

## Outcome

Pending CI.
