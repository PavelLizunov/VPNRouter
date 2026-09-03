# Phase — Network Sockets, Bulk Pasting and Zero-Allocation Subscription Stream

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/perf-sockets-and-subscription-stream`
**Accepted base**: `origin/main` head `385c738e`
**Roadmap ref**: Audit Wave 3 & 4 / Performance & Resource Optimization
**Effort**: 0.5 days
**Risk**: LOW
**Blast radius**: Socket probing (`TcpTlsProbe.cs`), server importing (`MainWindowViewModel.cs`), URI parsing (`ServerUriParser.cs`, `VlessUriParser.cs`), Geo-data checking (`GeoDataDownloader.cs`), and unit tests.
**Rollback**: revert branch commit; restore prior implementations

## Why

1. Socket exhaustion: `TcpTlsProbe.cs` opens multiple TCP connections per tested server. When parallel testing runs with 80 concurrency, thousands of closed sockets linger in `TIME_WAIT` for 120s on Windows, exhausting the 16k ephemeral port space (`49152..65535`) and throwing `AddressAlreadyInUse` (10048).
2. Disk freeze on bulk paste: `MainWindowViewModel.AddServer()` calls `SaveSettings()` synchronously inside the `foreach` loop over every pasted line. Pasting 50 servers executes 50 sequential file copies and disk `fsync` calls, freezing the UI for 1–2.5 seconds.
3. Multi-megabyte subscription allocations: `ServerUriParser.ParseMultiple` and `VlessUriParser.ParseMultiple` call `text.Split('\n', '\r')`, allocating arrays with 20,000+ strings (3–6 MB Gen0 GC garbage) on large subscriptions.
4. Redundant disk stat calls: `GeoDataDownloader.AreGeoFilesAvailable()` checks file existence and file length 4 times per configuration generation without caching.

## What

1. In `TcpTlsProbe.cs`:
   - Set `LingerState = new LingerOption(true, 0)` on probe `TcpClient` instances to immediately tear down sockets with RST instead of parking in `TIME_WAIT`.
2. In `MainWindowViewModel.cs`:
   - Move `SaveSettings()` outside the `foreach (var line in lines)` loop in `AddServer()`, executing persistence once if any valid server was added.
3. In `ServerUriParser.cs` and `VlessUriParser.cs`:
   - Replace `text.Split('\n', '\r')` with `MemoryExtensions.EnumerateLines(text.AsSpan())` to iterate line spans without allocating array buffers.
4. In `GeoDataDownloader.cs`:
   - Cache `AreGeoFilesAvailable()` result and invalidate when geo-files are downloaded or updated.
5. Tests:
   - Contract test verifying `LingerState` in `TcpTlsProbe.cs`.
   - Contract test verifying batch persistence in `MainWindowViewModel.cs`.
   - Unit tests covering zero-allocation line enumeration in `ServerUriParser.ParseMultiple` and `VlessUriParser.ParseMultiple`.
   - Unit tests covering `GeoDataDownloader.AreGeoFilesAvailable()` caching.

## How

1. Commit phase brief.
2. Implement socket linger, batch save, stream parsing, and geo caching.
3. Add unit tests in `VPNRouter.Tests`.
4. Multi-iteration verification (build/tests, Opus adversarial review, GitHub Actions CI).
5. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [ ] Gate 1 — Build clean: Release solution build completes with zero errors.
- [ ] Gate 2 — Tests green: all unit and characterization tests pass (0 failures).
- [ ] Gate 3 — Docs: outcome recorded and plans updated.
- [ ] Gate 4 — Adversarial review: Opus swarm review confirms no regressions or lost servers.
- [ ] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

Pending execution.
