using System.Net.Http;
using Newtonsoft.Json.Linq;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// F-E (2026-05-11) — runtime safety net for "dead config" launches.
///
/// <para>Catches the stas-class issue (see <c>plans/r10-stas-confirmed-and-apps-2mode.md</c>
/// §1.5) where the user's <c>vless.active_server</c> persists a placeholder
/// from an earlier Android-port smoke build (PlaceholderVlessUri constants)
/// AND a working subscription is also configured. The
/// <see cref="VlessServersResolver"/> picks up the placeholder before the
/// subscription, and sing-box starts cleanly with an outbound that
/// connects to 195.135.255.216 — a dead/hostile host with the same Reality
/// public_key as Android's placeholder. F-A/B/D prevent the bad state
/// from being created; F-E catches a user who's already in it.</para>
///
/// <para>Two phases:</para>
/// <list type="number">
///   <item><see cref="CheckBeforeStart"/> — static, runs BEFORE sing-box
///   launches. Pattern-matches the proxy outbound against a known
///   placeholder list (Reality public_key, short_id, server IP).</item>
///   <item><see cref="ProbeAsync"/> — runtime, runs AFTER sing-box
///   launches. Queries Clash API
///   <c>/proxies/proxy/delay?url=...gstatic.com/generate_204</c> twice;
///   if both attempts fail the outbound is considered dead.</item>
/// </list>
///
/// <para>Stateless / singleton-safe. The orchestration around it
/// (server cycling, restart, user-facing alert) lives in
/// <see cref="AutoFailoverEngine"/>.</para>
/// </summary>
public sealed class ConfigSanityCheck
{
    // ─── Known placeholder fingerprints ───────────────────────────────────
    //
    // These are pulled from VPNRouter.Android's PlaceholderVlessUri smoke-
    // test constant (removed in DEFCT-005 but still present in pre-r10
    // user configs that hot-flipped to "vless mode" via the legacy
    // ConfigMode=generated path). stas's evidence is the canonical case:
    //   plans/stas-evidence-config.yaml   (active_server: khunrath_ln)
    //   plans/stas-evidence-current.json  (outbound: 195.135.255.216 +
    //                                      pubkey DnT9... + sid 78ca7952)
    //
    // The lists are intentionally narrow — false-positive bans are worse
    // than false negatives here, because a banned valid server kills VPN
    // for the user. Add only fingerprints we've confirmed are placeholder
    // bait. Cross-referenced with Android constants — see r10 plan §1.5
    // step 5 "Unify placeholder lists".

