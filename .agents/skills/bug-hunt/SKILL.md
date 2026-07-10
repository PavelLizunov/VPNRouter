---
name: bug-hunt
description: Adversarial multi-agent bug-hunt over a diff or subsystem. Fans out independent reviewers briefed with VPNRouter invariants, verifies each claim against code, and appends survivors to plans/OPEN-DEFECTS.md (the cut-stable gate ledger). Run before a stable cut and after any non-trivial feature.
when: Before cutting stable, after landing a non-trivial feature/refactor, or when the user asks to "bug-hunt" / "adversarially review" the current work. NOT for ≤5-line hotfixes (the ship review-diff covers those).
---

# Adversarial bug-hunt

The methodology's review layer (METHODOLOGY §5/§7.1), captured as a repeatable
skill so it stops being improvised every session and stops being a *post-ship*
post-mortem. It feeds the open-defect ledger the cut-stable gate reads, closing
the loop the auto-failover P0 fell through (it was found, deferred, shipped).

## Inputs
- Scope: a diff (`git diff <base>..HEAD`) OR a named subsystem (e.g. "the
  failover path", "the update extractor", "the Linux kill-switch").
- The invariants in `docs/REVIEW_AGENT_PROMPT.md` (paste verbatim into each agent).

## Procedure

1. **Fan out independent reviewers** (3-5 for a feature; more for a "thorough
   audit"). Each is INDEPENDENT — paste the full diff/subsystem + the
   `docs/REVIEW_AGENT_PROMPT.md` block; never "as discussed". Use the `Workflow`
   tool (one agent per lens/area, then synthesize) when the user has opted into
   multi-agent orchestration, else spawn parallel `Agent(subagent_type:
   "general-purpose", ...)` calls. Give each a distinct angle so they don't all
   find the same thing: e.g. correctness, concurrency/lifetime, security/secrets,
   fail-closed/leak, test-coverage.

2. **Verify every claim against the code before recording it.** A finding that
   isn't grounded in a real `file:line` (Read/Grep it) is dropped. This is the
   difference between a bug-hunt and a hallucination — the synthesizer re-opens
   each claimed line and confirms the defect is real.

3. **Triage** survivors into severity (P0 = a broken/leaky build can reach
   users; P1 = real, bounded; P2 = hygiene), each with the concrete fix +
   blast-radius.

4. **Land them:**
   - Fix now what is in scope for the current change/cut.
   - Anything real-but-deferred MUST be appended to `plans/OPEN-DEFECTS.md`
     **## Open** as `- [ ] **P0/P1** — <symptom> — <file:line> — <target>`.
     The cut-stable gate (`tools/check-open-p0.ps1`) then physically blocks a
     stable cut while it's open (unless explicitly `-Waive`d).
   - Security-relevant survivors (process exec / firewall / sudo / local bind /
     untrusted parse) get a second security-focused pass (METHODOLOGY §7.1).

## Output schema (per finding)
`severity` (P0|P1|P2) · `title` · `problem` · `evidence` (file:line) · `fix` ·
`cost` (S|M|L) · `risk`. Synthesize duplicates raised by multiple reviewers into
one entry, noting which reviewers.

## Do NOT
- Record a finding you haven't re-confirmed against the code.
- Let survivors live only in a session-local plan file — they must reach
  `plans/OPEN-DEFECTS.md` or the cut gate can't see them.
- Re-flag already-instrumented gates as "missing" (pre-commit, pre-push,
  cut-stable, review-diff) — find what's BEYOND them.

## References
- `docs/REVIEW_AGENT_PROMPT.md` — the per-agent brief + VPNRouter invariants.
- `plans/OPEN-DEFECTS.md` — the ledger; `tools/check-open-p0.ps1` — the gate.
- `plans/methodology-audit-2026-06-25.md` — the 7-lens audit that established this.
