# Phase 3 — R10 — SignPath signing action SHA pins (SUP-3)

**Owner**: Qwen Code session (code-only)
**Branch**: `codex/qwen-audit-r10-signing-action-pins-2026-07-29`
**Base**: `origin/main` (verified: no P1 branch touches `.github/workflows/sign-windows.yml`)
**Roadmap ref**: `plans/qwen-remaining-remediation-index-2026-07-29.md` (R10); prompt pool P08
**IDs**: SUP-3
**Effort**: ~30 min
**Risk**: LOW (dormant hygiene; workflow is manual-only and secret-gated)
**Blast radius**: `.github/workflows/sign-windows.yml` · ~+4 LOC (two `uses:` lines + comments) · runtime: none until the workflow is enrolled
**Rollback**: `git revert <commit>` / delete branch

---

## 1. Final P00 verdict / severity / confidence / corrected scope

| ID | Orig | Verdict | Final | Conf |
|---|---|---|---|---|
| SUP-3 | P2 | CONFIRMED | P3 | High |

Corrected scope: exactly two actions use mutable major-version tags while the rest
of CI SHA-pins. The workflow is `on: workflow_dispatch` only and hard-fails without
SignPath secrets (verified header "INERT until enrollment … MANUAL-ONLY"), so the
supply-chain exposure is currently DORMANT → P3 hygiene follow-up.

## 2. Verified current root cause (commit `b39a28c3`)

`.github/workflows/sign-windows.yml`:

- `:69` `uses: actions/upload-artifact@v4`
- `:75` `uses: signpath/github-action-submit-signing-request@v1`
- These are the ONLY two unpinned `uses:` across all 39 in `.github/workflows`
  (the rest are SHA-pinned).
- Inert guards (verified): trigger `on: workflow_dispatch` only (`:28-33`); the
  header documents "INERT until enrollment … MANUAL-ONLY" and a first step that
  fails fast (`exit 1`) until `SIGNPATH_API_TOKEN` exists; `permissions: contents:
  write` (`:35-36`).

## 3. Why

The rest of CI SHA-pins third-party actions; these two floating major-version tags
are the sole exception. Even though the workflow is dormant today, once it is
enrolled a mutable tag becomes a supply-chain pivot (a compromised `@v4`/`@v1`
ref could tamper with the signing flow). Pinning now removes the last unpinned
`uses:` and matches the established CI convention.

## 4. What

SHA-pin both actions to full commit SHAs (with a trailing `# <tag>` comment for
readability), preserving the exact `@v4` / `@v1` semantics at the pinned commit.
Keep the `workflow_dispatch` trigger and the secrets-present fail-closed guard
unchanged. Document the pin-update procedure in the commit message.

```diff
- uses: actions/upload-artifact@v4
+ uses: actions/upload-artifact@<full-sha> # v4
...
- uses: signpath/github-action-submit-signing-request@v1
+ uses: signpath/github-action-submit-signing-request@<full-sha> # v1
```

## 5. How (ordered minimal steps)

1. Resolve the full commit SHA currently backing `actions/upload-artifact@v4` and
   `signpath/github-action-submit-signing-request@v1` (from the upstream repos /
   GitHub API). Record the source of each SHA in the commit message (primary-source
   provenance).
2. Replace the two `uses:` lines with `<full-sha> # <tag>` form.
3. Verify NO other change to the workflow (trigger, guard, permissions, steps).
4. Add/confirm a grep-style check that no unpinned `uses:` remains in this file.

### Tests written

Static/grep-style (no local actionlint/parse execution; validated by inspection
and remote CI):

- Grep test: `sign-windows.yml` has zero `uses: …@vN` (mutable-tag) lines — every
  `uses:` is a 40-char SHA.
- Grep test: the `workflow_dispatch` trigger and the secrets-present guard are
  still present (unchanged).

### Verification approach

Static YAML inspection + grep. The workflow itself cannot run in CI without
SignPath secrets (by design), so verification is the pin shape + the unchanged
guard, confirmed by remote CI's YAML lint (if any) after push.

## 6. Affected callers / consumers + invariants

