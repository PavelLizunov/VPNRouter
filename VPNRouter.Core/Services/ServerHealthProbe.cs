using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed class ServerHealthProbe
{
    internal const int MaxConcurrency = 8;

    private readonly ILogger? _logger;
    private readonly Func<VlessServerEntry, CancellationToken, Task<ServerProbeResult>> _probe;

    public ServerHealthProbe(
        ILogger? logger = null,
        Func<VlessServerEntry, CancellationToken, Task<ServerProbeResult>>? probeOverride = null)
    {
        _logger = logger;
        _probe = probeOverride ?? ((s, ct) => TcpTlsProbe.ProbeServerAsync(s, ct));
    }

    public async Task<List<ServerLiveness>> ProbeAllAsync(
        IReadOnlyList<VlessServerEntry> servers,
        TimeSpan overallDeadline,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (servers == null || servers.Count == 0)
            return new List<ServerLiveness>();

        int count = servers.Count;
        var results = new ServerLiveness[count];
        for (int i = 0; i < count; i++)
        {
            results[i] = new ServerLiveness(servers[i], Alive: false, LatencyMs: int.MaxValue);
        }

        if (overallDeadline == TimeSpan.Zero)
            return results.ToList();

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(overallDeadline);
        var token = deadlineCts.Token;

        int nextIndex = 0;

        async Task WorkerAsync()
        {
            while (!token.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref nextIndex) - 1;
                if (index >= count)
                    break;

                if (token.IsCancellationRequested)
                    break;

                var s = servers[index];
                try
                {
                    var r = await _probe(s, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var alive = r?.IsReachable == true;
                    results[index] = new ServerLiveness(s, alive, alive ? r!.LatencyMs : int.MaxValue);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _logger?.Debug("[ServerHealthProbe] {Name} ({Host}:{Port}) probe deadline expired",
                        s?.Name, s?.Server, s?.Port);
                    results[index] = new ServerLiveness(s!, false, int.MaxValue);
                }
                catch (Exception ex)
                {
                    _logger?.Debug("[ServerHealthProbe] {Name} ({Host}:{Port}) probe failed: {Err}",
                        s?.Name, s?.Server, s?.Port, ex.Message);
                    results[index] = new ServerLiveness(s!, false, int.MaxValue);
                }
            }
        }

        int workerCount = Math.Min(count, MaxConcurrency);
        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = WorkerAsync();
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        _logger?.Information("[ServerHealthProbe] {Alive}/{Total} servers alive",
            results.Count(r => r.Alive), count);
        return results.ToList();
    }

    public static VlessServerEntry? PickBest(IEnumerable<ServerLiveness> results)
        => results?
            .Where(r => r.Alive)
            .OrderBy(r => r.LatencyMs)
            .Select(r => r.Server)
            .FirstOrDefault();

    public static VlessServerEntry? PickForConnect(IEnumerable<ServerLiveness> results, string? activeName)
    {
        var list = results?.ToList();
        if (list == null || list.Count == 0) return null;

        if (!string.IsNullOrEmpty(activeName))
        {
            var active = list.FirstOrDefault(r =>
                r.Alive && string.Equals(r.Server.Name, activeName, StringComparison.Ordinal));
            if (active != null) return active.Server;
        }
        return PickBest(list);
    }
}

public sealed record ServerLiveness(VlessServerEntry Server, bool Alive, int LatencyMs);
