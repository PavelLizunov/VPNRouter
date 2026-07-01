using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// connects to the placeholder host (see
/// <see cref="PlaceholderDefense.KnownFingerprints"/> — a dead/hostile
/// host with the same Reality public_key as Android's placeholder).
/// F-A/B/D prevent the bad state from being created; F-E catches a user
/// who's already in it.</para>
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
    // v3.0 Phase 3D (2026-05-18) — fingerprint tables moved to the
    // consolidated single-source-of-truth at
    // <see cref="PlaceholderDefense.KnownFingerprints"/>. The three
    // hash-set properties below are kept as back-compat forwarders so
    // callers reaching in directly (AutoFailoverEngine,
    // VlessServersResolver.IsPlaceholderEntry pre-consolidation) still
    // compile. Layer-E logic itself (this file's
    // <see cref="CheckBeforeStart(JObject)"/>) now delegates to
    // <see cref="PlaceholderDefense.LayerE_RuntimeSanity.InspectOutbound"/>
    // via the shared <see cref="InspectOutbound(JObject?)"/> entry point
    // preserved below.
    //
    // Pre-3D content (kept here as a historical note for grep
    // discoverability):
    //   plans/stas-evidence-config.yaml   (active_server: khunrath_ln)
    //   plans/stas-evidence-current.json  (outbound: dead host pubkey/sid)

    /// <summary>Back-compat forwarder for the consolidated pubkey set. See <see cref="PlaceholderDefense.KnownPubkeys"/>.</summary>
    public static IReadOnlySet<string> KnownPlaceholderPubkeys => PlaceholderDefense.KnownPubkeys;

    /// <summary>Back-compat forwarder for the consolidated short_id set. See <see cref="PlaceholderDefense.KnownShortIds"/>.</summary>
    public static IReadOnlySet<string> KnownPlaceholderShortIds => PlaceholderDefense.KnownShortIds;

    /// <summary>Back-compat forwarder for the consolidated server-IP set. See <see cref="PlaceholderDefense.KnownServers"/>.</summary>
    public static IReadOnlySet<string> KnownPlaceholderServers => PlaceholderDefense.KnownServers;

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
    ///
    /// <para>Phase 4 (2026-05-18) — migrated from Newtonsoft
    /// <c>JObject</c>/<c>JArray</c> to System.Text.Json
    /// <c>JsonObject</c>/<c>JsonArray</c>. The detection logic and field
    /// extraction paths are byte-equivalent; the underlying tree
    /// representation differs only in type name.</para>
    /// </summary>
    public PreStartCheckResult CheckBeforeStart(JsonObject singboxConfig)
    {
        if (singboxConfig == null)
            return new PreStartCheckResult(true, "sing-box config is null", null);

        var outbounds = singboxConfig["outbounds"] as JsonArray;
        if (outbounds == null || outbounds.Count == 0)
            return new PreStartCheckResult(true, "sing-box config has no outbounds", "outbounds");

        // Walk every outbound that looks like a proxy (vless / hysteria2 /
        // tuic / shadowsocks / trojan). We treat the FIRST proxy-typed
        // outbound as the active one — same heuristic CustomConfigInjector
        // uses to identify the proxy tag.
        var proxy = FindFirstProxyOutbound(outbounds);

        if (proxy == null)
        {
            // AmneziaWG (sing-box-lx): the "proxy" is a wireguard ENDPOINT, not
            // an outbound. A proxy-tagged endpoint is a valid proxy target — the
            // outbound-shaped server/server_port checks below don't apply, and
            // runtime reachability rides the tunnel. Don't declare it dead, else
            // every AWG config aborts here before sing-box even launches.
            if (HasProxyEndpoint(singboxConfig))
                return new PreStartCheckResult(false, "proxy is a wireguard endpoint (AmneziaWG)", null);

            // No proxy outbound at all — this is fatal for generated mode
            // (the route rules would point at a non-existent tag). Same
            // class of bug LeakProtection catches separately.
            return new PreStartCheckResult(true,
                "no proxy outbound found (vless/hysteria2/tuic/shadowsocks/naive/trojan)",
                "outbounds");
        }

        // ── Structural checks ──
        var server = StjNodeHelpers.AsString(proxy["server"]);
        if (string.IsNullOrWhiteSpace(server))
            return new PreStartCheckResult(true,
                "proxy outbound has empty 'server' field — config never reachable",
                "outbound.server");

        var serverPort = StjNodeHelpers.AsInt(proxy["server_port"]) ?? 0;
        if (serverPort <= 0)
            return new PreStartCheckResult(true,
                "proxy outbound has invalid 'server_port' (must be 1-65535)",
                "outbound.server_port");

        // VLESS-specific: uuid is mandatory. Other protocols use password
        // (Hysteria2 / TUIC / Shadowsocks / Trojan) so the field is
        // protocol-conditional.
        var proxyType = StjNodeHelpers.AsString(proxy["type"])?.ToLowerInvariant() ?? "";
        if (proxyType == "vless")
        {
            var uuid = StjNodeHelpers.AsString(proxy["uuid"]);
            if (string.IsNullOrWhiteSpace(uuid))
                return new PreStartCheckResult(true,
                    "VLESS proxy outbound has empty 'uuid' — handshake would fail",
                    "outbound.uuid");
        }

        // ── Placeholder fingerprint match (delegated to PlaceholderDefense) ──
        // v2.32.3-r1 (2026-05-17): inspection logic moved to InspectOutbound
        // so CustomConfigInjector and any future caller share the same
        // single-source-of-truth check. Kept the per-field log lines here
        // because they include the actual value (more useful in production
        // logs than the typed exception).
        var offendingField = InspectOutbound(proxy);
        if (offendingField != null)
        {
            var reality = proxy["tls"]?["reality"] as JsonObject;
            switch (offendingField)
            {
                case "reality.public_key":
                {
                    var pubkey = StjNodeHelpers.AsString(reality?["public_key"]);
                    _logger?.Warning(
                        "[ConfigSanityCheck] Placeholder Reality public_key detected: {Key}",
                        pubkey);
                    return new PreStartCheckResult(true,
                        $"Reality public_key matches known placeholder ({pubkey})",
                        "outbound.tls.reality.public_key");
                }
                case "reality.short_id":
                {
                    var shortId = StjNodeHelpers.AsString(reality?["short_id"]);
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
    /// Back-compat forwarder — locates the first proxy-typed outbound in a
    /// sing-box <c>outbounds</c> array (vless / hysteria2 / tuic /
    /// shadowsocks / trojan). v3.0 Phase 3D delegates to
    /// <see cref="PlaceholderDefense.LayerE_RuntimeSanity.FindFirstProxyOutbound"/>
    /// so both this F-E call site and <see cref="CustomConfigInjector"/>'s
    /// placeholder gate share a single source of truth for "find the
    /// outbound to inspect".
    /// </summary>
    internal static JsonObject? FindFirstProxyOutbound(JsonArray outbounds) =>
        PlaceholderDefense.LayerE_RuntimeSanity.FindFirstProxyOutbound(outbounds);

    /// <summary>
    /// True when the config carries a top-level <c>endpoints[]</c> entry tagged
    /// <c>proxy</c> (the AmneziaWG wireguard endpoint, sing-box-lx). Used so the
    /// pre-start check treats an endpoint-based proxy as valid instead of dead.
    /// </summary>
    private static bool HasProxyEndpoint(JsonObject singboxConfig)
    {
        if (singboxConfig["endpoints"] is not JsonArray endpoints) return false;
        foreach (var e in endpoints)
            if (e is JsonObject o && StjNodeHelpers.AsString(o["tag"]) == "proxy")
                return true;
        return false;
    }

    /// <summary>
    /// Back-compat forwarder — inspects a single sing-box proxy outbound
    /// JsonObject for placeholder fingerprints (Reality public_key, Reality
    /// short_id, server IP). v3.0 Phase 3D delegates to
    /// <see cref="PlaceholderDefense.LayerE_RuntimeSanity.InspectOutbound"/>.
    /// Field-name convention matches
    /// <see cref="PlaceholderDefense.Inspect(string?, string?, string?)"/>.
    /// </summary>
    public static string? InspectOutbound(JsonObject? proxy) =>
        PlaceholderDefense.LayerE_RuntimeSanity.InspectOutbound(proxy);

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
            // Phase 4 (2026-05-18) — STJ JsonNode.Parse mirrors Newtonsoft's
            // JObject.Parse. JsonException is the STJ equivalent of
            // JsonReaderException; both classes hit the same generic catch.
            var jo = JsonNode.Parse(singboxConfigJson) as JsonObject
                ?? throw new JsonException("sing-box config root is not an object");
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
                        var jo = JsonNode.Parse(body) as JsonObject;
                        var delay = StjNodeHelpers.AsInt(jo?["delay"]) ?? 0;
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
