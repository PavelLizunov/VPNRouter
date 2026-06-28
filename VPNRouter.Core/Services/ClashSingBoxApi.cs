#nullable enable
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using VPNRouter.Core.Json;

namespace VPNRouter.Core.Services;

/// <summary>
/// Concrete <see cref="ISingBoxApi"/> implementation that talks HTTP to
/// sing-box's Clash API (Phase 2D-4, 2026-05-17).
///
/// <para><strong>Loopback-only.</strong> The ctor refuses any base URL
/// whose host doesn't resolve to a loopback address (<c>127.0.0.0/8</c>
/// or <c>::1</c>). Allowing remote control would let a hostile network
/// re-aim the user's tunnel via <see cref="SelectProxyAsync"/>. The
/// only legitimate use of the Clash API is on the same machine as
/// sing-box, so this is a hard guard rather than a config knob.</para>
///
/// <para><strong>Timeouts.</strong> Each method enforces a per-call
/// deadline via a linked <see cref="CancellationTokenSource"/>: 3s for
/// <see cref="ReloadConfigAsync"/> (matches the pre-2D-4 inline value in
/// <c>SingBoxManager.TryHotReload</c>), 1s for the ping/list endpoints.
/// The underlying <see cref="HttpClient"/> has its own infinite-timeout
/// setting; we don't rely on it.</para>
///
/// <para><strong>Pre-2D-4 history.</strong> The HTTP code lived inline in
/// <see cref="SingBoxManager"/>.<c>TryHotReload</c> / <c>IsClashApiAlive</c>.
/// 2D-4 extracted it without changing any wire behaviour — the URL shapes,
/// JSON body shapes, and status-code handling are byte-identical.</para>
/// </summary>
public sealed class ClashSingBoxApi : ISingBoxApi, IDisposable
{
    // 3s upper bound for hot-reload — matches the pre-2D-4 inline value in
    // SingBoxManager.TryHotReload. The hot-reload write may stall briefly
    // on slow disks; longer values just delay the fall-back to full
    // process restart, which is the correct outcome here.
    private static readonly TimeSpan ReloadDeadline = TimeSpan.FromSeconds(3);

