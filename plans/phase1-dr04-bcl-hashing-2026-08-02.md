# Phase 1 — DR-04 .NET 10 BCL hashing

**Owner**: Codex

**Branch**: `codex/dr-04-bcl-hashing`

**Audit ref**: dependency replacement task list DR-04, draft PR #99

**Effort**: 2–3 hours

**Risk**: MEDIUM — mechanical BCL migration across update/checksum paths; digest bytes and string casing must remain identical

**Blast radius**: App, Core, and checksum tests · about 15 files · no intended behavior change

**Rollback**: revert the implementation commit or close the branch

## Why

Several call sites allocate disposable SHA instances and manually lowercase hexadecimal output even though .NET 10 provides static one-shot hashing and `Convert.ToHexStringLower`. Replacing only equivalent patterns removes boilerplate and disposable state while keeping the existing algorithms, input bytes, cancellation behavior, and uppercase/lowercase contracts.

## What

- Replace `SHA256.Create()` plus `ComputeHash`/`ComputeHashAsync` with `SHA256.HashData`/`HashDataAsync`.
- Replace `SHA1.Create()` plus `ComputeHash` with `SHA1.HashData` for the existing non-security cache identifier.
- Replace `Convert.ToHexString(...).ToLowerInvariant()` with `Convert.ToHexStringLower(...)` where the contract is lowercase.
- Preserve uppercase output in `WgturnUpdater` and the uppercase prefix in `FreeConfigAggregator`.
- Modernize random-secret byte formatting without changing entropy or length.
- Do not alter expected-digest normalization, comparison rules, algorithms, or trust boundaries.

```diff
- using var sha = SHA256.Create();
- var hash = await sha.ComputeHashAsync(stream, ct);
- var text = Convert.ToHexString(hash).ToLowerInvariant();
+ var hash = await SHA256.HashDataAsync(stream, ct);
+ var text = Convert.ToHexStringLower(hash);
```

## How

1. Have Qwen 3.8 produce a read-only inventory of every SHA1/SHA256 and hash-output formatting call.
2. Classify each call by input type, sync/async behavior, cancellation, and output casing.
3. Apply only byte-for-byte equivalent .NET 10 BCL substitutions; do not introduce a helper or dependency.
4. Use existing checksum/update tests as the primary regression check and add a test only if Qwen finds an uncovered casing contract.
5. Run Release build and full tests through the available .NET 10 environment.
6. Perform a security-equivalence review focused on algorithms, cancellation, stream lifetime, and comparison semantics.
7. Fill Outcome, commit, push to `origin`, and update the draft PR.

### Expected areas

- App secret generation in `MainWindowViewModel.AutostartBootstrap.cs` and `MainWindowViewModel.cs`.
- Core update/checksum code in `SplitTunnelDriverManager`, `TgProxyUpdater`, `UpdateChecker`, `SideloadSource`, `WgturnUpdater`, and `SlipstreamManager`.
- Core identifiers in `FreeConfigAggregator` and `AppSettingsSane`.
- Existing checksum and characterization helpers in `VPNRouter.Tests`.

### Tests written

- None planned unless the inventory finds an untested output-casing contract.

### Verification approach

- Compare all existing digest test vectors before and after.
- Verify lowercase and uppercase output contracts separately.
- Verify async file hashing still receives the original cancellation token and does not dispose caller-owned streams.
- Build the solution and run the full test suite on .NET 10 CI.

## Verification gate

- [ ] **Gate 1 — Build clean**: solution Release build has 0 errors.
- [ ] **Gate 2 — Tests green**: full repository test suite passes; checksum tests remain green.
- [ ] **Gate 3 — Docs**: this brief's Outcome is filled; no README change is expected.
- [ ] **Gate 4 — Security/self-review**: security-equivalence review completed; use Qwen 3.8 fallback if the repository's `security-review` skill is unavailable.
- [ ] **Gate 5 — MCP verify**: N/A — no UI behavior change.
- [ ] **Gate 6 — Characterization diff**: public-surface characterization remains unchanged.

## Outcome

**Status**: PASS

**Commits**: `dde56968` (brief), `e72771c8` (main implementation), `13e1dc1a` (PoolAggregator follow-up)

**Pushed**: `origin/codex/dr-04-bcl-hashing` at `13e1dc1a`

**Test deltas**: +0 / -0

**Files changed**: implementation 17 files · +25 / -33 lines

**Gate results:**

- [x] Gate 1: PR CI restored and built the .NET 10 solution successfully. Local build was unavailable because the host exposes SDK 8.0.418 while `global.json` requires 10.0.301.
- [x] Gate 2: main test job successful — 2652 total, 2605 passed, 47 skipped, 0 failed; dedicated `test-update` job passed.
- [x] Gate 3: Outcome filled; README and zone instructions unchanged because there is no user-facing or architectural change.
- [x] Gate 4: Qwen 3.8 (`qwen3.8-max-preview`) completed pre-change inventory and post-change security-equivalence review with `SAFE TO COMMIT`; the repository has no callable `security-review` skill. `simplify` was not required for the 55-line mechanical diff.
- [-] Gate 5: N/A — no UI behavior change.
- [-] Gate 6: N/A — not a god-file split; public hash output and casing remain byte-for-byte identical.

**Surprises encountered**:

- `WgturnUpdater` and `FreeConfigAggregator` intentionally emit uppercase hashes; both contracts were preserved.
- A final repository-wide search found the same legacy SHA-1 pattern in `VPNRouter.Tools/PoolAggregator/Program.cs`, outside the initial App/Core/Tests inventory. The already-present parallel-worktree diff was independently reviewed by Qwen 3.8 as `SAFE TO INCLUDE`, then committed separately; its 8-byte uppercase identifier contract is unchanged.
- The updater-specific CI gate ran automatically because `UpdateChecker.cs` changed and passed.

**Follow-ups spawned**: none.

**Rollback**: `git revert 13e1dc1a e72771c8` or close the branch.
