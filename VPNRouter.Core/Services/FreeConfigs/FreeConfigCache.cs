using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Persists aggregated free configs to JSON file in %ProgramData%\VPNRouter\cache\free_configs.json.
/// Survives restarts so user doesn't wait for full re-scan every launch.
/// </summary>
public sealed class FreeConfigCache
{
    /// <summary>
    /// v2.32.0 — current cache schema version. Bumped whenever the on-disk
    /// shape changes in a non-backward-compatible way. Older files are
    /// quarantined and rebuilt by <see cref="CacheRecovery"/>.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public FreeConfigCache(ILogger logger)
        : this(logger, Path.Combine(AppPaths.CacheDir, "free_configs.json"))
    {
    }

    /// <summary>v2.32.0 — explicit-path constructor for unit tests so we
    /// can run hermetically against a temp dir.</summary>
    internal FreeConfigCache(ILogger logger, string filePath)
    {
        _logger = logger;
        _path = filePath;
    }

    /// <summary>Cache file path (for diagnostics / Open Folder).</summary>
    public string FilePath => _path;

    public sealed class CacheFile
    {
        /// <summary>
        /// v2.32.0 — schema marker for <see cref="CacheRecovery"/>. Defaults
        /// to <see cref="CurrentSchemaVersion"/> on a fresh in-memory file
        /// so the first <see cref="Save"/> writes the current version.
        /// </summary>
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public DateTime LastAggregatedAt { get; set; } = DateTime.MinValue;
        public List<FreeConfigEntry> Configs { get; set; } = new();
    }

    /// <summary>
    /// Loads cached configs. Returns empty file object if missing,
    /// schema-mismatched, or unreadable. Corrupt files are quarantined
    /// as <c>free_configs.json.corrupt-{timestamp}</c> by
    /// <see cref="CacheRecovery"/> for post-mortem.
    /// </summary>
    public CacheFile Load()
    {
        var result = CacheRecovery.LoadOrRecover<CacheFile>(
            _path,
            CurrentSchemaVersion,
            json => JsonSerializer.Deserialize<CacheFile>(json, JsonOptions),
            cf => cf.Configs is not null,
            _logger);

        if (result.Loaded)
        {
            HealCorruptedSubThresholdLatencies(result.Value!);
            return result.Value!;
        }

        // NotFound is the clean first-launch path — keep it quiet.
        // ShouldRebuild covers the four corruption reasons; the helper
        // already logged the warning + quarantined the file. Returning
        // an empty CacheFile signals "no cached configs" to the
        // aggregator, which then triggers a fresh fetch on next refresh.
        return new CacheFile();
    }

    /// <summary>
    /// v2.31.3-r1 (F-25 follow-up): heal old cache entries that picked up
    /// implausibly low TCP-ping latency from the pre-v2.31.2 Recheck flow.
    /// Recheck used to skip the <c>ImplausibleThresholdMs=5</c> gate and
    /// silently overwrote Verified <c>LatencyMs</c> with sub-1 ms readings
    /// (cached route + ARP made <c>TcpClient.ConnectAsync</c> return faster
    /// than the physical floor of internet RTT). v2.31.2 fixed the new-write
    /// path; this migration heals old corrupted entries on load by resetting
    /// any sub-threshold <c>LatencyMs</c> to 0 — the UI renders 0 as "—",
    /// signalling "needs re-verify" rather than displaying the bogus value.
    /// </summary>
    private static void HealCorruptedSubThresholdLatencies(CacheFile file)
    {
        const int ImplausibleThresholdMs = 5;
        foreach (var entry in file.Configs)
        {
            if (entry.LatencyMs > 0 && entry.LatencyMs < ImplausibleThresholdMs)
            {
                entry.LatencyMs = 0;
            }
        }
    }

    /// <summary>
    /// Save cache atomically (write to .tmp, then rename). Always stamps
    /// <see cref="CacheFile.SchemaVersion"/> with the current value so a
    /// future load can detect schema drift.
    /// </summary>
    public void Save(CacheFile file)
    {
        try
        {
            // Stamp the current schema on every write — defends against
            // callers that constructed the object externally and never
            // touched the property.
            file.SchemaVersion = CurrentSchemaVersion;
            EnsureCacheDir();
            var tmp = _path + ".tmp";
            var json = JsonSerializer.Serialize(file, JsonOptions);
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigCache: save failed: {err}", ex.Message);
        }
    }

    /// <summary>
    /// Ensures the parent directory of the cache file exists. The default
    /// constructor uses <see cref="AppPaths.CacheDir"/>, but tests inject
    /// arbitrary paths — so we create the parent of <see cref="_path"/>
    /// directly rather than calling <see cref="AppPaths.EnsureDirectories"/>.
    /// </summary>
    private void EnsureCacheDir()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
