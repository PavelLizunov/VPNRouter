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

/// <summary>
/// Bug #1 (v3.0 android-alpha r5+, 2026-05-11) — Android-side counterpart
/// of <see cref="FreeConfigDeepVerifier"/>. Same semantics, different
/// engine: instead of spawning <c>sing-box.exe</c> as a child process
/// (impossible on Android — there's no exec privilege from a normal app),
/// we call into <c>AndroidDeepVerifyBox.verifyConfigSync</c> in Java,
/// which uses the in-process <c>libbox.aar</c> already shipped for the
/// main VPN tunnel.
///
/// <para>The Java side handles libbox lifecycle (box creation, SOCKS
/// inbound bind, HTTP probe through SOCKS, box close). C# is responsible
/// for parsing the <c>vless://</c> URI, building the minimal sing-box
/// JSON via <see cref="FreeConfigDeepVerifier.BuildSingleOutboundConfig"/>,
/// picking a free SOCKS port, marshaling the call, and parsing the
/// returned result JSON.</para>
///
/// <para><b>Java bridge</b>: we don't write a manual JNI binding for the
/// helper class. Instead we use <see cref="Java.Lang.Class.ForName(string)"/>
/// + <c>getMethod()</c> + <c>invoke()</c>. Slower than a direct binding
/// (~200&#x202F;µs overhead per call) but cheap relative to the multi-second
/// verify itself, and saves a maintenance hazard — the Java surface here is
/// one static method.</para>
///
/// <para><b>Fallback</b>: if libbox throws / verify times out / the bridge
/// is unavailable, we leave the entry's Status untouched (stays Ok with
/// single&#x202F;✓). The orchestrator's deep-verify pass swallows our
/// exceptions and logs them — Bug #1's worst case is "we don't upgrade to
/// ✓✓", same UX as pre-fix.</para>
/// </summary>
internal sealed class AndroidFreeConfigDeepVerifier
{
    /// <summary>URL probed for verification — same as desktop.</summary>
    private const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    /// <summary>Overall per-config timeout, including libbox spin-up.</summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12);

    /// <summary>How many configs to verify in parallel.
    /// libbox's concurrent-box support is uncharted territory — we cap at 1
    /// for safety. The orchestrator will run verifications sequentially.</summary>
    public int MaxConcurrency { get; set; } = 1;

    private readonly ILogger _logger;
    private Java.Lang.Class? _verifierClass;
    private Java.Lang.Reflect.Method? _verifyMethod;
    private bool _bridgeProbed;

    public AndroidFreeConfigDeepVerifier(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verify a single config. Mutates the entry in place:
    /// <list type="bullet">
    ///   <item>Success → <see cref="FreeConfigStatus.Verified"/>,
    ///   <see cref="FreeConfigEntry.LastDeepVerifyAt"/> stamped.</item>
    ///   <item>Failure → Status unchanged (caller's TCP+TLS verdict stands),
    ///   <see cref="FreeConfigEntry.LastError"/> updated for diagnostics.</item>
    /// </list>
    ///
    /// <para>Never throws — the caller's loop should be safe to run
    /// unconditionally. If the bridge isn't available (test rig, missing
    /// libbox.aar, JNI failure), we log once and become a no-op.</para>
    /// </summary>
    public async Task VerifyOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        if (cfg is null) return;

        if (!EnsureBridgeLoaded())
        {
            // Bridge unavailable — never retry within this process.
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
            // We deliberately reuse the desktop verifier's config builder —
            // single source of truth for the minimal SOCKS+VLESS config the
            // verify pass needs. clashPort=null omits the experimental.clash_api
            // block (we don't hot-reload the verify box, and a hardcoded port
            // would collide with the main VPN's :9090 when both run).
            var vless = VlessUriParser.Parse(cfg.RawUri);
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

            // libbox's start() can block for ~1-2 s on cold call. Run on a
            // pool thread so we don't pin the orchestrator's caller (the
            // search loop on Avalonia's UI thread when invoked from the
            // tap handler — see AndroidApp.FreeConfigs.OnFreeConfigsFindClicked).
            string? resultJson = await Task.Run(() =>
            {
                try
                {
                    return InvokeJavaVerifySync(ctx, configJson, socksPort,
                        (int)OverallTimeout.TotalMilliseconds, ProbeUrl);
                }
                catch (Exception ex)
                {
                    // Surface via logcat too — the Serilog logger has no
                    // sink configured on Android today.
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

            // Parse {"ok":bool,"latencyMs":int,"err":"…"}.
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
                cfg.Status = FreeConfigStatus.Verified;
                cfg.LastDeepVerifyAt = DateTime.UtcNow;
                cfg.LastError = null;
                // Mirror desktop's policy: keep the TCP-ping latency value
                // for the badge (HTTP RTT through SOCKS includes 5+
                // round-trips and reads "slow" to the user). Only fall back
                // to HTTP latency if no TCP ping was recorded.
                if (cfg.LatencyMs <= 0 && latencyMs > 0)
                    cfg.LatencyMs = latencyMs;
                _logger.Information("[Android.DV] {host}:{port} [{cc}] ✓✓ VERIFIED in {ms}ms (total {total}ms)",
                    cfg.Host, cfg.Port, cc, latencyMs, sw.ElapsedMilliseconds);
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

    /// <summary>
    /// Resolve the static Java <c>AndroidDeepVerifyBox.verifyConfigSync</c>
    /// method once and cache the reflective handle. Called the first time
    /// <see cref="VerifyOneAsync"/> is invoked; if the lookup fails (libbox
    /// missing, Java class not in the APK), the verifier becomes a no-op
    /// and a warning is logged exactly once.
    /// </summary>
    private bool EnsureBridgeLoaded()
    {
        if (_verifyMethod is not null) return true;
        if (_bridgeProbed) return false; // failed once already

        _bridgeProbed = true;
        try
        {
            _verifierClass = Java.Lang.Class.ForName("com.ninitux.vpnrouter.AndroidDeepVerifyBox");
            if (_verifierClass is null) return false;

            // Java reflection: getMethod needs Java Class peers for the
            // parameter types, not C# Class.FromType(typeof(string)) — the
            // latter resolves to System.String's peer, which has a different
            // descriptor than java.lang.String and makes the method lookup
            // fail with NoSuchMethodException. Use Class.ForName for the
            // canonical Java class names instead.
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

        // null target → static method. Args are auto-boxed by the Mono.Android
        // reflection layer; int → Integer, string → java.lang.String.
        // The return value comes back as Java.Lang.String and we ToString it.
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

    /// <summary>Find a random free TCP port on loopback — same trick as the
    /// desktop verifier uses, just in C# rather than handing the port
    /// allocation to Java.</summary>
    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
