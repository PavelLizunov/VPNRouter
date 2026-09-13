using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tools.PoolAggregator;

// VPNRouter — Free Configs Pool Aggregator
// Runs in GitHub Actions every 6 hours. Fetches public sources,
// parses (multi-protocol), dedups via composite SHA-256, enriches with GeoIP (offline MMDB / online),
// and writes pool.json metadata file.
// NO validation (TCP/TLS/HTTP) — that happens client-side in user's network.

var output = "/tmp/pool.json";
string? mmdbPath = null;

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--output") output = args[i + 1];
    if (args[i] == "--mmdb") mmdbPath = args[i + 1];
}

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

logger.Information("PoolAggregator starting. Output: {path}", output);

var fetcher = new FreeConfigFetcher(logger);

using var mmdb = !string.IsNullOrEmpty(mmdbPath) && File.Exists(mmdbPath)
    ? new MmdbCountryLookup(mmdbPath)
    : null;

if (mmdb != null)
    logger.Information("Using offline GeoIP database from {path}", mmdbPath);
else
    logger.Information("Offline GeoIP database not found, using online fallback");

var geoIp = new FreeConfigGeoIp(logger, mmdb);

// ─── Stage 1: fetch all sources in parallel ─────────────────────────────────
var sources = FreeConfigSources.Default.Where(s => s.Enabled).ToList();
logger.Information("Fetching {n} sources...", sources.Count);

var fetchResults = await Task.WhenAll(sources.Select(async s =>
{
    var raws = await fetcher.FetchAsync(s);
    logger.Information("  {name}: {count} URIs", s.Name, raws.Count);
    return (source: s, raws);
}));

// ─── Stage 2: parse + dedup ─────────────────────────────────────────────────
var byId = new Dictionary<string, PoolEntry>(StringComparer.OrdinalIgnoreCase);
var parseErrors = 0;

foreach (var (src, raws) in fetchResults)
{
    foreach (var raw in raws)
    {
        try
        {
            var entry = ServerUriParser.Parse(raw);
            var id = BuildDeduplicationId(entry);
            if (byId.ContainsKey(id)) continue;

            var authKey = !string.IsNullOrEmpty(entry.Uuid) ? entry.Uuid
                        : !string.IsNullOrEmpty(entry.Password) ? entry.Password
                        : entry.Awg?.PeerPublicKey ?? string.Empty;
            var transport = entry.Transport?.Type ?? "tcp";
            var sni = entry.Reality?.ServerName ?? entry.Tls?.ServerName ?? string.Empty;
            var path = entry.Transport?.Path ?? string.Empty;
            var protocol = !string.IsNullOrEmpty(entry.Protocol) ? entry.Protocol : "vless";
            var security = entry.Security ?? (entry.Reality != null ? "reality" : (entry.Tls != null ? "tls" : "none"));

            byId[id] = new PoolEntry
            {
                Id = id,
                Protocol = protocol,
                Host = entry.Server,
                Port = entry.Port,
                Uuid = authKey,
                Sni = sni,
                Transport = transport,
                Security = security,
                Path = path,
                Source = src.Url,
                Raw = raw,
                FirstSeen = DateTime.UtcNow,
            };
        }
        catch { parseErrors++; }
    }
}

logger.Information("Parsed {ok} unique entries ({err} parse errors)", byId.Count, parseErrors);

var entries = byId.Values.ToList();

// ─── Stage 3: GeoIP enrich ──────────────────────────────────────────────────
// Build temporary FreeConfigEntry list to use existing GeoIp service, then
// copy back country + resolved_ip into PoolEntry.
var geoEntries = entries.Select(e => new FreeConfigEntry
{
    Id = e.Id,
    Protocol = e.Protocol,
    Host = e.Host,
    Port = e.Port,
    Uuid = e.Uuid,
    RawUri = e.Raw,
}).ToList();

logger.Information("Resolving GeoIP for {n} IPs...", geoEntries.Count);
geoIp.Progress = new Progress<(string stage, int done, int total)>(p =>
{
    if (p.done % 1000 == 0 || p.done == p.total)
        logger.Information("  GeoIP {stage}: {done}/{total}", p.stage, p.done, p.total);
});
await geoIp.EnrichAsync(geoEntries);

var geoById = geoEntries.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
foreach (var e in entries)
{
    if (geoById.TryGetValue(e.Id, out var g))
    {
        e.Country = g.CountryCode;
        e.ResolvedIp = g.ResolvedIp;
    }
}

var withCountry = entries.Count(e => !string.IsNullOrEmpty(e.Country));
logger.Information("GeoIP done: {with}/{total} have country codes", withCountry, entries.Count);

// ─── Stage 4: write pool.json ───────────────────────────────────────────────
var pool = new PoolFile
{
    UpdatedAt = DateTime.UtcNow,
    Version = 2,
    SourceCount = sources.Count,
    TotalConfigs = entries.Count,
    Servers = entries,
};

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = false,
    // camelCase to match client-side FreeConfigPoolFetcher.ParsePool expectations
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

await File.WriteAllTextAsync(output, JsonSerializer.Serialize(pool, jsonOpts));

var size = new FileInfo(output).Length;
logger.Information("Wrote {count} entries to {path} ({sizeKb} KB)", entries.Count, output, size / 1024);

// ─── Sanity check ───────────────────────────────────────────────────────────
if (entries.Count < 1000)
{
    logger.Warning("Pool has only {n} entries (expected >= 1000). Sources may be degraded.", entries.Count);
    if (entries.Count == 0)
    {
        Environment.ExitCode = 1;
    }
}

static string BuildDeduplicationId(VlessServerEntry entry)
{
    var authKey = !string.IsNullOrEmpty(entry.Uuid) ? entry.Uuid
                : !string.IsNullOrEmpty(entry.Password) ? entry.Password
                : entry.Awg?.PeerPublicKey ?? string.Empty;
    var transport = entry.Transport?.Type ?? "tcp";
    var sni = entry.Reality?.ServerName ?? entry.Tls?.ServerName ?? string.Empty;
    var path = entry.Transport?.Path ?? string.Empty;
    var protocol = !string.IsNullOrEmpty(entry.Protocol) ? entry.Protocol : "vless";

    var composite = $"{protocol.ToLowerInvariant()}|{entry.Server.ToLowerInvariant()}|{entry.Port}|{authKey.ToLowerInvariant()}|{transport.ToLowerInvariant()}|{sni.ToLowerInvariant()}|{path}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
    return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
}

// ─── DTOs ───────────────────────────────────────────────────────────────────

public sealed class PoolFile
{
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
    public int SourceCount { get; set; }
    public int TotalConfigs { get; set; }
    public List<PoolEntry> Servers { get; set; } = new();
}

public sealed class PoolEntry
{
    public string Id { get; set; } = string.Empty;
    public string Protocol { get; set; } = "vless";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Uuid { get; set; } = string.Empty;
    public string Sni { get; set; } = string.Empty;
    public string Transport { get; set; } = "tcp";
    public string Security { get; set; } = "reality";
    public string? Path { get; set; }
    public string? Country { get; set; }
    public string? ResolvedIp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Raw { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
}
