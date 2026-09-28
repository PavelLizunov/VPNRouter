# Omarchy continuation handoff

## Request for the separate session

Continue the unfinished Omarchy work with the owner. Do not confuse it with the
repository cleanup task. The owner explicitly excluded Omarchy from cleanup:
keep PR #296, its branch and original checkout; do not close, delete or rewrite
them as housekeeping. No new user-visible session was created by the cleanup agent.

## Starting point

- Repository: PavelLizunov/VPNRouter, canonical remote origin.
- Checkout: `/var/lib/dsh/Project/VPNRouter`.
- Branch: `dsh/omarchy-plugin-2026-09-17`.
- Recorded head: `04bdff27524906c757607046090060bec0092a16`; recheck live state.
- Draft: https://github.com/PavelLizunov/VPNRouter/pull/296
- Separate plugin repository: PavelLizunov/omarchy-vpnrouter.
- Start by reading global/project instructions, canonical contract and current
  Git status. The old branch's instructions differ from the newly reviewed main.
- Existing evidence: `plans/omarchy-integrated-candidate-2026-09-18.md` and
  `plans/omarchy-protocol-v1.md`. Treat them as historical receipts, not proof of
  current runtime acceptance. Inspect the branch-local architecture map as a
  snapshot, not as main's delivered architecture.

## Known unfinished acceptance areas

PR body identifies privileged Linux firewall/DNS parity, compatible authenticated
sing-box distribution/runtime path, real-host popup/input/scaling/lifecycle and
dataplane checks, plugin implementation delivery and complete product acceptance.
Same-UID flock is cooperative, not host-global; eligibility does not establish
binary integrity or TUN privileges. Reassess current code before making claims.
Do not merge, release, provision root access, restart shells or alter live VPN
state without the required owner approval and applicable verification procedure.

## Preserved local work

The cleanup agent did not alter the original checkout or its untracked entries:

- `.dsh/codebase-audit-scorecard-2026-09-16.md`
- `.dsh/performance-autoresearch/`
- `.dsh/security-review-report.md`
- `proxy_sources_2026-09-13.json`
- `vpn_issue_report.md`

Inspect before moving or committing anything; do not publish raw private logs,
credentials or configuration. Git recovery backup covers committed refs only.
