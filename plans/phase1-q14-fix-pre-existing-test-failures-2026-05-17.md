# Phase 1 — Q14: Fix 5 pre-existing test failures (test debt from v2.32.3)

**Owner**: Claude integrator session (Wave 1 follow-up — sequential, NOT spawned to agent)
**Roadmap ref**: emergent from Wave 1 results, not in original roadmap
**Effort**: 10 minutes
**Risk**: LOW (touches test fixtures only — no product code change)

## Why
All 3 test-touching Wave 1 agents (Q3, Q6, Q7) independently reported the same 5 pre-existing test failures on main HEAD:

1. `VlessUriParserTests.TryParse_ValidUri_ReturnsEntry`
2. `VlessUriParserTests.Parse_RealityUri_ExtractsAllFields`
3. `VlessUriParserTests.Parse_RealityUri_ExtractsTransport`
4. `VlessUriParserTests.Parse_RealityUri_ExtractsRealityConfig`
5. `AppAutostartTgProxyTests.Bootstrap_IsInvokedFromConstructor`

**Root cause #1 (tests 1-4)**: my own v2.32.3 commit `d041ec8` added `PlaceholderConfigException` that rejects Reality `public_key = DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` at parse time. The test fixture `RealityUri` constant (line 1162-1166 of UnitTest1.cs) uses that exact placeholder pubkey. Parser throws → tests fail.

This is "test debt" from v2.32.3 ship — the PlaceholderGuardTests were added but the existing VlessUriParserTests were never updated to use a non-placeholder fixture. The CI test workflow doesn't exist yet (Q8 is what adds it), so the failures slipped through.

**Root cause #2 (test 5)**: `MainWindowViewModel` constructor body was refactored at some point and the literal string `BootstrapAutostartAsync` no longer appears in the source. The test does a source-text assertion which is brittle by design.

## What
Two edits, both in `VPNRouter.Tests/`:

### Edit 1 — `VPNRouter.Tests/UnitTest1.cs` line 1162-1166

Replace test fixture `RealityUri` constant:
- `pbk=DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU` → `pbk=vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4` (43-char base64url, NOT in KnownPlaceholderPubkeys)
- `sid=78ca7952` → `sid=deadbeef` (NOT in KnownPlaceholderShortIds)

And update the corresponding assertion in `Parse_RealityUri_ExtractsRealityConfig` (line 1188):
- `Assert.Equal("DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU", entry.Reality.PublicKey);` → `Assert.Equal("vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", entry.Reality.PublicKey);`
- `Assert.Equal("78ca7952", entry.Reality.ShortId);` → `Assert.Equal("deadbeef", entry.Reality.ShortId);`

This restores the original test intent (verify Reality fields parse correctly) without tripping the placeholder guard.

### Edit 2 — `VPNRouter.Tests/AppAutostartTgProxyTests.cs` line 123

The test currently does:
```csharp
Assert.Contains("BootstrapAutostartAsync", ctorSource);
```

The constructor was refactored — bootstrap is indirected. Read the current `MainWindowViewModel` constructor body, find what actually fires the bootstrap. Likely candidates:
- `AppBootstrap.Initialize(...)`
- `StartAutostartChain()`
- `_ = BootstrapAsync()`

Update the assertion to look for the current literal. If the bootstrap pattern is too implicit to assert source-text, refactor the test to assert behavior (e.g. spy on a fake autostart service) instead of source string. For Phase 1, the source-text fix is sufficient — defer behavior refactor to Phase 2D test seam work.

## Verification gate
- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: previously-failing 5 now pass; total goes from 832-834 → 839 (+5)
- [ ] **No regression**: PlaceholderGuardTests + PlaceholderInputGateTests still pass (they explicitly use the placeholder pubkey on purpose)
- [ ] **Hook gates**: pre-commit + commit-msg both green

## Outcome
*(filled after impl)*
