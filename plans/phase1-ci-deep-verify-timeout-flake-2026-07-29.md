# Phase 1 — CI flake: DeepVerifyProbeCancellationTests client-timeout shape

Status: PENDING (awaiting orchestrator push + GitHub CI)
Date: 2026-07-29
Branch base: `origin/main` @ `b39a28c3` (Merge PR #47)
Scope: test-only. No production code changed.

## Why

`DeepVerifyProbeCancellationTests.ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout`
fails nondeterministically on GitHub CI (ubuntu `test` job). The same historical
red hit merge commit `43649922` (PR #45, run 29481203691) and the open dependabot
PR #29 (run 30138066328). Both reds are the SAME latent flake on `main`, not a
regression introduced by either PR (PR #45 touched only
`SingBoxManagerProcessExitLeakTests.cs`; PR #29 touches only `.github/workflows`).

Confirmed failure signature (run 29481203691, job 87565122788):

```
Assert.Equal() Failure: Strings differ
Expected: "http timeout"
Actual: "http: An error occurred while establishing a conne"...
at .../DeepVerifyProbeCancellationTests.cs:line 55
```

### Root cause (from code + failure evidence)

The test harness (`StartSilentListener`) accepts the loopback TCP connection but
never replies, so `HttpClient` stalls in the SOCKS/TLS **connect** phase against
the `https` ProbeUrl (`https://www.cloudflare.com/cdn-cgi/trace`). The 500 ms
`HttpClient.Timeout` therefore RACES the connection layer:

- If the timeout wins cleanly on the pending operation, the runtime throws
  `TaskCanceledException` → `DeepVerifyProbe.ProbeViaSocksAsync` maps it to
  `"http timeout"` (the string the test asserted).
- If the connect abort surfaces first, `SocketsHttpHandler` wraps it in
  `HttpRequestException` ("An error occurred while establishing a connection…")
  → the probe maps it to `"http: {message}"`.

Both are valid platform/runtime outcomes of the SAME "client gave up waiting,
no external cancel" event. Which one wins is thread-scheduling dependent, hence
the flake. See `VPNRouter.Core/Services/DeepVerifyProbe.cs` catch chain
(`catch (OperationCanceledException) when (ct.IsCancellationRequested)` rethrow →
`catch (TaskCanceledException)` → `"http timeout"` → `catch (HttpRequestException)`
→ `"http: …"`).

### Production is correct — no production change

The F1 contract is: EXTERNAL cancellation (user Cancel / caller budget) rethrows
as `OperationCanceledException`; the client's OWN timeout is a graceful,
server-meaningful http failure. Both callers consume the result identically
regardless of the exact string:

- `VlessDeepVerifier.VerifyAsync` (line ~320): `if (!httpOk) return
  DeepVerifyResult.Failed(httpErr ?? "http failed", DeepVerifyFailurePhase.ProxiedHttp);`
  — the phase is `ProxiedHttp` for BOTH `"http timeout"` and `"http: …"`; the
  string is only a human-readable reason.
- `FreeConfigDeepVerifier` (line ~168): `!httpOk` downgrades status to
  `TlsFailed` and stores `LastError = httpErr` — identical behavior for both
  strings.

So a timeout surfacing as `HttpRequestException` is NOT mis-branded: it still
lands as a `ProxiedHttp` failure, never as the false `ProtocolHandshakeBlockedLikely`
that F1 was fixing (that bug was the EXTERNAL-cancel path being swallowed, which
the rethrow filter already handles). A test-only correction is therefore correct;
changing production to force a single string would add fragile exception-shape
sniffing for zero behavioral gain.

## What

Make the assertion deterministic by accepting exactly the two valid runtime
shapes of a no-external-cancel client timeout, and rejecting everything else.
The pinned contract is unchanged: with `CancellationToken.None`, the call must
RETURN a graceful failure (`ok=false`, non-empty reason), never rethrow as
cancellation.

File changed: `VPNRouter.Tests/DeepVerifyProbeCancellationTests.cs`
(method `ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout` only).

Before:

```csharp
Assert.False(ok);
Assert.Equal("http timeout", err);
```

After:

```csharp
Assert.False(ok);
Assert.False(string.IsNullOrWhiteSpace(err));
Assert.True(
    err == "http timeout" || err!.StartsWith("http: ", StringComparison.Ordinal),
    $"expected a client-timeout http failure (\"http timeout\" or \"http: …\"), got: \"{err}\"");
```

The sibling `ExternalCancellation_Rethrows_NotHttpTimeout` test is untouched and
still pins the rethrow half of the contract.

## How

- Test-only edit in the existing dedicated file; reuses the existing
  `StartSilentListener` harness and the existing exception contract. No new
  helper, abstraction, dependency, suite, retry, or sleep.
- `StringComparison.Ordinal` needs no new `using` — the test project sets
  `<ImplicitUsings>enable</ImplicitUsings>` (the file already uses `TimeSpan` /
  `CancellationToken` without explicit `using System;`).
- The accepted set is deliberately narrow (`"http timeout"` OR prefix
  `"http: "`). It is NOT a catch-all: `ok=true`, an empty/null reason,
  `"local ip in response"`, `"bad response"`, `"http 500"`, or a rethrown
  `OperationCanceledException` (which would escape the `await` and fail the test)
  all still fail.

## Risk

- Low. Test-only; zero product/runtime surface touched.
- The assertion is strictly narrower than "accept any error" — it still fails
  for genuine regressions (wrong status, silent failure, proxy leak, or the F1
  regression of rethrowing a client timeout as cancellation).
- Residual: if a future runtime surfaces the client timeout in a third shape
  (e.g. a bare `OperationCanceledException` reaching the probe's generic
  `catch (Exception)` → `"OperationCanceledException"`), the test will fail and
  surface it for review rather than silently passing. That is the intended
  behavior, not a defect of this fix.

## Evidence links

- Failed run (merge commit 43649922 / PR #45):
  https://github.com/PavelLizunov/VPNRouter/actions/runs/29481203691
  job: https://github.com/PavelLizunov/VPNRouter/actions/runs/29481203691/job/87565122788
- Failed required `test` check on PR #29 (same flake, run 30138066328 — 2528
  passed / 2 failed, both in `DeepVerifyProbeCancellationTests`).
- Triage report (this flake diagnosed, minimal action recommended):
  `C:\Project\VPNRouter-qwen-pr-triage-2026-07-29\plans\qwen-open-pr-triage-2026-07-29.md`
  (section "Commit 43649922 / PR #45 — упавший test").
- Code: `VPNRouter.Core/Services/DeepVerifyProbe.cs` (`ProbeViaSocksAsync` catch
  chain), `VPNRouter.Core/Services/DeepVerifyConstants.cs` (https ProbeUrl),
  `VPNRouter.Core/Services/VlessDeepVerifier.cs` (~line 320),
  `VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs` (~line 168).

## Remote-only verification gates

No local build/test/run is performed in this session (resource restriction).
Verification happens only after the orchestrator reviews and pushes the diff:

1. `git push -u origin HEAD` of the task branch (orchestrator).
2. Required `test` check (ubuntu, `test.yml`) green on the pushed commit —
   confirms `DeepVerifyProbeCancellationTests` passes both methods.
3. Per rule #11, run `tools/verify-last-commit-ci.ps1` after push; exit 0 before
   any further change.
4. Spot-check the run shows the two cancellation tests passed (not skipped):
   `gh run view <run> --repo PavelLizunov/VPNRouter --log` filtered on
   `DeepVerifyProbeCancellationTests`.
5. Because the flake is timing-dependent, a single green run is necessary but a
   repeat green on a re-run raises confidence; a red on the SAME assertion would
   indicate a third exception shape and must be investigated, not retried away.

## Rollback

- Single-file test change. Revert = `git revert <commit>` or restore
  `VPNRouter.Tests/DeepVerifyProbeCancellationTests.cs` to `b39a28c3`.
- No production code, schema, config, CI workflow, or release artifact is
  touched, so there is no downstream state to unwind.

## Outcome

PENDING — diff authored and statically self-reviewed; awaiting orchestrator push
and GitHub CI result. Fill in after the `test` check completes:

- [ ] Pushed commit SHA: ____
- [ ] CI run id / result: ____
- [ ] `DeepVerifyProbeCancellationTests` both methods passed: ____
- [ ] `tools/verify-last-commit-ci.ps1` exit code: ____
