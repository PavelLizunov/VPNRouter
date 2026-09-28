using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Java.Lang;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using Exception = System.Exception;

namespace VPNRouter.Android;

internal sealed class AndroidFreeConfigDeepVerifier
{
    private const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    private const string SecondaryProbeUrl = "https://1.1.1.1/cdn-cgi/trace";

    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);

    public int MaxConcurrency { get; set; } = 1;

    private readonly ILogger _logger;
    private Java.Lang.Class? _verifierClass;
    private Java.Lang.Reflect.Method? _verifyMethod;
    private bool _bridgeProbed;

    public AndroidFreeConfigDeepVerifier(ILogger logger)
    {
        _logger = logger;
    }

    public async Task VerifyOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        if (cfg is null) return;

        if (!EnsureBridgeLoaded())
        {
            return;
        }

        var ctx = Application.Context;
        if (ctx is null)
        {
            _logger.Warning("[Android.DV] Application.Context null, skipping verify");
            return;
        }

        cfg.LastTestedAt = DateTime.UtcNow;
        var cc = cfg.CountryCode ?? "??";

        int socksPort;
        string configJson;
        try
        {
            socksPort = FindFreePort();
            var vless = ServerUriParser.Parse(cfg.RawUri);
            configJson = FreeConfigDeepVerifier.BuildSingleOutboundConfig(
                vless, socksPort, clashPort: null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.DV] {host}:{port} [{cc}] → config build failed",
                cfg.Host, cfg.Port, cc);
            cfg.LastError = "config build failed";
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(OverallTimeout);

            string? resultJson = await Task.Run(() =>
            {
                try
                {
                    return InvokeJavaVerifySync(ctx, configJson, socksPort,
                        (int)OverallTimeout.TotalMilliseconds, ProbeUrl);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("VpnRouter.DV",
                        $"Java invocation threw: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }, overallCts.Token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(resultJson))
            {
                cfg.LastError = "verify bridge unavailable";
                _logger.Information("[Android.DV] {host}:{port} [{cc}] ✗ bridge returned null",
                    cfg.Host, cfg.Port, cc);
                return;
            }

            JsonNode? root;
            try { root = JsonNode.Parse(resultJson); }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Android.DV] {host}:{port} bad result JSON: {json}",
                    cfg.Host, cfg.Port, resultJson);
                cfg.LastError = "verify result parse failed";
                return;
            }

            var ok = root?["ok"]?.GetValue<bool>() ?? false;
            var latencyMs = root?["latencyMs"]?.GetValue<int>() ?? 0;
            var err = root?["err"]?.GetValue<string?>();

            if (ok)
            {
                var sw2 = Stopwatch.StartNew();
                int socksPort2;
                string configJson2;
                bool secondOk = false;
                string? secondErr = null;
                try
                {
                    socksPort2 = FindFreePort();
                    var vless2 = ServerUriParser.Parse(cfg.RawUri);
                    configJson2 = FreeConfigDeepVerifier.BuildSingleOutboundConfig(
                        vless2, socksPort2, clashPort: null);
                    using var overallCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    overallCts2.CancelAfter(OverallTimeout);
                    var resultJson2 = await Task.Run(() =>
                    {
                        try
                        {
                            return InvokeJavaVerifySync(ctx, configJson2, socksPort2,
                                (int)OverallTimeout.TotalMilliseconds, SecondaryProbeUrl);
                        }
                        catch (Exception ex)
                        {
                            global::Android.Util.Log.Warn("VpnRouter.DV",
                                $"Java secondary invocation threw: {ex.GetType().Name}: {ex.Message}");
                            return null;
                        }
                    }, overallCts2.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(resultJson2))
                    {
                        var root2 = JsonNode.Parse(resultJson2);
                        secondOk = root2?["ok"]?.GetValue<bool>() ?? false;
                        secondErr = root2?["err"]?.GetValue<string?>();
                    }
                    else
                    {
                        secondErr = "bridge null on secondary";
                    }
                }
                catch (Exception ex2)
                {
                    secondErr = $"secondary threw: {ex2.GetType().Name}";
                }

                if (secondOk)
                {
                    cfg.Status = FreeConfigStatus.Verified;
                    cfg.LastDeepVerifyAt = DateTime.UtcNow;
                    cfg.LastError = null;
                    if (cfg.LatencyMs <= 0 && latencyMs > 0)
                        cfg.LatencyMs = latencyMs;
                    _logger.Information("[Android.DV] {host}:{port} [{cc}] ✓✓ VERIFIED (both probes ok, total {total}ms)",
                        cfg.Host, cfg.Port, cc, sw.ElapsedMilliseconds + sw2.ElapsedMilliseconds);
                }
                else
                {
                    cfg.LastError = $"primary ok, secondary {secondErr ?? "failed"}";
                    _logger.Information("[Android.DV] {host}:{port} [{cc}] ✗ false-positive: {err} (total {total}ms)",
                        cfg.Host, cfg.Port, cc, cfg.LastError, sw.ElapsedMilliseconds + sw2.ElapsedMilliseconds);
                }
            }
            else
            {
                cfg.LastError = string.IsNullOrEmpty(err) ? "deep verify failed" : err;
                _logger.Information("[Android.DV] {host}:{port} [{cc}] ✗ {err} (total {total}ms)",
                    cfg.Host, cfg.Port, cc, cfg.LastError, sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            cfg.LastError = "deep verify timeout";
            _logger.Information("[Android.DV] {host}:{port} [{cc}] → TIMEOUT after {ms}ms",
                cfg.Host, cfg.Port, cc, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Android.DV] {host}:{port} [{cc}] → THREW {type}",
                cfg.Host, cfg.Port, cc, ex.GetType().Name);
            cfg.LastError = ex.GetType().Name;
        }
    }

    private bool EnsureBridgeLoaded()
    {
        if (_verifyMethod is not null) return true;
        if (_bridgeProbed) return false;

        _bridgeProbed = true;
        try
        {
            _verifierClass = Java.Lang.Class.ForName("com.ninitux.vpnrouter.AndroidDeepVerifyBox");
            if (_verifierClass is null) return false;

            var stringClass = Java.Lang.Class.ForName("java.lang.String");
            var contextClass = Java.Lang.Class.ForName("android.content.Context");
            if (stringClass is null || contextClass is null) return false;

            _verifyMethod = _verifierClass.GetMethod(
                "verifyConfigSync",
                contextClass,
                stringClass,
                Java.Lang.Integer.Type!,
                Java.Lang.Integer.Type!,
                stringClass);
            return _verifyMethod is not null;
        }
        catch (Java.Lang.Throwable jex)
        {
            global::Android.Util.Log.Warn("VpnRouter.DV",
                $"bridge load Java threw: {jex.GetType().Name}: {jex.Message}");
            _verifierClass = null;
            _verifyMethod = null;
            return false;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.DV",
                $"bridge load .NET threw: {ex.GetType().Name}: {ex.Message}");
            _verifierClass = null;
            _verifyMethod = null;
            return false;
        }
    }

    private string? InvokeJavaVerifySync(
        Context ctx,
        string configJson,
        int socksPort,
        int timeoutMs,
        string probeUrl)
    {
        if (_verifyMethod is null) return null;

        var args = new Java.Lang.Object[]
        {
            ctx,
            new Java.Lang.String(configJson),
            Java.Lang.Integer.ValueOf(socksPort)!,
            Java.Lang.Integer.ValueOf(timeoutMs)!,
            new Java.Lang.String(probeUrl),
        };
        var result = _verifyMethod.Invoke(null, args);
        return result?.ToString();
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
