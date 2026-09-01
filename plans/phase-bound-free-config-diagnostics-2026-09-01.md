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

- [ ] Valid plain/base64 source bodies still extract and deduplicate VLESS URIs.
- [ ] Production fetch uses `PolicyHttpClient.Shared`, a per-request 4 MiB read ceiling, a 10-second timeout, and one transient/timeout retry.
- [ ] A source body above 4 MiB is rejected before text decoding/parsing.
- [ ] Caller cancellation propagates while transport/status/timeout failures remain non-throwing.
- [ ] UUIDs, proxy URIs, query tokens, and long keys never survive verifier buffering/log snippets.
- [ ] Diagnostic buffers remain bounded under repeated/concurrent callback input.
- [ ] Both VLESS and free-config verifiers use the shared sanitized append path.
- [ ] Focused contracts and full exact-head CI pass.
- [ ] Independent correctness/security/test review has no surviving P0/P1.

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

Pending implementation and verification.
