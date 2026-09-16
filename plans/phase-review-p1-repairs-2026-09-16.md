# Phase — 2.50 review P1 repairs

**Owner**: DSH session sequential-review follow-up
**Branch**: `dsh/review-p1-repairs-2026-09-16`
**Roadmap ref**: `plans/OPEN-DEFECTS.md` Mass review 2026-09-16 P1 block; approved SDD 2026-09-16
**Effort**: 1 session
**Risk**: MEDIUM (connect UI, public-config parse, subscription logs, update extract)
**Blast radius**: FreeConfigs apply VM, Android free-config apply/verify, aggregator fallback parse, PolicyHttpClient timeout text, UpdateChecker SHA gate, focused tests
**Rollback**: `git revert` / branch delete

## Why

The 2026-09-16 sequential review confirmed six P1s on `main` `b101a7e2`: Free Configs can paint Connected without typed readiness; Android apply/deep-verify and aggregator fallback still parse with `VlessUriParser` after the 2.50 multi-protocol overhaul; subscription HTTP timeouts embed the raw URI in `TimeoutException`; GitHub auto-update can extract with no SHA256.

## What

Approved SDD only. No NIGHT-FOLLOWUP, no P2 overflow/test-restore, no version bump, no WINBRAT.

```diff
- try { await startTask; } catch { }
- IsConnected = true;
+ if (outcome != TwoPhaseStartOutcome.Connected) { /* stop / false */ }
+ IsConnected = true; // Connected only

- VlessUriParser.Parse(raw)
+ ServerUriParser.Parse(raw)

- throw new TimeoutException($"HTTP request to {request.Uri} timed out...")
+ throw new TimeoutException($"HTTP request timed out after {ms} ms.")

- expectedSha = useLite ? null : info.FullChecksumSha256;
- // absent -> extract
+ // missing digest after sidecar fetch -> throw, do not extract
```

## How

1. Gate `ApplyFreeConfigAsync` like `ToggleConnectionAsync`.
2. Replace leftover `VlessUriParser.Parse` in Android apply, Android deep-verify (both sites), aggregator fallback.
3. Generic PolicyHttpClient timeout messages; SubscriptionFetcher logs type + redacted URL, not `ex`.
4. `CheckAsync` only returns a release with a valid SHA; `DownloadAndStageAsync` refuses extract without a digest (lite still requires sidecar/digest).
5. Tests: source pins + aggregator hy2 parse + redaction of exception objects + missing-SHA refuse; restage uniqueness test supplies a digest.

### Tests written

- `ApplyFreeConfigReadinessTests` — source pin: Connected-only `IsConnected = true`
- `FreeConfigMultiProtocolParseTests` — aggregator hy2/ss parse; Android/aggregator source pins
- `SubscriptionUrlRedactionTests` — timeout path: token absent from RenderMessage and Exception
- `UpdateCheckerChecksumTests.DownloadAndStageAsync_MissingDigest_RefusesExtract`
- `IUpdateSourceContractTests` — CheckAsync without sidecar returns null
- `UpdateCheckerStagingTests` — unique-dir test carries a matching SHA so it still stages

### Verification approach

Focused `dotnet test` filters for the new/updated tests, then Core/App oracles from `docs/agent-contract.md`. Full discovered suite before PR handoff if SDK is available. No WINBRAT.

## Verification gate

- [x] **Gate 1 — Build clean**: linux-worker isolated SDK 10.0.301 compiled the test project (Release). Full `VPNRouter.sln` not built (Android workload not required for this slice).
- [x] **Gate 2 — Tests green**: focused 21/21 PASS; Core/App oracles 77/77 PASS (`ConfigGeneratorTests|VpnEngineStartAsyncSeamTests|SingBoxManagerProcessRunnerTests|MainWindowViewModelCharacterizationTests|MainWindowViewModelAppsModeTests|MainWindowViewModelTests`). Full discovered suite not run this session.
- [x] **Gate 3 — Docs**: this Outcome filled; six mass-review P1s checked RESOLVED / UNRELEASED
- [x] **Gate 4 — Self-review**: lead SOL-verified the six sites after tests; leftover `VlessUriParser.Parse(raw|RawUri)` gone from the three product files; timeout URI interpolation gone; size-only extract gone
- [x] **Gate 5 — UI verify**: N/A — no XAML; ApplyFreeConfig is ViewModel-only
- [x] **Gate 6 — Characterization**: N/A — ApplyFreeConfigAsync stays private; MainWindowViewModelCharacterizationTests passed on linux-worker

## Outcome (filled before final handoff)

**Status**: PARTIAL — implementation + focused/oracle tests PASS; full suite and PR pending commit/push
**Commits**: `f0844469` (brief)
**Pushed**: not yet
**Test deltas**: +ApplyFreeConfigReadinessTests (2), +FreeConfigMultiProtocolParseTests (4), +Subscription timeout/source pins, +UpdateChecker missing digest, +GitHub missing sidecar
**Files changed**: product 10 + tests 6 + ledger/brief

**Gate results:**
- [x] Gate 1: test project Release build 0 errors (NU1900 nuget audit offline warnings)
- [x] Gate 2: focused 21 passed; oracles 77 passed
- [x] Gate 3: OPEN-DEFECTS six P1s marked resolved unreleased
- [x] Gate 4: SOL re-open of the six sites
- [-] Gate 5: N/A
- [-] Gate 6: N/A (characterization tests in oracle filter passed)

**Surprises encountered**:
- `VlessServerEntry.PrivateKey` does not exist; AWG key is `Awg.PrivateKey`
- harness-test has no SDK; tests ran on linux-worker debian-xfce as tester
- pre-commit on harness-test cannot run dotnet; implementation commit should run hooks on the worker

**Follow-ups spawned**:
- remaining P2s from the mass review (overflow, OpenServiceMenu Arguments, crash redactor, test restore, Unix CLI orphan sweep)
- zip/tar member/symlink policy still out of this SHA slice
- sequential-review skill installed globally; not a campaign run
