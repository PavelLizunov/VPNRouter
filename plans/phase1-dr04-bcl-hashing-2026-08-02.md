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

To be filled after implementation and verification.
