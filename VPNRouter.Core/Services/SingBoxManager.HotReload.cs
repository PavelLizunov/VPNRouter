using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    public void ReloadConfig(SingBoxConfig config, bool forceRestart = false) =>
        ReloadConfigJson(ConfigGenerator.Serialize(config), forceRestart);

    public bool TryReloadConfig(SingBoxConfig config) =>
        TryReloadConfigJson(ConfigGenerator.Serialize(config));

    /// <summary>
    /// Phase 2D-4 (2026-05-17): public seam for the
    /// <see cref="ISingBoxApi"/> hot-reload path. Writes
    /// <paramref name="configJson"/> to disk (rotating the current path)
    /// and returns the absolute path. The caller is then expected to
    /// invoke <see cref="ISingBoxApi.ReloadConfigAsync"/> with the
    /// returned path — splitting the "write JSON" concern from the
    /// "talk to Clash API" concern that pre-2D-4 lived together inside
    /// <see cref="TryReloadConfigJson"/>.
    ///
    /// <para>Used by <see cref="HealthMonitor"/>. <see cref="VpnEngine"/>
    /// still uses the thicker <see cref="TryReloadConfigJson"/> /
    /// <see cref="ReloadConfigJson"/> entry points because its
    /// callsites depend on the bundled write+reload+restart-fallback
    /// behaviour and aren't part of the 2D-4 POC scope.</para>
    /// </summary>
    /// <param name="configJson">Generated sing-box JSON.</param>
    /// <returns>Absolute path the JSON was written to (currently always
    /// <c>%ProgramData%\VPNRouter\config\current.json</c> — same path
    /// every existing reload path writes to).</returns>
    public string WriteConfigToDisk(string configJson)
    {
        _currentConfigPath = WriteJsonToDisk(configJson);
        return _currentConfigPath;
    }

    private bool TryHotReload()
    {
        // Pre-check: don't attempt an HTTP call to a dead sing-box. Without
        // this, a crash-recovery path that tries hot-reload first (because
        // a debounced process rescan landed between Crashed and our state
        // update) dumps a 20-line HttpRequestException stack into the log
        // — every single time. Checking HasExited gives us a fast, clean
        // "hot-reload unavailable, restarting" log line instead.
        if (_handle == null || _handle.HasExited)
        {
            _logger.Debug("[SingBoxManager] Hot-reload skipped — sing-box process not alive");
            return false;
        }

        try
        {
            // v2.31.0-r1 (CO-3 audit fix): the previous sync-over-async pattern
            // (`PutAsync(...).GetAwaiter().GetResult()` on a static HttpClient)
            // is mitigated by HttpClient.Timeout=3s, but on saturated
            // threadpools the awaiter's continuation could land on a starved
            // worker, extending the wait beyond Timeout. Solutions:
            //   1. Explicit CancellationToken with hard 3s deadline → enforces
            //      cancellation at .NET layer, not HttpClient internals.
            //   2. Future: convert to async signature and propagate awaits up
            //      to HealthMonitor.OnDebounceElapsed / AttemptRestart.
            // For now (1) is non-invasive and bounds the worst case explicitly.
            //
            // 3G-2 (v3.0 refactor): bumped from `_http.PutAsync(...)` to the
            // shared `IHttpClient.SendAsync(HttpRequest)` seam. Same URL, same
            // 3s deadline (now belt-and-braces — `HttpRequest.Timeout` + the
            // CancellationToken below both enforce it), same JSON body.
            var url = $"http://{_settings.ClashApi}/configs?force=true";
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Put, new Uri(url),
                Headers: ClashAuthHeaders(),
                Body: bodyBytes,
                BodyContentType: "application/json",
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();

            if (response.IsSuccess())
            {
                _logger.Information("[SingBoxManager] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    response.StatusCode);
                return true;
            }

            _logger.Warning("[SingBoxManager] Hot-reload HTTP {Code}: {Body}",
                response.StatusCode, response.AsString());
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[SingBoxManager] Hot-reload timed out after 3s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
        }
    }

    /// <summary>Check if sing-box Clash API responds (macOS: sing-box runs as root child of sudo).
    /// 3G-2 (v3.0 refactor): routed through the shared <see cref="IHttpClient"/>
    /// seam with an explicit 3 s deadline mirroring the legacy <c>HttpClient.Timeout</c>.
    /// P1 clash_api secret (2026-07-10): this is THE liveness authority behind
    /// <c>IsRunning</c> — an unauthenticated GET against a secret-carrying
    /// sing-box 401s and would read a healthy tunnel as dead (false demotes,
    /// failed health checks), so the bearer header here is load-bearing.</summary>
    private bool IsClashApiAlive()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Get, new Uri($"http://{_settings.ClashApi}/configs"),
                Headers: ClashAuthHeaders(),
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();
            return response.IsSuccess();
        }
        catch { return false; }
    }

    /// <summary>Authorization header for the Clash API, or null when the
    /// settings carry no secret (legacy open API — header omitted keeps the
    /// wire shape byte-identical to pre-P1).</summary>
    private Dictionary<string, string>? ClashAuthHeaders()
        => string.IsNullOrEmpty(_settings.ClashApiSecret)
            ? null
            : new Dictionary<string, string> { ["Authorization"] = $"Bearer {_settings.ClashApiSecret}" };

}
