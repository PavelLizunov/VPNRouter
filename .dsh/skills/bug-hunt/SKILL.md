---
name: bug-hunt
description: Adversarial multi-agent review of a diff or subsystem. Fans out independent reviewers briefed with VPNRouter invariants, verifies each claim against code, and appends survivors to plans/OPEN-DEFECTS.md.
whenToUse: Before cutting stable, after landing a non-trivial feature/refactor, or when asked to "bug-hunt" or "adversarially review" work. NOT for <=5-line hotfixes.
---

# Adversarial bug-hunt

Adversarial multi-agent review over a diff or subsystem. Fans out independent reviewers briefed with VPNRouter invariants, verifies each claim against code, and appends survivors to `plans/OPEN-DEFECTS.md` (the cut-stable gate ledger).

## Inputs

- Scope: a diff (`git diff <base>..HEAD`) OR a named subsystem (e.g., "the failover path", "the update extractor", "the Linux kill-switch").
- Invariants: `docs/REVIEW_AGENT_PROMPT.md` (paste verbatim into each subagent prompt).

## Procedure

1. **Fan out independent reviewers** (3–5 for a feature; more for a thorough audit).
   - Launch parallel `subagent` / `subagent_fork` calls. Use `workflow` only when the user explicitly requests a workflow or large scripted orchestration.
   - Give each subagent a distinct angle (e.g., correctness, concurrency/lifetime, security/secrets, fail-closed/leak, test-coverage).
   - Each prompt receives the full diff/subsystem and `docs/REVIEW_AGENT_PROMPT.md` block independently.

2. **SOL verification (Source-verified check):**
   - The SOL (lead agent) re-opens every claimed `file:line` using `read` or `grep` to verify the defect against current code before recording it.
   - Drop any claim that is unverified or hallucinated.

3. **Triage survivors** by severity:
   - **P0**: broken/leaky build can reach users.
   - **P1**: real, bounded defect.
   - **P2**: hygiene / code cleanup.

4. **Land findings:**
   - Fix in-scope P0/P1 findings immediately for the current change.
   - Append any real-but-deferred findings to `plans/OPEN-DEFECTS.md` under `## Open` as `- [ ] **P0/P1** — <symptom> — <file:line> — <target>`.
   - The cut-stable gate (`tools/check-open-p0.ps1`) blocks stable releases while open P0/P1 entries exist.

## Output schema (per finding)

`severity` (P0|P1|P2) · `title` · `problem` · `evidence` (file:line) · `fix` · `cost` (S|M|L) · `risk`

Synthesize duplicates raised by multiple reviewers into a single entry noting all reporting reviewers.

## Do NOT

- Record a finding without re-confirming it against current code.
- Keep survivors only in session context — they must reach `plans/OPEN-DEFECTS.md`.
- Re-flag already-instrumented gates as missing — inspect code beyond them.

## References

- `docs/REVIEW_AGENT_PROMPT.md` — per-agent brief and VPNRouter invariants.
- `plans/OPEN-DEFECTS.md` — defect ledger.
- `tools/check-open-p0.ps1` — release gate verification script.
