# Phase 2 - MainWindowViewModel connection split

**Owner**: Codex session 2026-08-13
**Branch**: `codex/quality-trust-hardening-249`
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` section 2B
**Effort**: 3-5 hours
**Risk**: MEDIUM
**Blast radius**: mechanical relocation inside one partial type; zero public surface or runtime behavior change
**Rollback**: revert the split commit

## Why

The main `MainWindowViewModel.cs` file is 7744 lines even after prior partial
extractions. Connection transition logic is a cohesive, frequently edited
block and is the highest-value context reduction for agents. A partial-file
move preserves private access and avoids introducing services, interfaces or
new ownership semantics.

## What

- Add `MainWindowViewModel.Connection.cs`.
- Move the connection-status, connect/disconnect transition and related
  connection presentation helpers from the main partial without editing their
  bodies.
- Keep fields, constructor, disposal and unrelated feature logic anchored in
  the main file.
- Do not move custom-rule, Telegram, Free Config, update or platform setup code.

```diff
- connection methods embedded in the 7744-line main partial
+ the same byte-equivalent method bodies in a named Connection partial
```

## How

1. Run the current `MainWindowViewModelCharacterizationTests` and record the
   pre-split pinned surface/hash result.
2. Identify complete member boundaries and callers before moving anything.
3. Apply a move-only patch; no renames, formatting or logic changes in the same commit.
4. Run source comparison, characterization, headless GUI and full tests.
5. Use remote WINBRAT only if review finds a UI-visible behavioral change;
   otherwise Gate 5 is N/A and Gate 6 is the blocking proof.

### Tests written

- Existing `MainWindowViewModelCharacterizationTests` is the required snapshot.
- A source ownership test may be added only if needed to keep the connection
  block from drifting back into the main file.

### Verification approach

Pre/post characterization must be identical. Build output, focused headless
tests and the full suite must stay green. Diff review must show relocation only.

## Verification gate

- [ ] **Gate 1 - Build clean**: Release solution build, 0 errors.
- [ ] **Gate 2 - Tests green**: characterization, headless GUI and full suite.
- [ ] **Gate 3 - Docs**: App zone architecture and Outcome updated.
- [ ] **Gate 4 - Self-review**: ponytail/simplify confirms a move-only partial, no abstraction.
- [ ] **Gate 5 - Remote brat UI verify**: N/A if the diff remains move-only; BLOCKED if behavior changes and WINBRAT is unavailable.
- [ ] **Gate 6 - Characterization diff**: exact pre/post snapshot match.

## Outcome

Pending implementation and verification.
