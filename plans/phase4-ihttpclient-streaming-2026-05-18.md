# Phase 4 — IHttpClient streaming primitive

**Owner**: Wave 16 single agent
**Roadmap ref**: Phase 3G-2 deferred 4 streaming consumers; this unblocks them
**Effort**: 1-2 days
**Risk**: MEDIUM (cancellation + dispose audit critical; large-file downloads)

## Why

Phase 2D-3 introduced `IHttpClient` with byte[] body (`HttpResponse.Body`).
Phase 3G-2 migrated 4 HTTP consumers but deferred 4 more because they use
`GetStreamAsync` for ZIP / binary downloads:
- `ZapretUpdater.cs` — downloads Flowseal zapret-discord-youtube release ZIP
- `TgProxyUpdater.cs` — downloads tg-ws-proxy release ZIP
- `WgturnUpdater.cs` — downloads wgturn-cli release ZIP
- `GeoDataDownloader.cs` — downloads MaxMind GeoIP2 .mmdb file

Plus `UpdateChecker.cs` (binary update download path post-Phase 3F).

Without a streaming primitive on `IHttpClient`, these consumers can't unify
through the IHttpClient seam. Adding streaming:
- Unblocks Phase 3G-2 completion
- Brings ZIP-download retry policy + DNS-pool sharing to those paths
- Enables FakeHttpClient to simulate progressive download in tests

## What

Extend `IHttpClient`:

```csharp
/// <summary>
/// Streaming variant of SendAsync for large-file downloads (ZIP, binary).
/// Returns the response stream + status code + headers WITHOUT buffering
/// the full body. Caller MUST dispose the returned IHttpStreamingResponse
/// before disposing the client (preferably via `await using`).
///
/// Cancellation: linked to caller's CancellationToken; on cancel the
/// underlying stream is aborted + disposed. No partial buffer leaks.
/// </summary>
Task<IHttpStreamingResponse> SendStreamingAsync(
    HttpRequest request,
    CancellationToken ct = default);
```

```csharp
public interface IHttpStreamingResponse : IAsyncDisposable
{
    int StatusCode { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    long? ContentLength { get; }
    Stream Body { get; }   // disposing this disposes the underlying HTTP stream
}
```

Concrete `PolicyHttpClient.SendStreamingAsync`:
- Uses `HttpCompletionOption.ResponseHeadersRead` so the body stream is
  truly progressive (not buffered)
- Returns a wrapper that owns the HttpResponseMessage + Stream and
  disposes both on `DisposeAsync`

Fake `FakeHttpClient.SendStreamingAsync`:
- Returns a MemoryStream wrapper of the configured response Body
- Supports `SetupStream(urlPattern, byte[])` for tests
- Records all SendStreamingAsync calls in `SentStreamingRequests`

Migrate the 5 consumers:
1. **ZapretUpdater.cs** — replace `GetStreamAsync` with `SendStreamingAsync` + `await using`
2. **TgProxyUpdater.cs** — same
3. **WgturnUpdater.cs** — same
4. **GeoDataDownloader.cs** — same
5. **UpdateChecker.cs** binary-download path — same

## How

**Step 1**: Extend `VPNRouter.Core/Services/IHttpClient.cs` with `SendStreamingAsync` + `IHttpStreamingResponse`.

**Step 2**: Implement `PolicyHttpClient.SendStreamingAsync` with proper disposal chain:
```csharp
public async Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest req, CancellationToken ct)
{
    var msg = BuildHttpRequestMessage(req);
    var resp = await _shared.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
    return new PolicyStreamingResponse(resp, await resp.Content.ReadAsStreamAsync(ct));
}
```

**Step 3**: Add `FakeHttpClient.SendStreamingAsync` + `SetupStream` helper.

**Step 4**: Migrate each consumer (one commit per consumer for bisect-friendly history):
- Replace `_http.GetStreamAsync(url)` with `await using var resp = await _http.SendStreamingAsync(new HttpRequest(...));`
- Use `resp.Body` instead of the returned stream
- Verify cancellation + dispose chain

**Step 5**: Tests:
- `IHttpClientStreamingContractTests.cs` (6 cases):
  - HappyPath_StreamsBody
  - DisposeBeforeReadComplete_AbortsConnection
  - Cancellation_AbortsStream_NoLeak
  - LargeBody_5MB_Streams_NoOOM
  - HeadersAvailable_BeforeBodyRead
  - FakeHttpClient_SetupStream_ReturnsConfiguredBytes

## Verification gate
- [ ] IHttpStreamingResponse interface + PolicyHttpClient + FakeHttpClient impl
- [ ] 5 consumer migrations (ZapretUpdater + TgProxyUpdater + WgturnUpdater + GeoDataDownloader + UpdateChecker binary)
- [ ] 6 contract tests added
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite green + 6 new
- [ ] **Gate 2b**: existing WgturnUpdaterTests + any TgProxy/Zapret tests stay green
- [ ] **Gate 4 security-review**: stream disposal under cancellation + 5MB stress
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- SingBoxManager `PutAsync` stop-fast-path is separate (sync-over-async,
  delicate v2.30.x interaction) — defer to Phase 4B.
- Streaming progress reporting (IProgress<long>) could land as Phase 4C
  cosmetic addition.
