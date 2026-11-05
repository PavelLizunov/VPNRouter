using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal enum BusinessNodeFreshness { Unknown, Fresh, Stale }

internal sealed record BusinessNodeMetadata(
    string EndpointHost,
    string? CountryIso,
    long? MemoryTotalMib,
    long? MemoryAvailableMib,
    uint? Load1MinX100,
    long? ObservedAtUnixSeconds,
    ulong? AgeSeconds,
    BusinessNodeFreshness Freshness)
{
    // Unix load average is deliberately separate from this RAM percentage.
    internal double? MemoryUsedPercent =>
        MemoryTotalMib is > 0 && MemoryAvailableMib is >= 0 && MemoryAvailableMib <= MemoryTotalMib
            ? 100.0 * (1.0 - (double)MemoryAvailableMib.Value / MemoryTotalMib.Value)
            : null;
}

// A received snapshot and its monotonic age survive view/window reconstruction.
internal sealed class BusinessNodeSnapshot
{
    internal const int MaximumBytes = 256 * 1024;
    private const long MaximumUnixSeconds = 253402300799;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal IReadOnlyDictionary<string, BusinessNodeMetadata> Nodes { get; }
    internal long GeneratedAtUnixSeconds { get; }
    internal uint MaxAgeSeconds { get; }
    internal long ReceivedTimestamp { get; }
    internal double ElapsedSeconds => Math.Max(0, Stopwatch.GetElapsedTime(ReceivedTimestamp).TotalSeconds);

    private BusinessNodeSnapshot(Dictionary<string, BusinessNodeMetadata> nodes, long generatedAt, uint maxAgeSeconds)
    {
        Nodes = new ReadOnlyDictionary<string, BusinessNodeMetadata>(nodes);
        GeneratedAtUnixSeconds = generatedAt;
        MaxAgeSeconds = maxAgeSeconds;
        ReceivedTimestamp = Stopwatch.GetTimestamp();
    }

    internal bool TryGetNode(string? endpointHost, out BusinessNodeMetadata node)
    {
        node = null!;
        return TryNormalizeHost(endpointHost, out var normalized) && Nodes.TryGetValue(normalized, out node!);
    }

    internal ulong? GetAgeSeconds(BusinessNodeMetadata node) => GetAgeSeconds(node, Stopwatch.GetTimestamp());

    internal ulong? GetAgeSeconds(BusinessNodeMetadata node, long timestamp)
    {
        if (node.AgeSeconds is not { } age) return null;
        var elapsed = (ulong)Math.Min(long.MaxValue,
            Math.Floor(Math.Max(0, Stopwatch.GetElapsedTime(ReceivedTimestamp, timestamp).TotalSeconds)));
        return elapsed > ulong.MaxValue - age ? ulong.MaxValue : age + elapsed;
    }

    internal BusinessNodeFreshness GetFreshness(BusinessNodeMetadata node) => GetFreshness(node, Stopwatch.GetTimestamp());

    internal BusinessNodeFreshness GetFreshness(BusinessNodeMetadata node, long timestamp)
    {
        if (node.Freshness == BusinessNodeFreshness.Unknown || GetAgeSeconds(node, timestamp) is not { } age)
            return BusinessNodeFreshness.Unknown;
        return node.Freshness == BusinessNodeFreshness.Stale || age > MaxAgeSeconds
            ? BusinessNodeFreshness.Stale : BusinessNodeFreshness.Fresh;
    }

    internal static BusinessNodeSnapshot Parse(string json)
    {
        try
        {
            if (json == null || json.Length > MaximumBytes || StrictUtf8.GetByteCount(json) > MaximumBytes)
                throw InvalidSnapshot();
            if (json.StartsWith('\uFEFF')) json = json[1..];
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            RequireObject(root);
            if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
                throw InvalidSnapshot();
            var generatedAt = RequiredTimestamp(root, "generated_at");
            if (!root.TryGetProperty("max_age_seconds", out var ttl) || !ttl.TryGetUInt32(out var maxAge) || maxAge is 0 or > 86400)
                throw InvalidSnapshot();
            if (!root.TryGetProperty("nodes", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 500)
                throw InvalidSnapshot();

            var nodes = new Dictionary<string, BusinessNodeMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.EnumerateArray())
            {
                RequireObject(row);
                if (!row.TryGetProperty("endpoint_host", out var endpoint) || endpoint.ValueKind != JsonValueKind.String ||
                    !TryNormalizeHost(endpoint.GetString(), out var host))
                    throw InvalidSnapshot();
                var country = Country(row);
                var total = OptionalInt64(row, "memory_total_mib");
                var available = OptionalInt64(row, "memory_available_mib");
                var load = OptionalUInt32(row, "load_1min_x100");
                var observed = OptionalInt64(row, "observed_at");
                if (observed is < 0 or > MaximumUnixSeconds) throw InvalidSnapshot();
                var age = OptionalUInt64(row, "age_seconds");
                if (age > (ulong)generatedAt) throw InvalidSnapshot();
                var freshness = Freshness(row);
                // Server time and age are authoritative; the guest's wall clock is never consulted.
                if (observed == null || age == null || observed > generatedAt ||
                    age != (ulong)(generatedAt - observed.Value))
                {
                    age = null;
                    freshness = BusinessNodeFreshness.Unknown;
                }
                else if (freshness == BusinessNodeFreshness.Fresh && age > maxAge)
                    freshness = BusinessNodeFreshness.Stale;

                if (!nodes.TryAdd(host, new(host, country, total, available, load, observed, age, freshness)))
                    throw InvalidSnapshot();
            }
            return new(nodes, generatedAt, maxAge);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or DecoderFallbackException or ArgumentException)
        {
            // Parser diagnostics can contain the private response; never retain an inner exception.
            throw InvalidSnapshot();
        }
    }

