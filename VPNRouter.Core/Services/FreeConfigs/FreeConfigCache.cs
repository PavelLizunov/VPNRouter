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
            return file ?? new CacheFile();
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigCache: load failed: {err}", ex.Message);
            return new CacheFile();
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
