#nullable enable
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory test fake for <see cref="ISingBoxApi"/> (Phase 2D-4,
/// 2026-05-17). Records every call for assertion + lets tests drive
/// canned state machines (healthy / crashed / proxy-switched).
///
/// <para>Per <c>plans/v3.0-execution-methodology.md</c> §5: "Don't use
/// Moq for fakes — write small inline impls per test class." This fake
/// is one half-step away from inline — shared across the contract-test
/// + future HealthMonitor + AutoFailover test suites — but kept tiny and
/// hand-rolled so it stays auditable.</para>
/// </summary>
public sealed class FakeSingBoxApi : ISingBoxApi
{
    // ── Tunable state (writable by tests) ──────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), all calls succeed and return cheerful
    /// responses. When <c>false</c>, ReloadConfigAsync + SelectProxyAsync
    /// return false; GetVersionAsync returns null; GetConnectionsAsync
    /// returns a zero-snapshot; ListProxiesAsync returns the configured
    /// list anyway (mirrors how a hung tunnel can still expose proxy
    /// metadata via cached state).
    /// </summary>
    public bool TunnelHealthy { get; set; } = true;

    /// <summary>Version string returned by <see cref="GetVersionAsync"/>
    /// when <see cref="TunnelHealthy"/> is true. Default mirrors the
    /// currently-bundled sing-box upstream.</summary>
    public string Version { get; set; } = "1.13.10";

    /// <summary>Mutable proxy list returned by
    /// <see cref="ListProxiesAsync"/>. Tests append to this directly.</summary>
    public List<ProxyInfo> Proxies { get; } = new();

    /// <summary>Number of active connections returned by
    /// <see cref="GetConnectionsAsync"/> when healthy.</summary>
    public int ActiveConnectionCount { get; set; }

    /// <summary>Upload bytes counter returned by
    /// <see cref="GetConnectionsAsync"/>.</summary>
    public long TotalUploadBytes { get; set; }

    /// <summary>Download bytes counter returned by
    /// <see cref="GetConnectionsAsync"/>.</summary>
    public long TotalDownloadBytes { get; set; }

    /// <summary>Currently-selected proxy per selector group. Mutated by
    /// successful <see cref="SelectProxyAsync"/> calls so tests can
    /// observe the state-machine transition.</summary>
    public Dictionary<string, string> SelectedByGroup { get; } = new(StringComparer.Ordinal);

    /// <summary>Recorded call log. Each tuple is
    /// (timestamp, method, detail) — detail is method-specific (config
    /// path for Reload; "{group}={name}" for SelectProxy; empty for the
    /// list/get endpoints). Tests can assert call ordering / count.</summary>
    public List<(DateTimeOffset At, string Method, string Detail)> Calls { get; } = new();

    /// <summary>If non-null, every method throws this instead of
    /// returning. Used to test caller-side error handling against an
    /// unexpected fault (e.g. the HttpClient itself blowing up). Reset
    /// to null to resume normal behaviour.</summary>
    public Exception? FaultToThrow { get; set; }

    /// <summary>
    /// Value returned by <see cref="GetProxyDelayAsync"/> — the live proxy
    /// reachability probe used by StrictDns failover. <c>null</c> = the proxy
    /// could not reach the test URL (unreachable). Default 42ms (reachable).
    /// When <see cref="TunnelHealthy"/> is false this is forced to null so a
    /// SimulateCrash() also makes the proxy probe report unreachable.
    /// </summary>
    public int? ProxyDelayMs { get; set; } = 42;

    // ── Helpers tests use to drive state ───────────────────────────────

    /// <summary>Mark the tunnel as crashed — subsequent calls fail. Mirrors
    /// the behaviour of a real sing-box that's been killed but whose
    /// process handle hasn't been cleaned up yet (Clash API stops
    /// responding).</summary>
    public void SimulateCrash() => TunnelHealthy = false;

    /// <summary>Restore the tunnel to healthy. Tests use this between
    /// failure-recovery assertions.</summary>
    public void SimulateRecovery() => TunnelHealthy = true;

    /// <summary>Stamp a synthetic delay on a known proxy. Mirrors
    /// sing-box's /proxies/{name}/delay endpoint having been called
    /// externally.</summary>
    public void SimulateProxyDelay(string name, int delayMs)
    {
        for (int i = 0; i < Proxies.Count; i++)
        {
            if (Proxies[i].Name == name)
            {
                Proxies[i] = Proxies[i] with
                {
                    DelayMs = delayMs,
                    DelayMeasuredAt = DateTimeOffset.UtcNow,
                };
                return;
            }
        }
    }

    // ── ISingBoxApi impl ───────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("Reload", configPath);
        if (FaultToThrow is not null) throw FaultToThrow;
        return Task.FromResult(TunnelHealthy);
    }

    /// <inheritdoc/>
    public Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetVersion", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;
        return Task.FromResult<string?>(TunnelHealthy ? Version : null);
    }

    /// <inheritdoc/>
    public Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetConnections", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;

        var snapshot = TunnelHealthy
            ? new ConnectionsSnapshot(ActiveConnectionCount, TotalUploadBytes, TotalDownloadBytes, DateTimeOffset.UtcNow)
            : new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc/>
    public Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("SelectProxy", $"{group}={name}");
        if (FaultToThrow is not null) throw FaultToThrow;

        if (!TunnelHealthy) return Task.FromResult(false);

        SelectedByGroup[group] = name;
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("ListProxies", string.Empty);
        if (FaultToThrow is not null) throw FaultToThrow;

        // Return a snapshot copy so test mutation doesn't reach the caller.
        IReadOnlyList<ProxyInfo> copy = Proxies.ToArray();
        return Task.FromResult(copy);
    }

    /// <inheritdoc/>
    public Task<int?> GetProxyDelayAsync(string proxyTag, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Record("GetProxyDelay", proxyTag);
        if (FaultToThrow is not null) throw FaultToThrow;

        // A crashed/unhealthy tunnel can't carry the probe → unreachable.
        var delay = TunnelHealthy ? ProxyDelayMs : null;
        return Task.FromResult(delay);
    }

    private void Record(string method, string detail)
    {
        Calls.Add((DateTimeOffset.UtcNow, method, detail));
    }
}