    internal static bool TryNormalizeHost(string? value, out string host)
    {
        host = "";
        if (string.IsNullOrEmpty(value) || value.Length > 255 || value.Any(char.IsWhiteSpace) ||
            value.IndexOfAny(['/', '\\', '@', '?', '#', '%']) >= 0)
            return false;
        var normalized = VlessUriParser.NormalizeHost(value);
        if (IPAddress.TryParse(normalized, out var address))
        {
            // Brackets are only meaningful for IPv6, never a port or DNS wrapper.
            if (value.Contains('[') && address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                return false;
            host = address.ToString();
            return true;
        }
        if (normalized.IndexOfAny([':', '[', ']']) >= 0) return false;
        try
        {
            normalized = new IdnMapping().GetAscii(normalized);
            if (normalized.EndsWith('.')) normalized = normalized[..^1];
        }
        catch (ArgumentException) { return false; }
        if (normalized.Length is 0 or > 253) return false;
        foreach (var label in normalized.Split('.'))
        {
            if (label.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                return false;
        }
        // Reject malformed IPv4 spellings instead of treating them as DNS names.
        if (normalized.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        host = normalized.ToLowerInvariant();
        return true;
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidSnapshot();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name)) throw InvalidSnapshot();
    }

    private static long RequiredTimestamp(JsonElement element, string name)
    {
        var value = OptionalInt64(element, name);
        return value is >= 0 and <= MaximumUnixSeconds ? value.Value : throw InvalidSnapshot();
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw InvalidSnapshot();
    }

    private static uint? OptionalUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number) ? number : throw InvalidSnapshot();
    }

    private static ulong? OptionalUInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number : throw InvalidSnapshot();
    }

    private static string? Country(JsonElement row)
    {
        if (!row.TryGetProperty("country_iso", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidSnapshot();
        var code = value.GetString();
        if (code is not { Length: 2 } || !code.All(char.IsAsciiLetter)) throw InvalidSnapshot();
        return code.ToUpperInvariant();
    }

    private static BusinessNodeFreshness Freshness(JsonElement row)
    {
        if (!row.TryGetProperty("freshness", out var value) || value.ValueKind != JsonValueKind.String)
            throw InvalidSnapshot();
        return value.GetString() switch
        {
            "fresh" => BusinessNodeFreshness.Fresh,
            "stale" => BusinessNodeFreshness.Stale,
            "unknown" => BusinessNodeFreshness.Unknown,
            _ => throw InvalidSnapshot()
        };
    }

    private static InvalidDataException InvalidSnapshot() => new("Invalid node metadata snapshot.");
}

internal sealed class BusinessNodeMetadataClient : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;

    internal BusinessNodeMetadataClient(HttpClient? http = null) : this(http, TimeSpan.FromSeconds(15)) { }

    // A shorter deadline is useful to exercise actual cancellation without a slow network test.
    internal BusinessNodeMetadataClient(HttpClient? http, TimeSpan requestTimeout)
    {
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
        _ownsClient = http == null;
        _http = http ?? new HttpClient(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    internal async Task<BusinessNodeSnapshot> FetchAsync(Uri trustedEndpoint, CancellationToken ct)
    {
        if (!trustedEndpoint.IsAbsoluteUri || trustedEndpoint.Scheme != Uri.UriSchemeHttps ||
            trustedEndpoint.UserInfo.Length != 0 || trustedEndpoint.Fragment.Length != 0 ||
            !BusinessNodeSnapshot.TryNormalizeHost(trustedEndpoint.IdnHost, out _))
            throw new InvalidDataException("Node metadata service must use HTTPS.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, trustedEndpoint);
            request.Headers.UserAgent.ParseAdd("VPNRouterBusiness");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                (response.RequestMessage?.RequestUri is { } receivedUri && receivedUri != trustedEndpoint))
                throw new IOException("Node metadata not received.");
            if (response.Content.Headers.ContentLength > BusinessNodeSnapshot.MaximumBytes)
                throw new IOException("Node metadata response too large.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(chunk.AsMemory(0,
                       (int)Math.Min(chunk.Length, BusinessNodeSnapshot.MaximumBytes - buffer.Length + 1)), timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > BusinessNodeSnapshot.MaximumBytes)
                    throw new IOException("Node metadata response too large.");
                buffer.Write(chunk, 0, read);
            }
            timeout.Token.ThrowIfCancellationRequested();
            return BusinessNodeSnapshot.Parse(StrictUtf8.GetString(buffer.ToArray()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Node metadata request cancelled.", ct);
        }
        catch (OperationCanceledException)
        {
            throw new IOException("Node metadata request timed out.");
        }
        catch (Exception e) when (e is HttpRequestException || e is IOException ||
                                  e is InvalidDataException || e is DecoderFallbackException)
        {
            // Never expose HTTP diagnostics, the endpoint, grant token, or response body.
            throw new IOException("Node metadata could not be received.");
        }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