    public static readonly IReadOnlySet<string> KnownPlaceholderPubkeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU",
        };

    public static readonly IReadOnlySet<string> KnownPlaceholderShortIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "78ca7952",
        };

    public static readonly IReadOnlySet<string> KnownPlaceholderServers =
        new HashSet<string>
        {
            "195.135.255.216",
        };

    private readonly ILogger? _logger;
    private readonly HttpClient _http;

    public ConfigSanityCheck(ILogger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        // HttpClient.Timeout > Clash API timeout (5000ms) so the .NET layer
        // doesn't pre-empt the Clash response. Caller can inject a mock
        // HttpClient for tests.
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>
    /// Phase 1 — static analysis of the about-to-launch sing-box config.
    /// Looks for placeholder fingerprints in the first VLESS-like outbound.
    /// Returns <c>IsDead=true</c> if a known placeholder is matched OR if
    /// the outbound is structurally malformed (missing server / port / uuid).
    /// </summary>
    public PreStartCheckResult CheckBeforeStart(JObject singboxConfig)
    {
        if (singboxConfig == null)
            return new PreStartCheckResult(true, "sing-box config is null", null);

        var outbounds = singboxConfig["outbounds"] as JArray;
        if (outbounds == null || outbounds.Count == 0)
            return new PreStartCheckResult(true, "sing-box config has no outbounds", "outbounds");

        // Walk every outbound that looks like a proxy (vless / hysteria2 /
        // tuic / shadowsocks / trojan). We treat the FIRST proxy-typed
        // outbound as the active one — same heuristic CustomConfigInjector
        // uses to identify the proxy tag.
        var proxy = FindFirstProxyOutbound(outbounds);

        if (proxy == null)
        {
            // No proxy outbound at all — this is fatal for generated mode
            // (the route rules would point at a non-existent tag). Same
            // class of bug LeakProtection catches separately.
            return new PreStartCheckResult(true,
                "no proxy outbound found (vless/hysteria2/tuic/shadowsocks/trojan)",
                "outbounds");
        }

        // ── Structural checks ──
        var server = proxy["server"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(server))
            return new PreStartCheckResult(true,
                "proxy outbound has empty 'server' field — config never reachable",
                "outbound.server");

        var serverPort = proxy["server_port"]?.Value<int?>() ?? 0;
        if (serverPort <= 0)
            return new PreStartCheckResult(true,
                "proxy outbound has invalid 'server_port' (must be 1-65535)",
                "outbound.server_port");

        // VLESS-specific: uuid is mandatory. Other protocols use password
        // (Hysteria2 / TUIC / Shadowsocks / Trojan) so the field is
        // protocol-conditional.
        var proxyType = proxy["type"]?.Value<string>()?.ToLowerInvariant() ?? "";
        if (proxyType == "vless")
        {
            var uuid = proxy["uuid"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(uuid))
                return new PreStartCheckResult(true,
                    "VLESS proxy outbound has empty 'uuid' — handshake would fail",
                    "outbound.uuid");
        }

        // ── Placeholder fingerprint match (delegated to PlaceholderGuard) ──
        // v2.32.3-r1 (2026-05-17): inspection logic moved to InspectOutbound
        // so CustomConfigInjector and any future caller share the same
        // single-source-of-truth check. Kept the per-field log lines here
        // because they include the actual value (more useful in production
        // logs than the typed exception).
        var offendingField = InspectOutbound(proxy);
        if (offendingField != null)
        {
            var reality = proxy["tls"]?["reality"] as JObject;
            switch (offendingField)
            {
                case "reality.public_key":
                {
                    var pubkey = reality?["public_key"]?.Value<string>();
                    _logger?.Warning(
                        "[ConfigSanityCheck] Placeholder Reality public_key detected: {Key}",
                        pubkey);
                    return new PreStartCheckResult(true,
                        $"Reality public_key matches known placeholder ({pubkey})",
                        "outbound.tls.reality.public_key");
                }
                case "reality.short_id":
                {
                    var shortId = reality?["short_id"]?.Value<string>();
                    _logger?.Warning(
                        "[ConfigSanityCheck] Placeholder Reality short_id detected: {ShortId}",
                        shortId);
                    return new PreStartCheckResult(true,
                        $"Reality short_id matches known placeholder ({shortId})",
                        "outbound.tls.reality.short_id");
                }
                case "server":
                {
                    _logger?.Warning(
                        "[ConfigSanityCheck] Placeholder server IP detected: {Server}",
                        server);
                    return new PreStartCheckResult(true,
                        $"Proxy server IP matches known placeholder ({server})",
                        "outbound.server");
                }
            }
        }

        // All gates passed.
        return new PreStartCheckResult(false, null, null);
    }

    /// <summary>
    /// Locates the first proxy-typed outbound in a sing-box <c>outbounds</c>
    /// array (vless / hysteria2 / tuic / shadowsocks / trojan). Returns
    /// <c>null</c> when none is present. Shared between
    /// <see cref="CheckBeforeStart(JObject)"/> and
    /// <see cref="CustomConfigInjector"/>'s placeholder gate so both layers
    /// pick the same outbound to inspect.
    /// </summary>
    internal static JObject? FindFirstProxyOutbound(JArray outbounds)
    {
        foreach (var ob in outbounds.OfType<JObject>())
        {
            var type = ob["type"]?.Value<string>()?.ToLowerInvariant() ?? "";
            if (type is "vless" or "hysteria2" or "tuic" or "shadowsocks" or "trojan")
                return ob;
        }
        return null;
    }

    /// <summary>
    /// Inspects a single sing-box proxy outbound JObject for placeholder
    /// fingerprints (Reality public_key, Reality short_id, server IP).
    /// Returns the matching field name (<c>"reality.public_key"</c>,
    /// <c>"reality.short_id"</c>, <c>"server"</c>) or <c>null</c> when the
    /// outbound is clean. Matches the field-name convention used by
    /// <see cref="PlaceholderGuard.Inspect(string?, string?, string?)"/>.
    ///
    /// <para>v2.32.3-r1 (2026-05-17): extracted from
    /// <see cref="CheckBeforeStart(JObject)"/> so the custom-config injector
    /// can reject placeholder credentials at Inject time using the same
    /// detection logic as the runtime safety net.</para>
    /// </summary>
    public static string? InspectOutbound(JObject? proxy)
    {
        if (proxy == null) return null;

        var reality = proxy["tls"]?["reality"] as JObject;
        var pubkey = reality?["public_key"]?.Value<string>();
        var shortId = reality?["short_id"]?.Value<string>();
        var server = proxy["server"]?.Value<string>();

        return PlaceholderGuard.Inspect(pubkey, shortId, server);
    }

    /// <summary>
    /// Phase 1 — convenience overload that accepts the raw JSON text.
    /// Catches parse errors as a fatal config-dead reason rather than
    /// propagating an exception out of the safety net.
    /// </summary>
    public PreStartCheckResult CheckBeforeStart(string singboxConfigJson)
    {
        if (string.IsNullOrWhiteSpace(singboxConfigJson))
            return new PreStartCheckResult(true, "sing-box config JSON is empty", null);

        try
        {
            var jo = JObject.Parse(singboxConfigJson);
            return CheckBeforeStart(jo);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[ConfigSanityCheck] Failed to parse sing-box JSON");
            return new PreStartCheckResult(true,
                $"sing-box config JSON is not parseable: {ex.Message}",
                null);
        }
    }

    /// <summary>
    /// Phase 2 — live probe against the running sing-box via the Clash API.
    /// Uses <c>/proxies/proxy/delay?url=...&amp;timeout=5000</c>, the same
    /// endpoint Clash dashboards use. Two attempts with 3s spacing — one
    /// transient failure is common (TCP RST during TUN warm-up), two in
    /// a row strongly suggests the outbound is unreachable.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(int clashApiPort, CancellationToken ct = default)
    {
        if (clashApiPort <= 0 || clashApiPort > 65535)
            return new ProbeResult(true, $"invalid Clash API port {clashApiPort}", 0);

        var url = $"http://127.0.0.1:{clashApiPort}/proxies/proxy/delay" +
                  $"?url={Uri.EscapeDataString("http://www.gstatic.com/generate_204")}" +
                  $"&timeout=5000";

        int lastDelay = 0;
        string? lastError = null;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    // Body looks like: {"delay": 123}
                    // Negative or zero delay means timeout/unreachable.
                    try
                    {
                        var jo = JObject.Parse(body);
                        var delay = jo["delay"]?.Value<int>() ?? 0;
                        lastDelay = delay;
                        if (delay > 0)
                        {
                            _logger?.Debug(
                                "[ConfigSanityCheck] Probe attempt {Attempt} OK ({Delay} ms)",
                                attempt, delay);
                            return new ProbeResult(false, null, delay);
                        }
                        lastError = $"Clash API reported delay=0 (unreachable, attempt {attempt})";
                    }
                    catch (Exception parseEx)
                    {
                        lastError = $"Clash API response not parseable (attempt {attempt}): {parseEx.Message}";
                    }
                }
                else
                {
                    // Clash API returns 504 when the proxy is unreachable.
                    lastError = $"Clash API HTTP {(int)resp.StatusCode} (attempt {attempt}): {body}";
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"Clash API call timed out (attempt {attempt})";
            }
            catch (Exception ex)
            {
                lastError = $"Clash API call failed (attempt {attempt}): {ex.Message}";
            }

            _logger?.Debug(
                "[ConfigSanityCheck] Probe attempt {Attempt} failed: {Reason}",
                attempt, lastError);

            if (attempt < 2)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        return new ProbeResult(true,
            lastError ?? "two consecutive probe failures",
            lastDelay);
    }
}

/// <summary>
/// Outcome of <see cref="ConfigSanityCheck.CheckBeforeStart(JObject)"/>.
/// <para><c>IsDead=true</c> means the caller must NOT launch sing-box with
/// this config — try <see cref="AutoFailoverEngine"/> first, then surface
/// the reason to the user.</para>
/// </summary>
public sealed record PreStartCheckResult(bool IsDead, string? Reason, string? OffendingField);

/// <summary>
/// Outcome of <see cref="ConfigSanityCheck.ProbeAsync"/>.
/// <para><c>IsDead=true</c> means BOTH probe attempts failed and the
/// outbound is presumed unreachable. <c>LastDelayMs</c> is the most-
/// recent successful delay value (0 if none).</para>
/// </summary>
public sealed record ProbeResult(bool IsDead, string? Reason, int LastDelayMs);
