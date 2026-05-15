# Pull Request

## Summary

<!-- 1-3 bullets: what + why -->

## Tests added

- [ ] Followed Test-First when contract was clear
      (per `plans/android-development-methodology.md` §1.1)
- [ ] Anti-fitted-to-fit checklist 6+/8 items pass (§6):
  - [ ] Test name reflects user-story / contract (not «test_for_pr_42»)
  - [ ] Setup builds state as user would (UI / API), not direct private mutation
  - [ ] Assert checks externally-observable behaviour
  - [ ] Test does NOT import internals just to peek at state
  - [ ] Test fails on fresh clone WITHOUT the fix applied
  - [ ] No duplicate with existing test
  - [ ] Test file has `[Category(...)]` / `[Trait(...)]` attribute (§3)
  - [ ] Logged in test inventory if Android (`plans/android-test-inventory.md`)

## Performance

- [ ] No baseline regression vs `VPNRouter.Tests/perf-baselines/` (§5)
- [ ] If intentional baseline change — old + new JSON + reviewer sign-off in commit

## Methodology compliance

- [ ] `bash tools/check-methodology.sh` 0 FAILs locally
- [ ] If Android-touching: relevant phase (`§7`) and MCP attribution (`§4`)
      noted in commit message

## Cross-references

<!-- Plan / issue / upstream commit references -->

## Test plan

<!-- Bullet list of how reviewer can verify -->
