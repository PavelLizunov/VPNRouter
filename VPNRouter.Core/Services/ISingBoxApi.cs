#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over sing-box's Clash API surface (Phase 2D-4, 2026-05-17).
/// Lets <see cref="HealthMonitor"/> + auto-failover code be tested without
/// spawning a real sing-box process. The concrete implementation
/// (<see cref="ClashSingBoxApi"/>) talks HTTP to <c>127.0.0.1:9090</c>;
/// the test fake (<c>VPNRouter.Tests.Fakes.FakeSingBoxApi</c>) returns
/// canned in-memory state and records all calls.
///
/// <para><strong>Why split?</strong> Pre-2D-4, <see cref="SingBoxManager"/>
/// owned BOTH the process lifecycle (start/stop/kill/orphan-cleanup) AND
/// the Clash API talking (PUT /configs, GET /version, etc.). Two
/// responsibilities; no mocking seam. Splitting the API talking concern
/// unlocks unit tests for crash-recovery + auto-failover paths.
/// <see cref="SingBoxManager"/> still owns the process side; this
/// interface owns the HTTP side.</para>
///
/// <para><strong>Contract for implementors.</strong> All methods:</para>
/// <list type="bullet">
///   <item>Robust to a dead/missing sing-box — return null / false / empty
///   instead of throwing. The caller decides retry policy.</item>
///   <item>Short timeouts: 3s for <see cref="ReloadConfigAsync"/>
///   (config write may stall briefly on slow disks); 1s for
///   ping/list endpoints (snappier failover decisions).</item>
///   <item>Idempotent. Calling <see cref="ReloadConfigAsync"/> twice
///   with the same path must produce the same effect as one call.</item>
///   <item>Honour <see cref="CancellationToken"/> at every await — the
///   HealthMonitor cancels in-flight calls during teardown.</item>
/// </list>
///
/// <para><strong>Security note (per security-review).</strong> The Clash
/// API listens on the loopback only (<c>127.0.0.1:9090</c>) by convention.
/// Implementations MUST refuse to construct themselves with a non-loopback
/// base URL (see <see cref="ClashSingBoxApi"/>'s ctor guard). Allowing
/// remote Clash control would let a misconfigured / hostile network
/// re-aim the user's tunnel via <see cref="SelectProxyAsync"/>.</para>
/// </summary>
public interface ISingBoxApi
{
    /// <summary>
    /// PUT <c>/configs?force=true</c> — hot-reload the running sing-box
    /// config from a path on disk without restarting the process. Used by
    /// <see cref="HealthMonitor"/>'s debounced-rescan + crash-recovery
    /// paths to avoid TUN-adapter teardown.
    /// </summary>
    /// <param name="configPath">Absolute path to the sing-box JSON config
    /// on disk. Must be readable by the sing-box process (which may be
    /// running as root on Linux/macOS — same filesystem still works).</param>
    /// <param name="ct">Cancellation token. The implementation also enforces
    /// an internal hard deadline (3s) so a hung Clash API can't block
    /// caller threads indefinitely.</param>
    /// <returns><c>true</c> on HTTP 2xx; <c>false</c> on any non-success
    /// status, timeout, network error, or non-running sing-box.</returns>
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);

    /// <summary>
    /// GET <c>/version</c> — health-check ping. Used as a liveness probe
    /// for the Clash API specifically (the sing-box process itself can be
    /// alive but the Clash API hung — this distinguishes those).
    /// </summary>
    /// <returns>Version string from sing-box (e.g. <c>"1.13.10"</c>) on
    /// success; <c>null</c> on any failure (timeout, network error, dead
    /// process, non-success status, parse error).</returns>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// GET <c>/connections</c> — current active connections snapshot.
    /// Used by <see cref="HealthMonitor"/> to verify the tunnel is
    /// actually carrying traffic (not silently dead — sing-box up, Clash
    /// API alive, but TUN routes broken so user traffic isn't actually
    /// using the tunnel).
    /// </summary>
    /// <returns>A snapshot with the current active count and aggregate
    /// upload/download counters. Returns a snapshot with <c>ActiveCount=0</c>
    /// and zeroed counters on any failure (caller distinguishes via
    /// <see cref="ConnectionsSnapshot.CapturedAt"/> staleness if needed).</returns>
    Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// PUT <c>/proxies/{group}</c> — switch active proxy in a selector
    /// group. Used by auto-failover (Phase 2G, follow-up) when the current
    /// proxy is unhealthy.
    /// </summary>
    /// <param name="group">Selector group name (e.g. <c>"select"</c> in
    /// the generated config).</param>
    /// <param name="name">Target proxy name within the group. Must exist
    /// in the group's <c>proxies</c> list — implementations don't validate
    /// this; sing-box returns 4xx if invalid.</param>
    /// <returns><c>true</c> on HTTP 2xx; <c>false</c> on any failure.</returns>
    Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default);

    /// <summary>
    /// GET <c>/proxies</c> — list all available proxies + their last-known
    /// delay measurements. Used by auto-failover decision logic and the
    /// UI proxy-picker.
    /// </summary>
    /// <returns>List of <see cref="ProxyInfo"/> records. Empty list on any
    /// failure (never null — keeps caller LINQ ergonomic).</returns>
    Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default);
}

/// <summary>
/// Snapshot of sing-box's <c>/connections</c> endpoint at a point in time.
/// Returned by <see cref="ISingBoxApi.GetConnectionsAsync"/>.
/// </summary>
/// <param name="ActiveCount">Number of active connections at capture time.</param>
/// <param name="TotalUploadBytes">Aggregate upload bytes across all
/// active connections.</param>
/// <param name="TotalDownloadBytes">Aggregate download bytes across all
/// active connections.</param>
/// <param name="CapturedAt">When this snapshot was captured. Useful for
/// callers to detect stale data (e.g. if the call failed and the
/// implementation returned a zeroed snapshot, the timestamp shows when
/// that decision was made).</param>
public sealed record ConnectionsSnapshot(
    int ActiveCount,
    long TotalUploadBytes,
    long TotalDownloadBytes,
    DateTimeOffset CapturedAt);

/// <summary>
/// Proxy metadata returned by <see cref="ISingBoxApi.ListProxiesAsync"/>.
/// Maps to sing-box's <c>/proxies</c> response shape (one entry per
/// outbound).
/// </summary>
/// <param name="Name">Proxy/outbound name as registered in the
/// sing-box config (e.g. <c>"proxy"</c>, <c>"direct"</c>, or a custom
/// tag).</param>
/// <param name="Type">Protocol type (<c>"vless"</c>, <c>"direct"</c>,
/// <c>"selector"</c>, <c>"urltest"</c>, etc.).</param>
/// <param name="DelayMs">Last-measured delay in milliseconds, or
/// <c>null</c> if the proxy hasn't been probed.</param>
/// <param name="DelayMeasuredAt">Timestamp of the last delay
/// measurement, or <c>null</c> if never probed.</param>
public sealed record ProxyInfo(
    string Name,
    string Type,
    int? DelayMs,
    DateTimeOffset? DelayMeasuredAt);
