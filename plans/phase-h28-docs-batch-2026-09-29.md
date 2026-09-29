# H-28: record the owner decisions and finish the brief outcomes

## Why

After the H-27 close-out: the Android compile-check workflow (H-25) was merged by owner decision, the
owner decided not to rewrite the history for the old archived keys, and NIGHT-09 to NIGHT-12 were
re-verified against the current source. Eight briefs still lacked their PR number.

## What

- Ledger: close NIGHT-09, NIGHT-10, NIGHT-11, NIGHT-12 (verified fixed, with the evidence) and
  ARCHIVED-EVIDENCE-CREDENTIALS (keys rotated, no history rewrite).
- Briefs H-1, H-2a, H-2b, H-3a, H-3b, H-9, H-12, H-25 and H-27 get their merged PR number.
- `.github/workflows/AGENTS.md` lists `android-compile.yml`.

## Verification

Only documentation changes; exact-head CI.

## Outcome

Pending CI.
