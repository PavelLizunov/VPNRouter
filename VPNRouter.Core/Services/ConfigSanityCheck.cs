using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class ConfigSanityCheck
{
    public static IReadOnlySet<string> KnownPlaceholderPubkeys => PlaceholderDefense.KnownPubkeys;

    public static IReadOnlySet<string> KnownPlaceholderShortIds => PlaceholderDefense.KnownShortIds;

    public static IReadOnlySet<string> KnownPlaceholderServers => PlaceholderDefense.KnownServers;

    private readonly ILogger? _logger;
    private readonly HttpClient _http;

    public ConfigSanityCheck(ILogger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public PreStartCheckResult CheckBeforeStart(JsonObject singboxConfig)
    {
        if (singboxConfig == null)
            return new PreStartCheckResult(true, "sing-box config is null", null);

        var outbounds = singboxConfig["outbounds"] as JsonArray;
        if (outbounds == null || outbounds.Count == 0)
            return new PreStartCheckResult(true, "sing-box config has no outbounds", "outbounds");

        var proxy = FindFirstProxyOutbound(outbounds);

        if (proxy == null)
        {
            if (HasProxyEndpoint(singboxConfig))
                return new PreStartCheckResult(false, "proxy is a wireguard endpoint (AmneziaWG)", null);

            return new PreStartCheckResult(true,
                "no proxy outbound found (vless/hysteria2/tuic/shadowsocks/naive/trojan)",
                "outbounds");
        }

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

        var proxyType = StjNodeHelpers.AsString(proxy["type"])?.ToLowerInvariant() ?? "";
        if (proxyType == "vless")
        {
            var uuid = StjNodeHelpers.AsString(proxy["uuid"]);
            if (string.IsNullOrWhiteSpace(uuid))
                return new PreStartCheckResult(true,
                    "VLESS proxy outbound has empty 'uuid' — handshake would fail",
                    "outbound.uuid");
        }

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

        return new PreStartCheckResult(false, null, null);
    }

    internal static JsonObject? FindFirstProxyOutbound(JsonArray outbounds) =>
        PlaceholderDefense.LayerE_RuntimeSanity.FindFirstProxyOutbound(outbounds);

    private static bool HasProxyEndpoint(JsonObject singboxConfig)
    {
        if (singboxConfig["endpoints"] is not JsonArray endpoints) return false;
        foreach (var e in endpoints)
            if (e is JsonObject o && StjNodeHelpers.AsString(o["tag"]) == "proxy")
                return true;
        return false;
    }

    public static string? InspectOutbound(JsonObject? proxy) =>
        PlaceholderDefense.LayerE_RuntimeSanity.InspectOutbound(proxy);

    public PreStartCheckResult CheckBeforeStart(string singboxConfigJson)
    {
        if (string.IsNullOrWhiteSpace(singboxConfigJson))
            return new PreStartCheckResult(true, "sing-box config JSON is empty", null);

        try
        {
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

    public async Task<ProbeResult> ProbeAsync(int clashApiPort, CancellationToken ct = default)
        => await ProbeAsync(clashApiPort, clashApiSecret: null, ct).ConfigureAwait(false);

    public async Task<ProbeResult> ProbeAsync(int clashApiPort, string? clashApiSecret, CancellationToken ct = default)
    {
        if (clashApiPort <= 0 || clashApiPort > 65535)
            return new ProbeResult(true, $"invalid Clash API port {clashApiPort}", 0);

        var url = $"http://127.0.0.1:{clashApiPort}/proxies/proxy/delay" +
                  $"?url={Uri.EscapeDataString("http:
                  $"&timeout=5000";

        int lastDelay = 0;
        string? lastError = null;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(clashApiSecret))
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clashApiSecret);
                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
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

public sealed record PreStartCheckResult(bool IsDead, string? Reason, string? OffendingField);

public sealed record ProbeResult(bool IsDead, string? Reason, int LastDelayMs);
