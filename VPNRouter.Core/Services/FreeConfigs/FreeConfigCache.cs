using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

public sealed class FreeConfigCache
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public FreeConfigCache(ILogger logger)
        : this(logger, Path.Combine(AppPaths.CacheDir, "free_configs.json"))
    {
    }

    public FreeConfigCache(ILogger logger, string filePath)
    {
        _logger = logger;
        _path = filePath;
    }

    public string FilePath => _path;

    public sealed class CacheFile
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public DateTime LastAggregatedAt { get; set; } = DateTime.MinValue;
        public List<FreeConfigEntry> Configs { get; set; } = new();
    }

    public CacheFile Load()
    {
        _ioLock.Wait();
        try
        {
            return LoadInternal();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<CacheFile> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new CacheFile();

            try
            {
                await using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 8192,
                    useAsync: true);

                var file = await JsonSerializer.DeserializeAsync(
                    stream,
                    VPNRouter.Core.Json.AppJsonContext.Default.CacheFile,
                    ct).ConfigureAwait(false);

                if (file != null && file.Configs != null)
                {
                    HealCorruptedSubThresholdLatencies(file);
                    return file;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("FreeConfigCache: async stream load failed, falling back to recovery: {err}", ex.Message);
            }

            return LoadInternal();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private CacheFile LoadInternal()
    {
        var result = CacheRecovery.LoadOrRecover<CacheFile>(
            _path,
            CurrentSchemaVersion,
            json => JsonSerializer.Deserialize(json, VPNRouter.Core.Json.AppJsonContext.Default.CacheFile),
            cf => cf.Configs is not null,
            _logger);

        if (result.Loaded)
        {
            HealCorruptedSubThresholdLatencies(result.Value!);
            return result.Value!;
        }

        return new CacheFile();
    }

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

    public void Save(CacheFile file)
    {
        _ioLock.Wait();
        try
        {
            file.SchemaVersion = CurrentSchemaVersion;
            EnsureCacheDir();
            var tmp = $"{_path}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(
                tmp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192))
            {
                JsonSerializer.Serialize(stream, file, VPNRouter.Core.Json.AppJsonContext.Default.CacheFile);
                stream.Flush();
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigCache: save failed: {err}", ex.Message);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private void EnsureCacheDir()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
