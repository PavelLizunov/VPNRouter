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

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: focused filters + discovered suite
- [ ] **Gate 3 — Docs**: this Outcome filled; OPEN-DEFECTS P1s checked when verified
- [ ] **Gate 4 — Self-review**: bug-hunt / sequential SOL on the diff
- [ ] **Gate 5 — UI verify**: N/A — no XAML; ApplyFreeConfig is ViewModel-only
- [ ] **Gate 6 — Characterization**: N/A — ApplyFreeConfigAsync stays private; public surface unchanged

## Outcome (filled before final handoff)

**Status**: in progress
**Commits**: (pending)
**Pushed**: (pending)
**Test deltas**: (pending)
**Files changed**: (pending)
