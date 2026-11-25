using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed record BusinessServerProbe(
    VlessServerEntry Entry,
    DeepVerifyResult Result,
    int Order);

internal sealed record BusinessAutoSelectOutcome(
    IReadOnlyList<BusinessServerProbe> Probes,
    VlessServerEntry? Winner,
    int WinnerLatencyMs,
    bool BudgetExpired)
{
    internal bool HasWinner => Winner != null;
}

// Bounded fan-out over configured Business choices. Only an end-to-end probe
// that reached the fixed HTTPS validation endpoint may win; there is no TCP or
// direct fallback for an unverified candidate.
internal static class BusinessServerAutoSelection
{
    internal const int DefaultMaxConcurrency = 4;

    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    internal static async Task<BusinessAutoSelectOutcome> SelectFastestAsync(
        IReadOnlyList<VlessServerEntry> candidates,
        Func<VlessServerEntry, CancellationToken, Task<DeepVerifyResult>> verify,
        CancellationToken ct,
        int maxConcurrency = DefaultMaxConcurrency,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(verify);
        ct.ThrowIfCancellationRequested();

        if (candidates.Count == 0)
            return new BusinessAutoSelectOutcome([], null, 0, BudgetExpired: false);

        var probes = new BusinessServerProbe[candidates.Count];
        var concurrency = Math.Max(1, Math.Min(maxConcurrency, candidates.Count));
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timer = budget.HasValue && budget.Value > TimeSpan.Zero
            ? new Timer(static state =>
                {
                    try { ((CancellationTokenSource)state!).Cancel(); }
                    catch (ObjectDisposedException) { }
                }, run, budget.Value, Timeout.InfiniteTimeSpan)
            : null;

        var tasks = candidates.Select(async (entry, index) =>
        {
            try
            {
                await gate.WaitAsync(run.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                probes[index] = new(entry, DeepVerifyResult.Failed(
                    "cancelled", DeepVerifyFailurePhase.Cancelled), index);
                return;
            }

            try
            {
                run.Token.ThrowIfCancellationRequested();
                var result = await verify(entry, run.Token).ConfigureAwait(false);
                probes[index] = new(entry, result, index);
            }
            catch (OperationCanceledException)
            {
                probes[index] = new(entry, DeepVerifyResult.Failed(
                    "cancelled", DeepVerifyFailurePhase.Cancelled), index);
                if (ct.IsCancellationRequested) throw;
            }
            catch (Exception ex)
            {
                probes[index] = new(entry, DeepVerifyResult.Failed(ex.GetType().Name), index);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The shared budget expired; completed results remain usable.
        }

        ct.ThrowIfCancellationRequested();

        var ordered = probes.Where(probe => probe != null).OrderBy(probe => probe.Order).ToArray();
        var winner = ordered
            .Where(probe => probe.Result.Ok &&
                probe.Result.FailurePhase == DeepVerifyFailurePhase.None)
            .OrderBy(probe => probe.Result.HttpLatencyMs)
            .ThenBy(probe => probe.Order)
            .FirstOrDefault();

        return new BusinessAutoSelectOutcome(
            ordered,
            winner?.Entry,
            winner?.Result.HttpLatencyMs ?? 0,
            BudgetExpired: run.IsCancellationRequested && !ct.IsCancellationRequested);
    }

    internal static bool IsVerifiableCandidate(VlessServerEntry server)
    {
        if (string.Equals(server.Protocol, "dnstt", StringComparison.OrdinalIgnoreCase))
            return server.Dnstt != null && SingBoxFeatures.DnsttAvailable;
        if (server.IsDnsTunnel) return false;
        return ConfigGenerator.IsBusinessPortalTransportAvailable(server);
    }

    internal static ServerProbeStatus ToProbeStatus(DeepVerifyResult result)
    {
        if (result.Ok) return result.HttpLatencyMs > 800 ? ServerProbeStatus.Slow : ServerProbeStatus.Ok;

        return result.FailurePhase switch
        {
            DeepVerifyFailurePhase.Precondition => ServerProbeStatus.SkippedNotApplicable,
            DeepVerifyFailurePhase.UnsupportedByVerifier => ServerProbeStatus.SkippedNotApplicable,
            DeepVerifyFailurePhase.Cancelled => ServerProbeStatus.Unknown,
            DeepVerifyFailurePhase.Timeout => ServerProbeStatus.Timeout,
            DeepVerifyFailurePhase.LocalSpawn or DeepVerifyFailurePhase.SocksBind
                or DeepVerifyFailurePhase.ProxiedHttp => ServerProbeStatus.Unreachable,
            _ => ServerProbeStatus.Unreachable,
        };
    }

    internal static ServerProbeResult ToProbeResult(DeepVerifyResult result)
    {
        var status = ToProbeStatus(result);
        return new ServerProbeResult(status,
            status is ServerProbeStatus.Ok or ServerProbeStatus.Slow ? result.HttpLatencyMs : 0,
            status switch
            {
                ServerProbeStatus.SkippedNotApplicable => "unsupported by the end-to-end check",
                ServerProbeStatus.Unknown => null,
                _ => null,
            });
    }
}
