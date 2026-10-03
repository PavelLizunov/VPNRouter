using System.Diagnostics;
using System.Net.Sockets;

namespace VPNRouter.Core.Services.FreeConfigs;

public sealed class FreeConfigTester
{
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(3);

    public int MaxConcurrency { get; set; } = 80;

    public bool RequireTlsHandshake { get; set; } = true;

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

    public async Task TestOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        cfg.LastTestedAt = DateTime.UtcNow;
        cfg.LastError = null;

        var sni = !string.IsNullOrWhiteSpace(cfg.Sni) ? cfg.Sni : cfg.Host;

        var result = await TcpTlsProbe.ProbeAsync(
            cfg.Host,
            cfg.Port,
            sni,
            requireTls: RequireTlsHandshake,
            ct: ct,
            tcpTimeout: TcpConnectTimeout,
            tlsTimeout: TlsHandshakeTimeout);

        cfg.LatencyMs = result.LatencyMs;
        switch (result.Status)
        {
            case ServerProbeStatus.Ok:
                cfg.Status = FreeConfigStatus.Ok;
                break;
            case ServerProbeStatus.Slow:
                cfg.Status = FreeConfigStatus.Slow;
                break;
            case ServerProbeStatus.Implausible:
                cfg.Status = FreeConfigStatus.Implausible;
                cfg.LastError = result.Error;
                break;
            case ServerProbeStatus.TlsFailed:
                cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = result.Error;
                break;
            case ServerProbeStatus.Timeout:
                cfg.Status = FreeConfigStatus.Timeout;
                cfg.LastError = result.Error ?? "tcp timeout";
                cfg.LatencyMs = 0;
                break;
            case ServerProbeStatus.Unreachable:
                cfg.Status = FreeConfigStatus.Unreachable;
                cfg.LastError = result.Error ?? "tcp unreachable";
                cfg.LatencyMs = 0;
                break;
            case ServerProbeStatus.SkippedNotApplicable:
                // A free config nobody could check (IPv6-only address) is not offered as a working one.
                cfg.Status = FreeConfigStatus.Unreachable;
                cfg.LastError = result.Error ?? "not probed";
                cfg.LatencyMs = 0;
                break;
            default:
                cfg.Status = FreeConfigStatus.Timeout;
                cfg.LastError = result.Error ?? "unknown";
                cfg.LatencyMs = 0;
                break;
        }
    }

    public async Task TcpPingOnlyAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        if (cfg == null) return;
        var (ok, latency, _) = await TcpTlsProbe.ProbeTcpAsync(
            cfg.Host, cfg.Port, TcpConnectTimeout, ct);
        if (ok && latency >= TcpTlsProbe.ImplausibleThresholdMs)
        {
            cfg.LatencyMs = latency;
        }
    }
}