- Consumers: the manual SignPath signing flow (post-`build.ps1` ship step).
  Invariant: the workflow stays `workflow_dispatch`-only; the fail-fast
  secrets-present guard stays; `permissions: contents: write` stays; the
  artifact-upload → submit-signing-request sequence and `github-artifact-id`
  handoff stay identical.
- The pinned SHAs must correspond to the SAME `@v4`/`@v1` behavior (no version
  change, only immutability).

## 7. Exact expected file list

- `.github/workflows/sign-windows.yml` (two SHA pins)

## 8. Non-goals

- Do NOT enroll the workflow, add secrets, or change the trigger/permissions.
- Do NOT change the SignPath step inputs or the artifact naming.
- Do NOT pin actions in OTHER workflows (they are already SHA-pinned; this brief
  covers the only two exceptions).
- Do NOT run actionlint or any local YAML parser (code-only).
- Do NOT create a tag/release.

## 9. Security / concurrency / data-loss / platform review

- **Security**: this is a supply-chain hygiene fix (immutable action refs). The
  SHAs must be captured from the authoritative upstream sources and recorded in the
  commit message.
- **Concurrency / data-loss / platform**: none (workflow metadata only).

## 10. Dependencies / overlaps

- No P1 branch touches `sign-windows.yml` → base `origin/main`.
- R05 (PKG-1/SUP-2/SUP-4) touches `build-mac.sh`/`build-linux.yml` (different
  files) — independent, though both are prompt-pool P08 supply-chain items.
- Best executed when the signing workflow is about to be enrolled (per P00 the pin
  is "when the signing workflow is enrolled"); it is safe to land earlier since the
  workflow is inert.

## 11. Remote-only verification gates

- [ ] Gate 1 — Build clean (remote CI): YAML lint / workflow parse (if CI has one) passes; no job execution expected (secret-gated).
- [ ] Gate 2 — Tests green (remote CI): grep/static checks confirm zero unpinned `uses:` in the file.
- [ ] Gate 3 — Docs: brief Outcome filled; commit message records each SHA's capture source + update procedure.
- [ ] Gate 4 — Self-review: static YAML review; confirm trigger + guard unchanged.
- [ ] Gate 5 — MCP verify: N/A (workflow metadata).
- [ ] Gate 6 — Characterization diff: N/A.

## 12. Outcome (PENDING — filled after merge)

**Status**: PENDING
**Commits**: PENDING
**Pushed**: PENDING
**Test deltas**: PENDING
**Files changed**: PENDING

**Gate results:**
- [ ] Gate 1: PENDING
- [ ] Gate 2: PENDING
- [ ] Gate 3: PENDING
- [ ] Gate 4: PENDING
- [-] Gate 5: N/A — workflow metadata
- [-] Gate 6: N/A

**Surprises encountered**: PENDING
**Follow-ups spawned**: PENDING

## 13. Rollback

`git revert <commit>` on the R10 branch, or delete
`codex/qwen-audit-r10-signing-action-pins-2026-07-29`. The two `uses:` revert to
mutable tags; the workflow remains inert either way. No release state is touched.

## 14. Self-contained copyable Qwen prompt

```text
Выполни brief plans/phase3-audit-r10-signing-action-pins-2026-07-29.md через
Qwen Code. ID: SUP-3 (P3). Base branch: origin/main. Сначала прочитай brief
целиком, AGENTS.md, plans/CLAUDE.md и .github/workflows/CLAUDE.md. SHA-pin два
mutable actions (actions/upload-artifact@v4 и
signpath/github-action-submit-signing-request@v1) в
.github/workflows/sign-windows.yml на полные commit SHA, чтобы ни одного
unpinned uses: не осталось. Сохрани manual-only workflow_dispatch trigger и
"Guard - secrets present" fail-closed. Задокументируй update procedure для
пинов в commit message. НЕ запускай локальные build/actionlint/parse, не делай
live мутаций; YAML проверяй статическим осмотром. Только чтение/поиск/
редактирование. Commit/push/CI делает orchestrator. Без release/merge/tag/
deploy. Без emoji. Заполни Outcome шаблоном PENDING.
```
