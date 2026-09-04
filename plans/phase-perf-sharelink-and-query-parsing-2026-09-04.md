# Phase — Multi-Protocol Share Link and Query Parsing Performance Optimization

**Owner**: DSH session `session-527962d1-ce92-41c3-b855-73d0c090e510`
**Branch**: `dsh/perf-sharelink-and-query-parsing`
**Accepted base**: `origin/main` head `b7ce0e4f`
**Roadmap ref**: Audit Wave 4 / Performance & Resource Optimization
**Effort**: 0.5 days
**Risk**: LOW
**Blast radius**: Core URI and share-link parsing (`VlessUriParser.cs`, `ServerUriParser.cs`, new `ShareLinkHelper.cs`), unit tests (`PerformanceShareLinkTests.cs`).
**Rollback**: revert branch commit; restore prior implementations

## Why

1. Profiler-guided analysis with `dotnet-trace` revealed that `System.Web.HttpUtility.ParseQueryString` accounted for **76.45% of total CPU time** during multi-protocol server link ingestion and subscription parsing.
2. In `HttpUtility.ParseQueryString`, internal `NameValueCollection`, `ArrayList`, and `Hashtable` allocations create massive Gen 0 GC garbage and array copy overhead when processing hundreds or thousands of share links (subscription refresh, bulk pasting, free config verification).
3. In `VlessUriParser.cs` and `ServerUriParser.cs`, every protocol parser (`vless://`, `hysteria2://`, `tuic://`, `ss://`, `naive://`) allocated a pseudo-URI string `"https://" + uri.Substring(...)`, instantiated a heavy `System.Uri` object, and repeatedly extracted string properties (`UserInfo`, `Host`, `Query`, `Fragment`) with multiple intermediate allocations.
4. In `ServerUriParser.ParseMultiple`, `IsSupportedScheme` allocated strings on every line span even for non-matching lines.

## What

1. Created `VPNRouter.Core/Services/ShareLinkHelper.cs`:
   - High-performance, zero-allocation span-based URI component extractor (`ParseComponents`) that parses `userinfo`, `host`, `port`, `query`, and `fragment` directly from `ReadOnlySpan<char>` without `System.Uri` or string concatenations.
   - Lightweight `QueryDictionary` struct wrapping a fast case-insensitive `Dictionary<string, string>` that returns `null` for missing keys without throwing `KeyNotFoundException`.
   - Reusable `Unescape` helper that bypasses `Uri.UnescapeDataString` when no `%` characters are present.
   - Strict port boundary enforcement: validates `0..65535`, falls back `:0` to `443`, and throws `FormatException` on out-of-range ports (`:70000`).
2. Updated `VPNRouter.Core/Services/VlessUriParser.cs`:
   - Replaced pseudo-URI `https://` concatenation, `System.Uri` instantiation, and `HttpUtility.ParseQueryString` with `ShareLinkHelper`.
   - Removed `using System.Web;`.
3. Updated `VPNRouter.Core/Services/ServerUriParser.cs`:
   - Unified `ParseHysteria2`, `ParseTuic`, `ParseShadowsocks`, and `ParseNaive` to use `ShareLinkHelper.ParseComponents` and `ShareLinkHelper.ParseQuery`.
   - Added `IsSupportedScheme(ReadOnlySpan<char>)` overload so non-matching lines in `ParseMultiple` do not allocate strings.
   - Preserves literal `+` in base64 keys without silent space corruption (superceding `ParseQueryPreservingPlus`).
   - Removed `using System.Web;`.
4. Added unit tests in `VPNRouter.Tests/PerformanceShareLinkTests.cs`:
   - Pinned `ShareLinkHelper.ParseComponents` for VLESS, IPv6 bracketed hosts, port fallback, and port overflow rejection.
   - Pinned `ShareLinkHelper.ParseQuery` case-insensitivity and empty/flag parameter handling.
   - Pinned `ServerUriParser.IsSupportedScheme` span overload parity with string overload.

## How

1. Commit phase brief and implementation on task branch `dsh/perf-sharelink-and-query-parsing`.
2. Add unit tests in `VPNRouter.Tests`.
3. Multi-iteration verification (local unit tests, Windows-native benchmark, GitHub Actions CI).
4. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [x] Gate 1 — Build clean: Release solution build completes with zero errors on both Linux and Windows.
- [x] Gate 2 — Tests green: all unit tests pass (78/78 tests in `ServerUriParserTests`, `VlessUriParserTests`, `AmneziaWgEndpointTests`, `ConfigGeneratorTests`, `PerformanceStreamAndSocketTests`, `PerformanceShareLinkTests` pass with 0 failures natively on Windows).
- [x] Gate 3 — Windows-native benchmark: 56.88% speedup (Control median 9.96 ms -> Candidate median 4.56 ms, delta -5.67 ms, confidence 17.6x MAD) verified natively on WINBRAT (Windows 10 Enterprise LTSC).
- [x] Gate 4 — Docs: phase brief and outcome recorded in `plans/`.

## Outcome

**Status**: READY FOR OWNER REVIEW / PR
**Pushed**: `origin/dsh/perf-sharelink-and-query-parsing`
**Files changed**:
- `VPNRouter.Core/Services/ShareLinkHelper.cs`: new span-based multi-protocol link parser and lightweight `QueryDictionary`.
- `VPNRouter.Core/Services/VlessUriParser.cs`: zero-allocation span component extraction and query parsing.
- `VPNRouter.Core/Services/ServerUriParser.cs`: unified protocol parsing on `ShareLinkHelper` and zero-allocation `IsSupportedScheme(ReadOnlySpan<char>)`.
- `VPNRouter.Tests/PerformanceShareLinkTests.cs`: unit tests pinning parsing, IPv6, port bounds, and scheme probing.
