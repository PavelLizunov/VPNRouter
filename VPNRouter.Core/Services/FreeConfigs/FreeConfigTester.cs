using System.Diagnostics;
using System.Net.Sockets;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Tests free configs in two stages:
///   1. TCP connect + RTT measurement
///   2. TLS handshake to the SNI (validates the server actually responds as a TLS endpoint
///      presenting a valid cert for the expected SNI — real Reality servers proxy to real
///      SNIs like google.com/microsoft.com, so a valid TLS handshake with chain validation
///      strongly suggests the config is alive. Dead endpoints, honeypots, and local TUN
///      responders fail this stage.)
///
/// <para><b>v2.31.6-r15 (iter#6 dedup)</b>: this class is now a thin shim
/// around <see cref="TcpTlsProbe"/>. Pre-r15 it was a verbatim copy
/// (~200 LOC of TCP/TLS/cert-validation logic) — the iter#6 audit
/// caught the duplication. Now <see cref="TestOneAsync"/> delegates
/// to <see cref="TcpTlsProbe.ProbeAsync"/> with the bulk-test timeouts
/// (1.5 s TCP, 3 s TLS) and maps the immutable
/// <see cref="ServerProbeResult"/> back into the in-place mutation
/// pattern that <see cref="FreeConfigEntry"/> uses.</para>
/// </summary>
public sealed class FreeConfigTester
{
    // v2.28.6-r5: TCP connect timeout dropped 3s → 1.5s. Most live VLESS
    // servers respond in < 500 ms; 1.5 s still covers slow / overseas links.
    // Effect: dead entries (most of the pool) get killed twice as fast,
    // halving the "tested 500/500" wait before deep-verify starts.
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(3);  // v2.13.16: was 5s — most real servers handshake in <1s; 3s covers slow links

    /// <summary>
    /// v2.31.6-r15: low-cardinality wrapper around the
    /// <c>ConnectionRefused/HostUnreachable/HostNotFound</c> SocketException
    /// codes that TcpClient throws synchronously inside ConnectAsync — the
    /// rest of the codebase treats them as the same logical
    /// <see cref="FreeConfigStatus.Unreachable"/> bucket.
    /// </summary>

    // v2.13.16: bumped from 30 → 80. Ephemeral ports on Windows: 49152-65535 = ~16k,
    // TIME_WAIT 2 min → 80 concurrent × 2s/test × 120s = ~9600 ports in flight at peak. Safe.
    public int MaxConcurrency { get; set; } = 80;

    /// <summary>
    /// If true (default), require a valid TLS handshake (cert chain + SAN matching SNI)
    /// in addition to TCP for status=Ok. Configs that TCP-connect but fail TLS are marked TlsFailed.
    /// </summary>
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

    /// <summary>
    /// Test a single config:
    ///   1) TCP connect (2 attempts, take best RTT)
    ///   2) TLS handshake with cert validation (if RequireTlsHandshake)
    ///
    /// <para>v2.31.6-r15: delegates to <see cref="TcpTlsProbe.ProbeAsync"/>
    /// with FreeConfigTester's per-bulk-test timeouts. Mutates the entry
    /// in place per the existing FreeConfigStatus contract.</para>
    /// </summary>
    public async Task TestOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        cfg.LastTestedAt = DateTime.UtcNow;
        cfg.LastError = null;

        var sni = !string.IsNullOrWhiteSpace(cfg.Sni) ? cfg.Sni : cfg.Host;

        // v2.31.6-r15: single-source-of-truth call into TcpTlsProbe.
        // Pre-r15 this body was ~80 LOC of TCP/TLS/plausibility logic
        // duplicated from TcpTlsProbe.cs.
        var result = await TcpTlsProbe.ProbeAsync(
            cfg.Host,
            cfg.Port,
            sni,
            requireTls: RequireTlsHandshake,
            ct: ct,
            tcpTimeout: TcpConnectTimeout,
            tlsTimeout: TlsHandshakeTimeout);

        // Map immutable ServerProbeResult back to in-place mutation on
        // FreeConfigEntry. The two-status enum mapping is 1-to-1 by
        // semantic intent (the FreeConfigStatus enum predates
        // ServerProbeStatus by ~13 versions; consolidating them is a
        // bigger refactor — Phase 2 of the iter#6 plan).
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
            default:
                cfg.Status = FreeConfigStatus.Timeout;
                cfg.LastError = result.Error ?? "unknown";
                cfg.LatencyMs = 0;
                break;
        }
    }

    /// <summary>v2.28.6-r5: public TCP-only ping helper used by the
    /// Recheck commands. Updates <see cref="FreeConfigEntry.LatencyMs"/>
    /// with a fresh raw TCP RTT (the recheck flow then runs deep-verify
    /// for the proxy-alive gate; we keep TCP ping as the displayed value).
    /// Skips TLS validation — Recheck runs only on already-Verified entries
    /// that previously passed the full TCP+TLS gauntlet, so re-validating
    /// TLS is redundant and costs another second per entry.
    /// <para>v2.31.2-r1 (F-25 fix): apply the same plausibility gate as
    /// <see cref="TestOneAsync"/>. Without it Recheck on Saved Verified
    /// entries was overwriting <c>LatencyMs</c> with raw sub-5 ms readings —
    /// <c>TcpClient.ConnectAsync</c> returns in &lt;1 ms when the OS has cached
    /// the route + ARP entry from a previous Deep Verify (most Saved
    /// entries fit this profile), masking the real internet RTT and making
    /// every Saved row look like "1 ms" after a recheck. Drop implausible
    /// readings; keep the previous value (which already passed the gate
    /// during the original TestOneAsync run, so it's a true RTT).</para>
    /// <para>v2.31.6-r15: now delegates to
    /// <see cref="TcpTlsProbe.ProbeTcpAsync(string,int,TimeSpan,CancellationToken)"/>
    /// with the FreeConfigTester TCP timeout. Pre-r15 this had its own
    /// inline copy of the TCP-connect-with-cancellation pattern.</para>
    /// </summary>
    public async Task TcpPingOnlyAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        if (cfg == null) return;
        var (ok, latency, _) = await TcpTlsProbe.ProbeTcpAsync(
            cfg.Host, cfg.Port, TcpConnectTimeout, ct);
        if (ok && latency >= TcpTlsProbe.ImplausibleThresholdMs)
        {
            cfg.LatencyMs = latency;
        }
        // Don't mutate Status/LastError on failure — caller (Recheck flow)
        // needs the original Verified status preserved for retention.
        // Sub-threshold readings are dropped silently; the previous LatencyMs
        // (set by the TestOneAsync gate) stays as the displayed value.
    }
}
