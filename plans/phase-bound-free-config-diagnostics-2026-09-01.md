# Phase: Bound free-config fetches and scrub verifier diagnostics

Base: `origin/main` / `e58ac75d7239ecb9ca23b51ef81a5ccfc0f8d255`
Branch: `dsh/bound-free-config-diagnostics`
Audit IDs: `SU-2-2`, `SU-2-3`

## 1. Intent & Invariants

- **What:** Route free-config source downloads through the repository HTTP policy with a dedicated body ceiling, and redact/cap sing-box verifier output before it enters diagnostic buffers, logs, or user-visible errors.
- **Invariants:** caller cancellation still propagates; timeout/network/status failures still return an empty source result; retry remains one additional transient attempt; no unbounded response or process-output buffer remains; redaction precedes truncation; no release/tag/merge/deploy/install occurs.

## 2. Interface / Data Contract

```csharp
public FreeConfigFetcher(ILogger logger); // PolicyHttpClient.Shared
internal FreeConfigFetcher(ILogger logger, IHttpClient http); // deterministic tests
internal const int MaxSourceBytes = 4 * 1024 * 1024;

// Non-positional: preserves HttpRequest constructor/deconstruction shape.
public sealed record HttpRequest(/* existing positional members */)
{
    public long? MaxResponseBytes { get; init; }
}

DeepVerifyProbe.AppendSanitizedLine(
    StringBuilder destination,
    string? line,
    int maxChars);

// source body > MaxSourceBytes => [] and bounded diagnostic
// cancellation token cancelled => OperationCanceledException
// verifier line => CrashReporter.ScrubSecrets before bounded append
```

## 3. Verification Checklist (Definition of Done)

- [x] Valid plain/base64 source bodies still extract and deduplicate VLESS URIs.
- [x] Production fetch uses `PolicyHttpClient.Shared`, a per-request 4 MiB read ceiling, a 10-second timeout, and one transient/timeout retry.
- [x] A source body above 4 MiB is rejected before text decoding/parsing.
- [x] Caller cancellation propagates while transport/status/timeout failures remain non-throwing.
- [x] UUIDs, proxy URIs, query tokens, and long keys never survive verifier buffering/log snippets.
- [x] Diagnostic buffers remain bounded under repeated/concurrent callback input.
- [x] Both VLESS and free-config verifiers use the shared sanitized append path.
- [x] Focused contracts and full exact-head CI pass.
- [x] Independent correctness/security/test review has no surviving P0/P1.

## Risk / rollback

- Risk: a cap that is too low could reject a legitimate aggregate source; transport migration could alter retry/cancellation semantics.
- Control: 4 MiB is far above normal text subscriptions but below the shared 32 MiB policy ceiling; inject the repository HTTP seam and pin the exact request envelope and failures.
- Rollback: revert this task PR; no persisted schema or migration exists.

## Six gates

1. **Scope:** fetcher, existing HTTP request/policy and diagnostics/probe primitives, two verifier call sites, focused tests, ledger, and this brief.
2. **Trust boundary:** bytes and process lines are bounded and scrubbed before parsing/logging.
3. **Compatibility:** source extraction and caller cancellation remain unchanged.
4. **Tests:** no real network or sing-box process; all seams are deterministic.
5. **Review:** independent network, redaction, and test lenses; lead verifies each claim.
6. **Handoff:** scoped commits, PR, exact-head green; owner alone decides merge/release.

## Outcome

- Implementation commits: `13c80227e58bb17ddf06f675ac294591e260fdfa` and boundary-test head `cad86133`; PR: #209.
- Scope: 13 files, `+512/-82` over the fixed audit base. The fetcher now uses the shared policy with a transport-time 4 MiB ceiling; a non-positional `HttpRequest.MaxResponseBytes` can only narrow the process-wide 32 MiB cap; per-request timeout retry and caller cancellation are deterministic.
- Verifier stderr is passed through the existing `DiagnosticsRedactor.RedactLogText` before a locked 2,048-character append. VLESS and free-config desktop call sites use the same bounded path, including quoted JSON, short-id, token, UUID, URI, and long-key redaction.
- Adversarial repair rounds closed post-buffer cap enforcement, lost timeout retry, a false-positive cancellation test, short/JSON secret gaps, and an override that could widen the global hard cap. A claim that `InvalidDataException` inherited `IOException` was refuted against the [.NET 10 API contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.invaliddataexception?view=net-10.0); RetryCount=1 plus one-dispatch overflow coverage mechanically confirms no retry.
- Primary platform guidance: [.NET `ResponseHeadersRead`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-9.0) requires callers to bound and time content reads separately; [.NET HTTP client guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) recommend long-lived pooled clients. The implementation reuses both existing repository mechanisms instead of adding a dependency.
- Exact product-head workflow passed twice with 2,845 total / 2,788 passed / 57 skipped. The expanded boundary-test head passed 2,848 total / 2,791 passed / 57 skipped; `characterization-windows`, `go-test-windows`, and `grep` passed. The local control plane has no `dotnet`, so GitHub Actions was the mechanical oracle.
- Three independent final network/security/test reviews returned CLEAN, and the final boundary-test review was CLEAN. Ouroboros QA session `qa-251224d3` passed the approved trust-boundary ACs at `0.92`.
- Rollback is a plain PR revert; no persisted schema, migration, dependency, README contract, release, tag, merge, deploy, or install is involved.
