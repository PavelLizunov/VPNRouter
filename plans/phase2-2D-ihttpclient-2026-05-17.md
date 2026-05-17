# Phase 2 — 2D-3: `IHttpClient` abstraction

**Owner**: Wave 6 parallel agent (3 of 4)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2D; plans/v3.0-architecture-roadmap.md §4 "6 static readonly HttpClient fields scattered"
**Effort**: 1 day
**Risk**: MEDIUM (new public interface; touches 6+ HTTP call sites)

## Why
Audit D found 6 `static readonly HttpClient` fields scattered across services. They each carry their own connection pool, no shared DNS cache, no shared retry policy. Per .NET 8 guidance, prefer `IHttpClientFactory`-style injection.

Audit E flagged: `UpdateChecker`, `SubscriptionFetcher`, `VlessDeepVerifier`, `FreeConfigPoolFetcher` all instantiate or use static HttpClients without a mocking seam. Tests can't stub HTTP responses.

Extract `IHttpClient` that wraps `HttpClient` with policies (timeout, retry, telemetry). Inject. Tests get `FakeHttpClient` with route-matchers.

## What

Create `VPNRouter.Core/Services/IHttpClient.cs`:

```csharp
namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over System.Net.Http.HttpClient with policy bundle:
/// - 30s default timeout (per-call override via HttpRequest.Timeout)
/// - Connection pool reuse (PooledConnectionLifetime = 5 min — DNS refresh)
/// - Optional retry-on-transient-failure (off by default; callers opt in)
/// Single instance shared across services via IHttpClientProvider DI;
/// concrete `PolicyHttpClient` and fake `FakeHttpClient` for tests.
/// </summary>
public interface IHttpClient
{
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default);
}

public sealed record HttpRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Body = null,
    string? BodyContentType = null,
    TimeSpan? Timeout = null,
    int RetryCount = 0,
    TimeSpan? RetryBaseDelay = null);

public sealed record HttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    TimeSpan Duration);
    
public static class HttpResponseExtensions
{
    public static string AsString(this HttpResponse r) => System.Text.Encoding.UTF8.GetString(r.Body);
    public static T AsJson<T>(this HttpResponse r) => System.Text.Json.JsonSerializer.Deserialize<T>(r.Body) ?? throw new InvalidOperationException();
    public static bool IsSuccess(this HttpResponse r) => r.StatusCode >= 200 && r.StatusCode < 300;
}
```

Concrete `PolicyHttpClient.cs`:
- Wraps `HttpClient` (singleton via static field — but injected via interface)
- Uses `SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }` per current best practice
- Retry logic if `request.RetryCount > 0`: exponential backoff with jitter

Fake `VPNRouter.Tests/Fakes/FakeHttpClient.cs`:
- `Setup(url, response)` for canned responses
- Records all sent requests for assertions
- `ThrowOn(url, exception)` for failure-mode tests

Refactor 1 high-traffic call site as POC: `UpdateChecker.cs`. It currently uses `_http.GetStringAsync(url)` — switch to `_http.SendAsync(new HttpRequest(HttpMethod.Get, new Uri(url)))`. Verify the existing update-check tests still pass.

## How

**Step 1** — Write interface + types. Stick to `byte[]` body (not `Stream`) for testability simplicity — VPNRouter never downloads >50 MB in one shot.

**Step 2** — `PolicyHttpClient` concrete:
```csharp
public sealed class PolicyHttpClient : IHttpClient
{
    private static readonly HttpClient _shared = new(new SocketsHttpHandler {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(30) };
    
    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        // construct HttpRequestMessage from HttpRequest
        // apply retry policy if RetryCount > 0
        // await _shared.SendAsync(msg, ct)
        // construct HttpResponse from HttpResponseMessage
    }
}
```

**Step 3** — `FakeHttpClient`:
```csharp
public sealed class FakeHttpClient : IHttpClient
{
    private readonly Dictionary<string, Func<HttpRequest, HttpResponse>> _routes = new();
    public List<HttpRequest> SentRequests { get; } = new();
    
    public FakeHttpClient Setup(string urlPattern, HttpResponse response) { ... }
    public FakeHttpClient ThrowOn(string urlPattern, Exception ex) { ... }
    
    public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct)
    {
        SentRequests.Add(request);
        // match against routes by Uri/method
        // return canned response OR throw
    }
}
```

**Step 4** — Refactor `UpdateChecker.cs`. Inject via ctor (default `new PolicyHttpClient()` for back-compat). Replace `_http.GetStringAsync(url)` with `var r = await _http.SendAsync(new HttpRequest(...)); var body = r.AsString();`

**Step 5** — Write 6 contract tests:
- `Send_HappyPath_ReturnsResponse`
- `Send_Timeout_ThrowsTimeoutException`
- `Send_RetryCount2_RetriesTwiceOnTransientFailure`
- `Send_NonSuccessStatus_DoesNotThrow_ReturnsResponse`
- `FakeHttpClient_Setup_ReturnsCannedResponse`
- `FakeHttpClient_SentRequests_RecordsAllCalls`

## Verification gate
- [ ] Interface ergonomic
- [ ] `PolicyHttpClient` uses PooledConnectionLifetime per .NET 8 best practice
- [ ] `FakeHttpClient` thread-safe enough for parallel tests
- [ ] UpdateChecker refactor compiles + existing update tests pass
- [ ] 6 new contract tests pass
- [ ] **Gate 1**: build clean
- [ ] **Gate 2**: full suite stable
- [ ] **Gate 4 self-review**: `simplify` + `security-review` (HTTP touches auth tokens for GitHub API)
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

**Follow-up**: Phase 2G converts `SubscriptionFetcher`, `VlessDeepVerifier`, `FreeConfigPoolFetcher` to `IHttpClient`. Phase 3F splits IUpdateSource per-platform on top of IHttpClient.
