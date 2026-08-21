// Phase 4 (Wave 18, 2026-05-18) — test double for VPNRouter.Core.Services
// .UpdateSources.IUpdateSource. Lets test classes script CheckAsync /
// DownloadAsync / ApplyAsync outcomes without driving the GitHub-side
// IHttpClient + adapter chain.
//
// Used by the migrated callers' tests:
//   • UpdateNotificationViewModelTests — UI toast trigger on non-null result.
//   • TestUpdateCommandTests — CLI exit-code mapping on info/null/throw.
//   • IUpdateSourceContractTests (existing) — would also work here, but it
//     drives the concrete sources to pin their contract; this fake is for
//     downstream callers that only care that "a source returned info".
//
// Brief: plans/phase4-iupdatesource-callers-2026-05-18.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory test double for <see cref="IUpdateSource"/>. Scripted via
/// init-only properties; inspect call counters + captured arguments after
/// the SUT runs.
/// </summary>
/// <remarks>
/// <para>
/// Default behaviour is "no update available, never throw". Scripts:
/// </para>
/// <list type="bullet">
///   <item><see cref="CheckResult"/> — set non-null to simulate an update,
///   null (default) for up-to-date.</item>
///   <item><see cref="CheckException"/> — set to bubble an exception out of
///   <see cref="CheckAsync"/> so caller's try/catch path is exercised.</item>
///   <item><see cref="DownloadReturnPath"/> — string returned from
///   <see cref="DownloadAsync"/>. Defaults to a non-empty placeholder path so
///   <see cref="ApplyAsync"/> can be chained without further setup.</item>
///   <item><see cref="DownloadException"/> — set to bubble an exception out
///   of <see cref="DownloadAsync"/>.</item>
///   <item><see cref="DownloadProgressEmits"/> — sequence of
///   <see cref="DownloadProgress"/> events reported to the caller's
///   <see cref="IProgress{T}"/> sink before <see cref="DownloadAsync"/>
///   returns. Empty by default.</item>
///   <item><see cref="ApplyReturnValue"/> — bool returned from
///   <see cref="ApplyAsync"/>; default <c>true</c>.</item>
///   <item><see cref="SourceId"/> — short id reported by the SUT; useful
///   when the caller logs the active source. Default "fake".</item>
/// </list>
/// </remarks>
public sealed class FakeUpdateSource : IUpdateSource
{
    /// <inheritdoc />
    public string SourceId { get; init; } = "fake";

    // ── Scripted return values ──────────────────────────────────────────

    /// <summary>Snapshot returned from <see cref="CheckAsync"/>. Null
    /// means "up to date" — the default.</summary>
    public UpdateSourceInfo? CheckResult { get; init; }

    /// <summary>Override to throw from <see cref="CheckAsync"/>; null =
    /// no throw.</summary>
    public Exception? CheckException { get; init; }

    public IReadOnlyList<UpdateSourceInfo> StableReleases { get; init; } =
        Array.Empty<UpdateSourceInfo>();

    public Exception? ListStableException { get; init; }

    /// <summary>String returned from <see cref="DownloadAsync"/>. Set to
    /// the staged-payload path the SUT expects.</summary>
    public string DownloadReturnPath { get; init; } = Path.Combine(Path.GetTempPath(), "fake-update-staging");

    /// <summary>Override to throw from <see cref="DownloadAsync"/>; null
    /// = no throw.</summary>
    public Exception? DownloadException { get; init; }

    public Func<UpdateSourceInfo, Task<string>>? DownloadHandler { get; init; }

    /// <summary>Progress samples emitted (in order) to the caller's
    /// <see cref="IProgress{T}"/> sink during
    /// <see cref="DownloadAsync"/>.</summary>
    public DownloadProgress[] DownloadProgressEmits { get; init; } = Array.Empty<DownloadProgress>();

    /// <summary>Bool returned from <see cref="ApplyAsync"/>. Default
    /// <c>true</c> (the caller normally short-circuits to "Restarting…" on
    /// true).</summary>
    public bool ApplyReturnValue { get; init; } = true;

    /// <summary>Override to throw from <see cref="ApplyAsync"/>; null =
    /// no throw.</summary>
    public Exception? ApplyException { get; init; }

    // ── Call recording ──────────────────────────────────────────────────

    /// <summary>Number of times <see cref="CheckAsync"/> was called.</summary>
    public int CheckCallCount { get; private set; }

    public int ListStableCallCount { get; private set; }

    /// <summary>Number of times <see cref="DownloadAsync"/> was called.</summary>
    public int DownloadCallCount { get; private set; }

    /// <summary>Number of times <see cref="ApplyAsync"/> was called.</summary>
    public int ApplyCallCount { get; private set; }

    /// <summary>Captured info argument of the most recent
    /// <see cref="DownloadAsync"/> call.</summary>
    public UpdateSourceInfo? LastDownloadInfo { get; private set; }

    /// <summary>Captured info argument of the most recent
    /// <see cref="ApplyAsync"/> call.</summary>
    public UpdateSourceInfo? LastApplyInfo { get; private set; }

    /// <summary>Captured stagedPath argument of the most recent
    /// <see cref="ApplyAsync"/> call.</summary>
    public string? LastApplyStagedPath { get; private set; }

    // ── IUpdateSource ───────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        CheckCallCount++;
        if (CheckException is not null)
            return Task.FromException<UpdateSourceInfo?>(CheckException);
        return Task.FromResult(CheckResult);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UpdateSourceInfo>> ListStableAsync(
        int maxCount,
        CancellationToken ct = default)
    {
        ListStableCallCount++;
        if (ListStableException is not null)
            return Task.FromException<IReadOnlyList<UpdateSourceInfo>>(ListStableException);
        return Task.FromResult(StableReleases);
    }

    /// <inheritdoc />
    public Task<string> DownloadAsync(
        UpdateSourceInfo info,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        DownloadCallCount++;
        LastDownloadInfo = info;
        if (DownloadHandler is not null)
            return DownloadHandler(info);
        if (DownloadException is not null)
            return Task.FromException<string>(DownloadException);

        if (progress != null)
            foreach (var sample in DownloadProgressEmits)
                progress.Report(sample);

        return Task.FromResult(DownloadReturnPath);
    }

    /// <inheritdoc />
    public Task<bool> ApplyAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct = default)
    {
        ApplyCallCount++;
        LastApplyInfo = info;
        LastApplyStagedPath = stagedPath;
        if (ApplyException is not null)
            return Task.FromException<bool>(ApplyException);
        return Task.FromResult(ApplyReturnValue);
    }
}
