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

**Status**: SHIPPED (Wave 16, 2026-05-18). All verification gates green.

### What landed

**Interface + impls** (1 new file logically, 3 modified):
- `VPNRouter.Core/Services/IHttpClient.cs` — added `SendStreamingAsync` method
  on `IHttpClient`, new `IHttpStreamingResponse : IAsyncDisposable` interface
  with `StatusCode` / `Headers` / `ContentLength` / `Body` members, +
  `IsSuccess()` extension for streaming responses. (+77 LOC)
- `VPNRouter.Core/Services/PolicyHttpClient.cs` — `SendStreamingAsync`
  using `HttpCompletionOption.ResponseHeadersRead` + private nested
  `PolicyStreamingResponse` wrapper that owns
  `HttpResponseMessage` + body `Stream` + per-request linked CTS, disposes
  in order (body → response → request → CTS) with idempotent
  `Interlocked.Exchange` guard. (+148 LOC)
- `VPNRouter.Tests/Fakes/FakeHttpClient.cs` — `SendStreamingAsync` +
  `SetupStream(urlPattern, byte[])` + `ThrowOnStream(...)` + a
  `SentStreamingRequests` capture list separate from the existing
  `SentRequests`, backed by a `MemoryStream`-backed
  `FakeStreamingResponse` wrapper. (+144 LOC)

**Consumer migrations** (5 sites in 5 files, +444 LOC net):
| Consumer | Sites migrated | Changes |
|---|---|---|
| `ZapretUpdater.cs` | 1 (ZIP) + 1 (API JSON) | New IHttpClient ctor (parameterless delegates to `PolicyHttpClient.Shared`), `FetchGitHubJsonAsync` helper for buffered API call, `SendStreamingAsync` for ZIP download with `await using` disposal chain. (+86/-14 LOC) |
| `TgProxyUpdater.cs` | 3 (Python ZIP, wheel ZIP, proxy zipball) + 2 (API JSON) | New IHttpClient ctor; removed dedicated `pypiHttp` field (pypi.org now goes through shared client); all 3 streaming sites use `await using` + IsSuccess() check. (+116/-16 LOC) |
| `WgturnUpdater.cs` | 1 (binary) + 1 (API JSON) | New IHttpClient ctor, `FetchGitHubJsonAsync` helper, streaming download retains existing 3-retry envelope. (+79/-16 LOC) |
| `GeoDataDownloader.cs` | 1 (geo SRS files) | New IHttpClient ctor, `_http.GetAsync(..., ResponseHeadersRead)` → `SendStreamingAsync`. (+29/-13 LOC) |
| `UpdateChecker.cs` | 1 (binary update ZIP/tar.gz) | Removed retired `_legacyHttp` static field + static ctor; binary download routes through `_http.SendStreamingAsync` with 64 KB chunk progress reporting unchanged. (+24/-50 LOC) |

**Tests**: 6 new contract cases in `VPNRouter.Tests/IHttpClientStreamingContractTests.cs`:
1. `HappyPath_StreamsBody` — body bytes round-trip cleanly.
2. `HeadersAvailable_BeforeBodyRead` — pinned via `SlowStreamContent.BytesRead == 0` after status/headers/ContentLength inspection.
3. `Cancellation_AbortsStream_NoLeak` — `HangingStreamContent` + `cts.CancelAfter(50ms)` → asserts `OperationCanceledException` + clean dispose.
4. `DisposeBeforeReadComplete_AbortsConnection` — reads 4 bytes of 1 MB body then `DisposeAsync` × 2 (idempotency pin).
5. `LargeBody_5MB_Streams_NoOOM` — 5 MB body, 64 KB caller buffer, asserts >1 chunk delivered (progressive pin: a buffered impl would deliver everything in 1 read).
6. `FakeHttpClient_SetupStream_ReturnsConfiguredBytes` — 16-byte canned body round-trip + asserts `SentStreamingRequests.Count == 1` while `SentRequests.Count == 0` (channel-separation pin).

(+431 LOC for the new test file)

### Verification gate results

| Gate | Result |
|---|---|
| Interface + concrete + fake | DONE (3 files) |
| 5 consumer migrations | DONE (5 files) |
| 6 contract tests | DONE (6 cases, all pass in isolation 7-150 ms each) |
| **Gate 1**: `dotnet build VPNRouter.sln -c Release` | 0 errors / 1 pre-existing warning (VpnRouterTestMcp tooling) |
| **Gate 2**: scoped suite | 1094/1098 pass, 4 skipped (platform), 0 failed |
| **Gate 2b**: WgturnUpdaterTests | 17/17 pass — parameterless ctor preserved via delegation |
| **Gate 4**: security review | See "Security review" below |

