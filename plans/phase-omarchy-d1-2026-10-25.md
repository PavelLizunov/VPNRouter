# D1: Linux backend defect and contradiction review

## Intent and scope

Review Linux additions against origin/main at ddf97f80, including Headless,
Core runtime/ownership seams, Arch packaging, CI, and the plugin wire consumer.
The accepted base origin/dsh/omarchy-mvp-2026-10-22 is already an ancestor.
Keep PR 622 stacked and preserve all platform behavior outside confirmed fixes.
No live VPN, package installation, release, merge, or workstation .NET execution.

## Confirmed defect and correction

The protocol server abandons handler disposal after three seconds, while a
session stop allows fifteen seconds and Core allows five seconds each for
SIGTERM and SIGKILL. Program's repeated disposal returns immediately after the
session sets its disposed flag. EOF/SIGTERM can therefore exit before stopping
an owned child. Give production handler teardown time for session gate wait
and bounded stop. Keep a short injected deadline for uncooperative-handler tests.
Add a fake-handler regression that remains in teardown beyond the old deadline.
Risk: shutdown waits longer for an uncooperative handler. Rollback: revert the
fix commit. No protocol schema change or network operation is required.

## Verification gates

1. Build: pending exact-SHA test.yml headless publish and existing build jobs.
2. Tests: pending existing Headless regression suite and Core suite in CI.
3. Documentation: record defect ledger and final observed outcomes here.
4. Review: direct source review in this round; independent lead review follows.
5. UI/remote: N/A, no UI or live VPN change; Arch offline CI package check pending.
6. Integration: verify the real server disposal boundary and consumer budgets;
   no local SDK execution. Read-only triage of PRs 617-620, 624 and 625.

## Outcome

Initial SHA ddf97f806b192a879ef5e0df940c2d6f949103a4 passed test.yml run
40030549271 and Arch run 40074333153. New checks are pending.

Preparation commit 6b16848b passed all six test.yml jobs in run 40170672727.
The shutdown correction changes the handler deadline to 20 s, retains an
injectable short hang deadline, and adds an EOF test whose handler completes
only after the old three-second deadline. Scoped diff review and `git diff
--check` passed; .NET execution awaits exact-head CI. The owning Headless
README now states the bounded shutdown behavior rather than promising cleanup
of an unresponsive engine. No full solution build or live engine stop was run.