    // 1s for ping/list endpoints — these are read-only and snappy under
    // healthy conditions. A long timeout would delay auto-failover
    // decisions and the periodic health-tick.
    private static readonly TimeSpan PingDeadline = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Construct a Clash API client pointed at <paramref name="baseUrl"/>.
    /// Default <c>http://127.0.0.1:9090</c> matches the YAML default in
    /// <c>AppSettings.SingBox.ClashApi</c>.
    /// </summary>
    /// <param name="httpClient">Optional pre-configured HttpClient. When
    /// null, an owned client is created (and disposed by
    /// <see cref="Dispose"/>). Phase 2D-3 sibling task will introduce
    /// <c>IHttpClient</c> + <c>PolicyHttpClient</c> — when it lands,
    /// switch this ctor to take that abstraction.</param>
    /// <param name="baseUrl">Full base URL including scheme. Must point
    /// at a loopback host — non-loopback bases throw
    /// <see cref="ArgumentException"/>.</param>
    /// <param name="logger">Optional Serilog logger. Defaults to
    /// <see cref="Log.Logger"/> for parity with the pre-2D-4 inline code.</param>
    /// <exception cref="ArgumentException">Thrown if
    /// <paramref name="baseUrl"/> is not a valid HTTP/HTTPS URL OR its
    /// host is not a loopback address. This is a hard security guard.</exception>
    public ClashSingBoxApi(
        HttpClient? httpClient = null,
        string baseUrl = "http://127.0.0.1:9090",
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Clash API base URL cannot be empty.", nameof(baseUrl));

        // Strip trailing slash so we control concatenation explicitly.
        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Clash API base URL must be an absolute http(s) URL; got '{baseUrl}'.",
                nameof(baseUrl));
        }

        // Loopback-only security guard (see class doc). A hostile network
        // can't redirect SelectProxyAsync if we refuse to talk to anyone
        // off the local machine.
        if (!IsLoopbackHost(uri.Host))
        {
            throw new ArgumentException(
                $"Clash API base URL must point at a loopback host; '{uri.Host}' is not loopback. " +
                "Remote Clash control is a security risk — sing-box's Clash API listens on 127.0.0.1 by convention.",
                nameof(baseUrl));
        }

        _baseUrl = normalized;
        _logger = logger ?? Log.Logger;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // Owned HttpClient: keep Timeout high so our per-call
            // CancellationToken deadlines are the authoritative budget.
            // (HttpClient.Timeout is the hard upper bound that converts
            // to OperationCanceledException — we don't want a static
            // default fighting our deadlines.)
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            _logger.Warning("[ClashSingBoxApi] ReloadConfigAsync called with empty configPath");
            return false;
        }

        // Build the body the same way SingBoxManager.TryHotReload did
        // pre-2D-4: { "path": "<escaped path>" } with backslash escaping.
        // Use System.Text.Json for the escape instead of ad-hoc string
        // replace — handles Windows path separators + Unicode correctly.
        //
        // Phase 6 — Wave 31b (2026-05-19): the pre-Wave-31b code used an
        // anonymous type (`new { path = ... }`) which triggers IL3050 at
        // AOT publish (no compiled JsonTypeInfo). Hoisted to a named
        // record + [JsonPropertyName("path")] so the wire format stays
        // identical (lowercase "path" key) while AOT can resolve it via
        // the AppJsonContext source generator.
        var body = JsonSerializer.Serialize(
            new ClashSetConfigDto(configPath), Json.AppJsonContext.Default.ClashSetConfigDto);
        var url = $"{_baseUrl}/configs?force=true";

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(ReloadDeadline);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PutAsync(url, content, deadlineCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.Information(
                    "[ClashSingBoxApi] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    (int)response.StatusCode);
                return true;
            }

            // Read body for diagnostics; bounded by the same deadline so a
            // hung Clash API can't block us forever on the error path.
            var respBody = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            _logger.Warning(
                "[ClashSingBoxApi] Hot-reload HTTP {Code}: {Body}",
                (int)response.StatusCode, respBody);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Internal deadline fired (3s). Caller cancellation propagates
            // naturally; this branch is just our budget.
            _logger.Debug("[ClashSingBoxApi] Hot-reload timed out after {Sec}s", ReloadDeadline.TotalSeconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — surface that to caller policy by returning
            // false. (We could rethrow, but the contract says null/false
            // for failures so HealthMonitor's caller stays simple.)
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/version", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            var doc = await JsonSerializer.DeserializeAsync(
                stream, Json.AppJsonContext.Default.VersionDto, deadlineCts.Token).ConfigureAwait(false);

            return string.IsNullOrEmpty(doc?.Version) ? null : doc.Version;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetVersionAsync failed");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default)
    {
        // Return a zero-snapshot on any failure. Caller can distinguish
        // "no connections" from "couldn't reach API" via the timestamp +
        // surrounding context (a healthy tunnel almost always has >0
        // connections, so 0 is itself a signal).
        var failureSnapshot = new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow);

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/connections", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return failureSnapshot;

            // F2 (v2.45.0): this poll runs ~every 2s while connected. The old
            // DeserializeAsync<ConnectionsDto> materialized a List<JsonElement>
            // (one JsonElement per active connection) purely to take .Count —
            // allocation scaling with connection count. Stream the summary
            // instead: read downloadTotal/uploadTotal + the connections array
            // LENGTH via Utf8JsonReader, no per-element allocation.
            var bytes = await response.Content.ReadAsByteArrayAsync(deadlineCts.Token).ConfigureAwait(false);
            if (!ParseConnectionsSummary(bytes, out var down, out var up, out var count))
                return failureSnapshot;

            return new ConnectionsSnapshot(
                ActiveCount: count,
                TotalUploadBytes: up,
                TotalDownloadBytes: down,
                CapturedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return failureSnapshot;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetConnectionsAsync failed");
            return failureSnapshot;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            _logger.Warning("[ClashSingBoxApi] SelectProxyAsync called with empty group name");
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.Warning("[ClashSingBoxApi] SelectProxyAsync called with empty proxy name");
            return false;
        }

        // URL-encode the group name — selector tags can legitimately
        // contain spaces, slashes, unicode, or arbitrary user input from
        // generated config. Same defensive escape sing-box's own UI does.
        var encodedGroup = Uri.EscapeDataString(group);
        var url = $"{_baseUrl}/proxies/{encodedGroup}";
        // Phase 6 — Wave 31b (2026-05-19): see ReloadConfigAsync above —
        // anonymous-type Serialize hoisted to a named record for AOT.
        var body = JsonSerializer.Serialize(
            new ClashSelectProxyDto(name), Json.AppJsonContext.Default.ClashSelectProxyDto);

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(ReloadDeadline); // Allow up to 3s — proxy switch may probe new target.

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PutAsync(url, content, deadlineCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.Information(
                    "[ClashSingBoxApi] Proxy switch succeeded: {Group} → {Name} (HTTP {Code})",
                    group, name, (int)response.StatusCode);
                return true;
            }

            var respBody = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            _logger.Warning(
                "[ClashSingBoxApi] Proxy switch failed: {Group} → {Name} HTTP {Code}: {Body}",
                group, name, (int)response.StatusCode, respBody);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] SelectProxyAsync failed");
            return false;
        }
    }

    /// <summary>
    /// v2.44.1-r6 — for a urltest / selector GROUP, return the member outbound
    /// it is currently routing through (Clash API <c>GET /proxies/{group}</c> →
    /// the <c>"now"</c> field). When <c>AutoSelectBestServer</c> builds the
    /// <c>proxy</c> outbound as a urltest over the subscription pool, this is the
    /// ONLY way to know which server traffic actually exits through (the stored
    /// active-server is irrelevant — urltest picks the fastest at runtime). The
    /// desktop status line / list highlight resolve this so they show the REAL
    /// server instead of the stale first-in-list. Returns null on any failure
    /// (caller falls back to a generic "auto-select" label). Parsed with
    /// <see cref="JsonDocument"/> (no source-gen DTO needed).
    /// </summary>
    public async Task<string?> GetGroupNowAsync(string group, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            var encodedGroup = Uri.EscapeDataString(group);
            using var response = await _http
                .GetAsync($"{_baseUrl}/proxies/{encodedGroup}", deadlineCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: deadlineCts.Token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("now", out var nowEl) &&
                nowEl.ValueKind == JsonValueKind.String)
            {
                var now = nowEl.GetString();
                return string.IsNullOrEmpty(now) ? null : now;
            }
            return null;
        }
        catch
        {
            // Best-effort only — a missing/failed selection just means the
            // caller shows the generic auto-select label. Never throws.
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/proxies", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<ProxyInfo>();

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync(
                stream, Json.AppJsonContext.Default.ProxiesEnvelopeDto, deadlineCts.Token).ConfigureAwait(false);

            if (dto?.Proxies is null)
                return Array.Empty<ProxyInfo>();

            var list = new List<ProxyInfo>(dto.Proxies.Count);
            foreach (var kv in dto.Proxies)
            {
                var name = kv.Key;
                var meta = kv.Value;
                if (meta is null)
                    continue;

                // Pull the most recent history entry's delay if present.
                int? delayMs = null;
                DateTimeOffset? delayAt = null;
                if (meta.History is { Count: > 0 })
                {
                    var last = meta.History[meta.History.Count - 1];
                    delayMs = last.Delay > 0 ? last.Delay : null;
                    if (DateTimeOffset.TryParse(last.Time, out var parsed))
                        delayAt = parsed;
                }

                list.Add(new ProxyInfo(
                    Name: name,
                    Type: meta.Type ?? "unknown",
                    DelayMs: delayMs,
                    DelayMeasuredAt: delayAt));
            }
            return list;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ProxyInfo>();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] ListProxiesAsync failed");
            return Array.Empty<ProxyInfo>();
        }
    }

    /// <inheritdoc/>
    public async Task<int?> GetProxyDelayAsync(string proxyTag, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(proxyTag) || string.IsNullOrWhiteSpace(testUrl))
            return null;

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Give the HTTP call a hair more than the probe's own timeout so the
            // sing-box-side deadline (passed as ?timeout=) is what actually fires,
            // yielding a clean null rather than a transport cancellation.
            deadlineCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs + 1500));

            // GET /proxies/{tag}/delay?timeout={ms}&url={testUrl}. sing-box fetches
            // testUrl THROUGH the named proxy and returns {"delay": N} on success or
            // a non-200 + {"message": "..."} when the proxy can't reach it.
            var encodedTag = Uri.EscapeDataString(proxyTag);
            var encodedUrl = Uri.EscapeDataString(testUrl);
            var url = $"{_baseUrl}/proxies/{encodedTag}/delay?timeout={timeoutMs}&url={encodedUrl}";

            using var response = await _http.GetAsync(url, deadlineCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 408 / 503 / 500 etc — the proxy could not reach the test URL.
                // That's exactly the "unreachable" signal the caller wants.
                _logger.Debug("[ClashSingBoxApi] Proxy delay probe {Tag} -> HTTP {Code} (treated as unreachable)",
                    proxyTag, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("delay", out var delayEl)
                && delayEl.TryGetInt32(out var delay)
                && delay >= 0)
            {
                return delay;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetProxyDelayAsync failed for {Tag}", proxyTag);
            return null;
        }
    }

    /// <summary>Disposes the owned HttpClient when this instance created
    /// one (i.e. ctor was called without <paramref name="httpClient"/>).
    /// An externally-supplied client is left alone — disposal is the
    /// caller's responsibility.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// True if <paramref name="host"/> is a loopback address by name or
    /// literal. Accepts <c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>.
    /// Anything else returns false — including hostnames that might
    /// resolve to loopback at runtime, because DNS can be spoofed.
    /// <para>Internal so the identical guard can be reused by
    /// <see cref="ClashLogStream"/> — one source of truth for the loopback
    /// security primitive rather than a copy.</para>
    /// </summary>
    internal static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);

        return false;
    }

    // Phase 7 Wave 34: flipped private → internal sealed so AppJsonContext
    // can register these DTOs. The JsonSerializableAttribute requires
    // referenceable types from the context's compilation unit; internal
    // + InternalsVisibleTo("VPNRouter.Tests") keeps the surface private to
    // Core's assembly while making the types reachable from
    // Json/AppJsonContext.cs.

    // sing-box's /version returns { "version": "1.13.10", "premium": true, ... }.
    internal sealed class VersionDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    /// <summary>
    /// F2 (v2.45.0): read downloadTotal / uploadTotal + the connections array
    /// LENGTH from a /connections body via Utf8JsonReader, WITHOUT materializing
    /// a List&lt;JsonElement&gt; per connection. Tolerant of property order and
    /// missing fields; <c>reader.Skip()</c> walks past each connection element
    /// without descending into it. Returns false only on a malformed body.
    /// </summary>
    internal static bool ParseConnectionsSummary(
        ReadOnlySpan<byte> json, out long download, out long upload, out int activeCount)
    {
        download = 0; upload = 0; activeCount = 0;
        try
        {
            var reader = new Utf8JsonReader(json);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("downloadTotal"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number) download = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("uploadTotal"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number) upload = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("connections"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            activeCount++;
                            reader.Skip(); // past this element's whole subtree (no alloc)
                        }
                    }
                }
            }
            return true;
        }
        catch
        {
            // Malformed body OR a downloadTotal/uploadTotal that isn't an Int64
            // (float / out-of-range -> GetInt64 throws FormatException) — honour
            // the "false on bad body" contract; the caller maps that to a zeroed
            // snapshot (one stale tick, no crash).
            return false;
        }
    }

    // sing-box's /connections returns { "downloadTotal":N, "uploadTotal":N, "connections":[...] }.
    internal sealed class ConnectionsDto
    {
        [JsonPropertyName("downloadTotal")]
        public long DownloadTotal { get; set; }

        [JsonPropertyName("uploadTotal")]
        public long UploadTotal { get; set; }

        [JsonPropertyName("connections")]
        public List<JsonElement>? Connections { get; set; }
    }

    // sing-box's /proxies returns { "proxies": { "name": { type, history, ... } } }.
    internal sealed class ProxiesEnvelopeDto
    {
        [JsonPropertyName("proxies")]
        public Dictionary<string, ProxyDto>? Proxies { get; set; }
    }

    internal sealed class ProxyDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("history")]
        public List<ProxyHistoryDto>? History { get; set; }
    }

    internal sealed class ProxyHistoryDto
    {
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("delay")]
        public int Delay { get; set; }
    }
}

// Phase 6 — Wave 31b (2026-05-19): hoisted anonymous-type Serialize
// sites in ClashSingBoxApi.ReloadConfigAsync + .SelectProxyAsync to
// named records so AppJsonContext can register them and AOT can resolve
// their JsonTypeInfo at compile time. The wire format is identical:
// the [JsonPropertyName] attributes pin the same lowercase key names
// the anonymous types emitted by default (anon properties preserve
// declared casing — `new { path = ... }` writes "path"; `new { name }`
// writes "name"). Phase 4 STJ migration tests pinning these bodies
// pass unchanged.
//
// Visibility: internal so AppJsonContext (also internal) can reference
// them. Records take one positional parameter each — matches the
// single-field shape of the original anonymous types.
internal sealed record ClashSetConfigDto(
    [property: JsonPropertyName("path")] string Path);

internal sealed record ClashSelectProxyDto(
    [property: JsonPropertyName("name")] string Name);
