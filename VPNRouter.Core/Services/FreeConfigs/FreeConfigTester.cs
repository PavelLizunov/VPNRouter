using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Tests a list of free configs by measuring TCP RTT to each endpoint.
/// Uses bounded concurrency (SemaphoreSlim) to avoid exhausting ephemeral ports or DNS resolvers.
/// </summary>
public sealed class FreeConfigTester
{
    /// <summary>TCP connect timeout per attempt.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Above this latency (ms) mark as "Slow" even if reachable.</summary>
    private const int SlowThresholdMs = 800;

    public int MaxConcurrency { get; set; } = 30;

    /// <summary>
    /// Tests all configs in parallel. Mutates entries in place (Status, LatencyMs, LastTestedAt).
    /// Returns when all tests complete or cancellation triggered.
    /// </summary>
    public async Task TestAllAsync(
        IReadOnlyCollection<FreeConfigEntry> configs,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var sem = new SemaphoreSlim(MaxConcurrency);
        var total = configs.Count;
        var done = 0;

        var tasks = configs.Select(async cfg =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await TestOneAsync(cfg, ct);
            }
            finally
            {
                sem.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report((n, total));
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Test a single config: 2 TCP connect attempts, take best latency.
    /// </summary>
    public async Task TestOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        cfg.LastTestedAt = DateTime.UtcNow;

        var latencies = new List<int>(capacity: 2);
        FreeConfigStatus lastError = FreeConfigStatus.Timeout;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (status, latency) = await TcpPingAsync(cfg.Host, cfg.Port, ct);
            if (status == FreeConfigStatus.Ok)
            {
                latencies.Add(latency);
            }
            else
            {
                lastError = status;
            }
        }

        if (latencies.Count > 0)
        {
            var best = latencies.Min();
            cfg.LatencyMs = best;
            cfg.Status = best > SlowThresholdMs ? FreeConfigStatus.Slow : FreeConfigStatus.Ok;
        }
        else
        {
            cfg.LatencyMs = 0;
            cfg.Status = lastError;
        }
    }

    /// <summary>
    /// Single TCP connect attempt with timeout. Returns (status, latency_ms).
    /// </summary>
    private static async Task<(FreeConfigStatus status, int latencyMs)> TcpPingAsync(
        string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
            };

#if NET6_0_OR_GREATER
            await client.ConnectAsync(host, port, cts.Token);
#else
            await client.ConnectAsync(host, port);
#endif
            sw.Stop();
            return (FreeConfigStatus.Ok, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (FreeConfigStatus.Timeout, 0);
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            return (FreeConfigStatus.Unreachable, 0);
        }
        catch
        {
            return (FreeConfigStatus.Timeout, 0);
        }
    }
}