### Security review

**Stream disposal under cancellation** — pinned by `Cancellation_AbortsStream_NoLeak`:
- `PolicyStreamingResponse.DisposeAsync` uses `Interlocked.Exchange(ref _disposed, 1)` so double-dispose is a no-op (await-using + manual Dispose in `finally` don't double-fault).
- Each underlying dispose (Body, response, request, CTS) is wrapped in its own try/catch so a half-aborted Stream doesn't prevent the response or CTS from being freed.
- Disposal order is deterministic: body → response → request → CTS. CTS is last so the linked timeout timer is removed only after the socket is freed (avoids a "CancelAfter fired on disposed CTS" benign exception surfacing as an unhandled task fault on a finalizer thread).

**5 MB stress test (OOM-safety)** — `LargeBody_5MB_Streams_NoOOM` asserts:
- ContentLength == 5 MB before any body read.
- Body delivers in >1 chunk (read into a 64 KB caller buffer); a buffered impl would return everything in 1 read.
- `HttpCompletionOption.ResponseHeadersRead` keeps the body progressive — the wire is not read past status/headers before the wrapper returns.

**No buffer leak on abort** — `DisposeBeforeReadComplete_AbortsConnection`:
- Reading 4 bytes of a 1 MB body then disposing the wrapper closes the underlying stream → connection is aborted at socket layer.
- Second `DisposeAsync` is a no-op (idempotency pin).

**Per-request CTS lifecycle** — the linked CTS owned by `PolicyStreamingResponse` lives for the wrapper's lifetime (not just for the headers phase) so any caller-driven cancellation propagates into the body read. The CTS is disposed last to avoid timer-fired-on-disposed warnings.

**Caller migrations** — 5 sites all use the canonical pattern:
```csharp
await using (var resp = await _http.SendStreamingAsync(...).ConfigureAwait(false))
{
    if (!resp.IsSuccess()) throw new HttpRequestException(...);
    using var file = File.Create(temp);
    await resp.Body.CopyToAsync(file, ct).ConfigureAwait(false);
}
```
`await using` ensures DisposeAsync fires even on `File.Create` / `CopyToAsync` failure → no half-read kernel buffer leaks. Each updater retains its own retry envelope (cleaning up partial files between attempts) because streaming responses can't be transparently retried.

### LOC delta + file count

| Category | LOC |
|---|---|
| Interface | +77 |
| PolicyHttpClient | +148 |
| FakeHttpClient | +144 |
| 5 consumer migrations (net) | +334 / -109 |
| Contract tests (new file) | +431 |
| **Total** | **+1139 / -104** across **9 files** |

### Surprises / deferred follow-ups

- `UpdateChecker.cs` had a `_legacyHttp` static field with a 30-s timeout dedicated solely to the streaming path. Removed entirely (its only caller migrated) so the shared `PolicyHttpClient` (5-min DNS refresh) now serves the binary download too — improves DNS rotation behaviour for long-uptime Service mode.
- `TgProxyUpdater` had a transient `pypiHttp` HttpClient created per-call inside `DownloadDependenciesAsync`. Removed; PyPI traffic now shares the process-wide pool. Header-set is GitHub's Accept value which PyPI tolerates — no semantic change.
- One flaky test (`MainWindowViewModelAppsModeTests.OnRoutingAppsModeChanged_FiresIsCheckedNotifications`) hits `C:\ProgramData\VPNRouter\config.yaml` lock during parallel headless-Avalonia runs. Failed in the first scoped suite run, passed in isolation + re-run. Documented in `VPNRouter.Tests/CLAUDE.md` "Known issues" as pre-existing.
- The brief listed `GeoDataDownloader.cs` (not `FreeConfigGeoIp.cs`); inspection confirmed `FreeConfigGeoIp` uses `HttpClient.SendAsync` (not streaming) for the ip-api.com batch endpoint — out of streaming scope. Migrated the correct file.
- `IHttpClient.SendStreamingAsync` deliberately ignores `HttpRequest.RetryCount` (per brief + XML doc) — silent mid-stream retry would corrupt the caller's file sink. Callers' existing 3-retry envelopes survive.

## Follow-up

- SingBoxManager `PutAsync` stop-fast-path is separate (sync-over-async,
  delicate v2.30.x interaction) — defer to Phase 4B.
- Streaming progress reporting (IProgress<long>) could land as Phase 4C
  cosmetic addition.
