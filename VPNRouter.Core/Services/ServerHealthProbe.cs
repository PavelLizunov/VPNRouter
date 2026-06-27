using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// G1 (2026-06-27) — Smart Connect pre-flight. Probes subscription candidate
/// servers for liveness BEFORE bringing the tunnel up, so Connect lands on a
/// server that actually responds instead of a dead one (the Latvia-HY2 i/o
/// timeout that caused the "часто теряется" restart-storm in user diags).
///
/// <para>Reuses the mature, PROTOCOL-AWARE <see cref="TcpTlsProbe.ProbeServerAsync"/>
/// (VLESS+Reality → TCP-only, plain TLS → full handshake, Hy2/TUIC → UDP) so a
/// Reality server isn't false-flagged dead by a naive TLS probe. Runs the whole
/// pool in parallel within a short deadline — cheap enough for the connect path,
/// unlike <see cref="VlessDeepVerifier"/> which spins up a real sing-box per
/// server. A server that's reachable but whose proxy is broken is caught by the
/// post-connect AutoFailover layer (G4); the two layers compose like the
/// engine-vs-GUI split in Clash/sing-box/Hiddify.</para>
///
/// <para><see cref="PickBest"/> / <see cref="AliveRanked"/> are pure (no I/O) so
/// the exclude-dead + fastest-wins + none-alive selection is deterministically
/// testable; the network probe is injectable via the constructor.</para>
/// </summary>
public sealed class ServerHealthProbe
{
    private readonly ILogger? _logger;
    private readonly Func<VlessServerEntry, CancellationToken, Task<ServerProbeResult>> _probe;

    public ServerHealthProbe(
        ILogger? logger = null,
        Func<VlessServerEntry, CancellationToken, Task<ServerProbeResult>>? probeOverride = null)
    {
        _logger = logger;
        _probe = probeOverride ?? ((s, ct) => TcpTlsProbe.ProbeServerAsync(s, ct));
    }

    /// <summary>
    /// Probe every server in parallel, bounded by <paramref name="overallDeadline"/>.
    /// Servers that don't complete in time, error, or come back not-reachable are
    /// reported dead. Result order is undefined — callers use <see cref="PickBest"/>.
    /// </summary>
    public async Task<List<ServerLiveness>> ProbeAllAsync(
        IReadOnlyList<VlessServerEntry> servers,
        TimeSpan overallDeadline,
        CancellationToken ct = default)
    {
        if (servers == null || servers.Count == 0)
            return new List<ServerLiveness>();

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(overallDeadline);
        var token = deadlineCts.Token;

        var tasks = servers.Select(async s =>
        {
            try
            {
                var r = await _probe(s, token).ConfigureAwait(false);
                var alive = r.IsReachable;
                return new ServerLiveness(s, alive, alive ? r.LatencyMs : int.MaxValue);
            }
            catch (Exception ex)
            {
                _logger?.Debug("[ServerHealthProbe] {Name} ({Host}:{Port}) probe failed: {Err}",
                    s.Name, s.Server, s.Port, ex.Message);
                return new ServerLiveness(s, false, int.MaxValue);
            }
        });

        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
        _logger?.Information("[ServerHealthProbe] {Alive}/{Total} servers alive",
            results.Count(r => r.Alive), results.Count);
        return results;
    }

    /// <summary>
    /// Pure selector: the fastest ALIVE server, or null if none are alive.
    /// Dead servers are never returned — the v2rayN "delay = -1" lesson.
    /// </summary>
    public static VlessServerEntry? PickBest(IEnumerable<ServerLiveness> results)
        => results?
            .Where(r => r.Alive)
            .OrderBy(r => r.LatencyMs)
            .Select(r => r.Server)
            .FirstOrDefault();

    /// <summary>
    /// G1 connect decision: KEEP the currently-active server if it's alive
    /// (respect an explicit pick), otherwise the fastest LIVE server, otherwise
    /// null — all dead, so the caller surfaces an honest error instead of
    /// connecting blind to a dead server. Pure / unit-tested.
    /// </summary>
    public static VlessServerEntry? PickForConnect(IEnumerable<ServerLiveness> results, string? activeName)
    {
        var list = results?.ToList();
        if (list == null || list.Count == 0) return null;

        if (!string.IsNullOrEmpty(activeName))
        {
            var active = list.FirstOrDefault(r =>
                r.Alive && string.Equals(r.Server.Name, activeName, StringComparison.Ordinal));
            if (active != null) return active.Server; // explicit pick is alive — keep it
        }
        return PickBest(list); // else fastest live (or null if none alive)
    }

    /// <summary>The alive servers, fastest-first — for building a live-only
    /// urltest pool (G1: pool excludes dead nodes).</summary>
    public static List<VlessServerEntry> AliveRanked(IEnumerable<ServerLiveness> results)
        => results?
            .Where(r => r.Alive)
            .OrderBy(r => r.LatencyMs)
            .Select(r => r.Server)
            .ToList() ?? new List<VlessServerEntry>();
}

/// <summary>One server's liveness result. LatencyMs is int.MaxValue when dead so
/// it naturally sorts last. (Distinct from <see cref="ServerProbeResult"/>, which
/// is the raw protocol-probe outcome without the server pairing.)</summary>
public sealed record ServerLiveness(VlessServerEntry Server, bool Alive, int LatencyMs);
