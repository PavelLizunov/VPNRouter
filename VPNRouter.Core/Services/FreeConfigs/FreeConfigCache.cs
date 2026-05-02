using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Persists aggregated free configs to JSON file in %ProgramData%\VPNRouter\cache\free_configs.json.
/// Survives restarts so user doesn't wait for full re-scan every launch.
/// </summary>
public sealed class FreeConfigCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public FreeConfigCache(ILogger logger)
    {
        _logger = logger;
        _path = Path.Combine(AppPaths.CacheDir, "free_configs.json");
    }

    /// <summary>Cache file path (for diagnostics / Open Folder).</summary>
    public string FilePath => _path;

    public sealed class CacheFile
    {
        public DateTime LastAggregatedAt { get; set; } = DateTime.MinValue;
        public List<FreeConfigEntry> Configs { get; set; } = new();
    }

    /// <summary>
    /// Loads cached configs. Returns empty file object if missing or unreadable.
    /// </summary>
    public CacheFile Load()
    {
        try
        {
            if (!File.Exists(_path)) return new CacheFile();
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions);
            if (file == null) return new CacheFile();
            HealCorruptedSubThresholdLatencies(file);
            return file;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigCache: load failed: {err}", ex.Message);
            return new CacheFile();
        }
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
    /// Save cache atomically (write to .tmp, then rename).
    /// </summary>
    public void Save(CacheFile file)
    {
        try
        {
            AppPaths.EnsureDirectories();
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
}
