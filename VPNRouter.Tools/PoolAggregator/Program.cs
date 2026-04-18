using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

// VPNRouter — Free Configs Pool Aggregator
// Runs in GitHub Actions every 6 hours. Fetches 14 public VLESS sources,
// parses + dedups, enriches with GeoIP, writes pool.json metadata file.
// NO validation (TCP/TLS/HTTP) — that happens client-side in user's network.

var output = "/tmp/pool.json";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--output") output = args[i + 1];
}

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

logger.Information("PoolAggregator starting. Output: {path}", output);

var fetcher = new FreeConfigFetcher(logger);
var geoIp = new FreeConfigGeoIp(logger);

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
            var vless = VlessUriParser.Parse(raw);
            var id = BuildId(vless.Server, vless.Port, vless.Uuid);
            if (byId.ContainsKey(id)) continue;

            byId[id] = new PoolEntry
            {
                Id = id,
                Host = vless.Server,
                Port = vless.Port,
                Uuid = vless.Uuid,
                Sni = vless.Reality?.ServerName ?? vless.Tls?.ServerName ?? "",
                Transport = vless.Transport?.Type ?? "tcp",
                Security = vless.Security ?? "reality",
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
    Host = e.Host,
    Port = e.Port,
    Uuid = e.Uuid,
    RawUri = e.Raw,
}).ToList();

logger.Information("Resolving GeoIP for {n} IPs...", geoEntries.Count);
geoIp.Progress = new Progress<(string stage, int done, int total)>(p =>
{
    if (p.done % 500 == 0 || p.done == p.total)
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
    Version = 1,
    SourceCount = sources.Count,
    TotalConfigs = entries.Count,
    Servers = entries,
};

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
};

await File.WriteAllTextAsync(output, JsonSerializer.Serialize(pool, jsonOpts));

var size = new FileInfo(output).Length;
logger.Information("Wrote {count} entries to {path} ({sizeKb} KB)", entries.Count, output, size / 1024);

// ─── Sanity check ───────────────────────────────────────────────────────────
if (entries.Count < 1000)
{
    logger.Warning("Pool has only {n} entries (expected > 10000). Sources may be broken.", entries.Count);
    Environment.ExitCode = 2; // warning exit code — CI can check
}

static string BuildId(string host, int port, string uuid)
{
    using var sha = SHA1.Create();
    var key = $"{host.ToLowerInvariant()}:{port}:{uuid.ToLowerInvariant()}";
    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(hash, 0, 8);
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
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Uuid { get; set; } = string.Empty;
    public string Sni { get; set; } = string.Empty;
    public string Transport { get; set; } = "tcp";
    public string Security { get; set; } = "reality";
    public string? Country { get; set; }
    public string? ResolvedIp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Raw { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
}
