# Phase 2 — 2D-4: `ISingBoxApi` abstraction

**Owner**: Wave 6 parallel agent (4 of 4)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2D; plans/test-coverage-audit-2026-05-17.md §"Missing abstractions"
**Effort**: 1 day
**Risk**: MEDIUM (new public interface; isolates HealthMonitor hot-reload path)

## Why
Audit E: `HealthMonitor` hot-reload path calls `SingBoxManager` directly. SingBoxManager spawns the sing-box process AND talks to Clash API over HTTP. Two responsibilities + no mocking seam.

Extract `ISingBoxApi` interface that covers the Clash API surface (config reload, traffic stats, proxy switch, etc.). Concrete = `ClashSingBoxApi` (talks to 127.0.0.1:9090). Fake = `FakeSingBoxApi` (in-memory, returns canned states).

This unlocks tests for HealthMonitor's hot-reload + crash-recovery paths without spawning real sing-box.

## What

Create `VPNRouter.Core/Services/ISingBoxApi.cs`:

```csharp
namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over sing-box's Clash API surface. Lets HealthMonitor +
/// auto-failover code be tested without spawning a real sing-box process.
/// Concrete = ClashSingBoxApi (talks to 127.0.0.1:9090); fake =
/// FakeSingBoxApi (in-memory; canned states for testing).
/// </summary>
public interface ISingBoxApi
{
    /// <summary>
    /// PUT /configs — hot-reload the running sing-box config from disk
    /// without restarting the process. Returns true on success.
    /// </summary>
    Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default);

    /// <summary>
    /// GET /version — health-check ping. Returns version string or null.
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /connections — current active connections. Used by HealthMonitor
    /// to verify the tunnel is carrying traffic (not silently dead).
    /// </summary>
    Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// PUT /proxies/{group} — switch active proxy in a selector group.
    /// Used by auto-failover when the current proxy is unhealthy.
    /// </summary>
    Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default);

    /// <summary>
    /// GET /proxies — list available proxies + their delays.
    /// </summary>
    Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default);
}

public sealed record ConnectionsSnapshot(
    int ActiveCount,
    long TotalUploadBytes,
    long TotalDownloadBytes,
    DateTimeOffset CapturedAt);

public sealed record ProxyInfo(
    string Name,
    string Type,
    int? DelayMs,
    DateTimeOffset? DelayMeasuredAt);
```

Concrete `ClashSingBoxApi.cs`:
- Takes `IHttpClient` (Wave 6 sibling task) + base URL (default `http://127.0.0.1:9090`)
- Each method = thin HTTP call + JSON parse
- Robust error handling: 5xx → log + return null/false (caller decides retry)
- Timeout: short (3s for ReloadConfig, 1s for ping endpoints)

Fake `VPNRouter.Tests/Fakes/FakeSingBoxApi.cs`:
- In-memory state: bool tunnelHealthy, list of proxies, connection count
- `SetState(...)` + `SimulateCrash()` + `SimulateProxyDelay(name, ms)` helpers
- Records all calls for assertions

Refactor 1 service as POC: `HealthMonitor.cs` — switch from direct `_singBoxManager.ReloadConfigAsync` to `_api.ReloadConfigAsync`. Keep `_singBoxManager` for process lifecycle (start/stop/kill), but split out the API talking concern.

## How

**Step 1** — Write interface + record types.

**Step 2** — `ClashSingBoxApi`:
- Constructor takes `IHttpClient client, string baseUrl = "http://127.0.0.1:9090"`
- `ReloadConfigAsync`: PUT body `{"path":"<config>"}` to `/configs?force=true` (sing-box convention)
- `GetVersionAsync`: GET /version → parse `{version: "1.13.10"}` → return string
- `GetConnectionsAsync`: GET /connections → parse
- `SelectProxyAsync`: PUT /proxies/{group} body `{"name":"<n>"}`
- `ListProxiesAsync`: GET /proxies → parse map

**Step 3** — `FakeSingBoxApi`:
```csharp
public sealed class FakeSingBoxApi : ISingBoxApi
{
    public bool TunnelHealthy { get; set; } = true;
    public List<ProxyInfo> Proxies { get; } = new();
    public int ActiveConnectionCount { get; set; } = 0;
    public List<(DateTimeOffset At, string Method, string Detail)> Calls { get; } = new();
    
    public void SimulateCrash() => TunnelHealthy = false;
    
    public Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct)
    {
        Calls.Add((DateTimeOffset.UtcNow, "Reload", configPath));
        return Task.FromResult(TunnelHealthy);
    }
    // ... etc.
}
```

**Step 4** — Refactor `HealthMonitor.cs` to inject `ISingBoxApi`. Default to `new ClashSingBoxApi(new PolicyHttpClient())` for back-compat. The hot-reload path becomes:
```csharp
var ok = await _api.ReloadConfigAsync(configPath, ct);
if (!ok) { ...fallback to process restart... }
```

**Step 5** — Write 6 contract tests in `VPNRouter.Tests/ISingBoxApiContractTests.cs`:
- `ReloadConfigAsync_FakeReturnsTrue_HappyPath`
- `ReloadConfigAsync_FakeCrashed_ReturnsFalse`
- `GetVersionAsync_ParsesResponse`
- `SelectProxyAsync_RecordsCall`
- `ListProxiesAsync_ReturnsProxies`
- `Real ClashSingBoxApi against mock server: HappyPath` (use a tiny in-memory HTTP listener)

## Verification gate
- [ ] Interface ergonomic
- [ ] `ClashSingBoxApi` HTTP-talks correctly (mock server test)
- [ ] `FakeSingBoxApi` records calls + state-machines correctly
- [ ] HealthMonitor refactor compiles + existing HealthMonitorRecoveryGapTests + HealthMonitorTimerRaceTests pass
- [ ] 6 new contract tests pass
- [ ] **Gate 1**: build clean
- [ ] **Gate 2**: full suite stable
- [ ] **Gate 4 self-review**: `simplify` + `security-review` (Clash API is security-relevant — proxy selection affects routing)
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

**Follow-up**: Phase 2G writes auto-failover tests against FakeSingBoxApi (currently uncovered). Phase 3D F-A..F-E consolidation runs through this for proxy-switch on placeholder detection.
