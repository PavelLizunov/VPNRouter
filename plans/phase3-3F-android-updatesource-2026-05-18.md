# Phase 3 — 3F: Android `IUpdateSource` per-platform abstraction

**Owner**: Wave 10 parallel agent (4 of 4)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` §3F
**Effort**: 3 days
**Risk**: LOW-MEDIUM (Android update path; deferred Play Store dispatch)

## Why

Audit B: Android currently downloads update APK via `UpdateChecker` shared with desktop. This couples Android distribution to GitHub Releases — blocks future Play Store distribution. Extract `IUpdateSource` interface; concrete impls per platform.

## What

Create `VPNRouter.Core/Services/IUpdateSource.cs`:

```csharp
public interface IUpdateSource
{
    Task<UpdateInfo?> CheckAsync(CancellationToken ct);
    Task<Stream> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct);
    Task<bool> ApplyAsync(UpdateInfo info, Stream downloadedBytes, CancellationToken ct);
}

public sealed record UpdateInfo(
    string Version,
    string ReleaseUrl,
    string AssetName,
    long AssetSize,
    string? AssetSha256,
    bool IsPrerelease);

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes);
```

Concrete implementations:

1. **`GitHubReleaseSource`** (desktop default) — wraps current `UpdateChecker` logic; checks GitHub Releases API for `v*.zip` and `v*.tar.gz` per platform.
2. **`SideloadSource`** (current Android) — same GitHub Releases path but for `.apk`. Used today.
3. **`PlayStoreSource`** (future Android, scaffold only) — placeholder returning `null` from CheckAsync. Lets `BuildVariants` switch between sideload + Play Store.

`UpdateChecker` becomes a thin wrapper that delegates to `IUpdateSource` per-platform via `PlatformServices`:

```csharp
// In PlatformServices
public static IUpdateSource CreateUpdateSource(AppSettings settings) => OperatingSystem.IsAndroid()
    ? new SideloadSource(...)      // or PlayStoreSource if BuildVariant=play
    : new GitHubReleaseSource(...);
```

## How

**Step 1** — Read `UpdateChecker.cs` (Phase 2D-3 already wired `IHttpClient` — use that seam). Catalog the check + download + apply surface.

**Step 2** — Build `IUpdateSource.cs` + `UpdateInfo` + `DownloadProgress` records.

**Step 3** — Extract `GitHubReleaseSource` from existing `UpdateChecker` logic. Refactor `UpdateChecker.CheckAsync` to delegate.

**Step 4** — Build `SideloadSource` (Android-specific APK install path; uses `Intent.ActionView` for system installer).

**Step 5** — Stub `PlayStoreSource` — `CheckAsync` returns `null` (Play Store handles its own updates), `Download/Apply` throws `NotSupportedException`. Add a TODO comment with the Play Console API endpoint when distribution lands.

**Step 6** — Update `PlatformServices.CreateUpdateSource` factory.

**Step 7** — Tests:
- `IUpdateSourceContractTests`: `CheckAsync_HappyPath_ReturnsInfo` against FakeHttpClient + canned GitHub Release JSON
- `IUpdateSourceContractTests`: `CheckAsync_NoNewerVersion_ReturnsNull`
- `IUpdateSourceContractTests`: `DownloadAsync_StreamingProgress_ReportsBytes`
- `SideloadSourceTests`: `ApplyAsync_ApkPath_InvokesIntent` (mocked Android context — may be Android-only test, gate by `#if PLATFORM_WINDOWS` inverted or PLATFORM_ANDROID)

## Verification gate
- [ ] IUpdateSource interface + 3 concrete impls (GitHubReleaseSource, SideloadSource, PlayStoreSource-stub)
- [ ] UpdateChecker refactored to delegate to GitHubReleaseSource (desktop) / SideloadSource (Android)
- [ ] PlatformServices.CreateUpdateSource factory wired
- [ ] Existing update-check tests still pass
- [ ] 4+ new contract tests added
- [ ] **Gate 1**: build 0 errors (solution + Android)
- [ ] **Gate 2**: scoped suite green
- [ ] **Gate 4 simplify**: each concrete impl <200 LOC
- [ ] **Gate 4 security-review**: APK install path is security-relevant — verify SHA256 check happens before invoking Intent
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 4: implement `PlayStoreSource` against Play Console API when distribution is approved.
- BuildVariant flag in `VPNRouter.Android.csproj` to select between `SideloadSource` and `PlayStoreSource` at build time.
- F-Droid distribution may want a third source variant (`FDroidSource`) that checks the F-Droid repo.
