using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class ProfileFeature
{
    private readonly ConfigStorage _storage;
    private readonly ILogger _logger;
    private ProfileManager? _profileManager;

    public ProfileFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
    }

    public Task<object> ListAsync(JsonElement parameters, CancellationToken ct)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        // Protocol contract invariant: list calls must not initiate remote network requests.
        var collection = _profileManager?.Loaded ?? OfflineProfileHelper.GetOfflineProfileCollection(_storage.GetSettings(), _storage.DataDir, _logger);

        var settings = _storage.GetSettings();
        var activeProfiles = (settings.ActiveProfile ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var activeSet = new HashSet<string>(activeProfiles, StringComparer.OrdinalIgnoreCase);

        var items = collection.Profiles
            .Select(p => new
            {
                id = p.Name,
                name = p.Name,
                selected = activeSet.Contains(p.Name)
            })
            .ToList();

        return Task.FromResult<object>(new { items });
    }

    public Task SelectAsync(JsonElement parameters, CancellationToken ct)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "ids");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("ids", out var idsProp) || idsProp.ValueKind != JsonValueKind.Array)
            throw new RouterException("invalid_argument", "Missing ids parameter (expected array of profile names)");

        var revision = revProp.GetString()!;
        _storage.ValidateRevision(revision);

        var collection = _profileManager?.Loaded ?? OfflineProfileHelper.GetOfflineProfileCollection(_storage.GetSettings(), _storage.DataDir, _logger);
        var availableNames = new HashSet<string>(collection.Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var selected = new List<string>();
        int count = 0;
        foreach (var item in idsProp.EnumerateArray())
        {
            count++;
            if (count > 100)
                throw new RouterException("invalid_argument", "Too many profile IDs (maximum 100)");

            if (item.ValueKind != JsonValueKind.String)
                throw new RouterException("invalid_argument", "Profile IDs must be strings");

            var name = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (name.Length > 256)
                throw new RouterException("invalid_argument", "Profile ID exceeds 256 characters");

            if (!availableNames.Contains(name))
                throw new RouterException("not_found", "Profile not found");

            if (!selected.Contains(name, StringComparer.OrdinalIgnoreCase))
                selected.Add(name);
        }

        var settings = _storage.GetSettings();
        settings.ActiveProfile = string.Join(",", selected);

        _storage.SaveSettings(settings, revision);
        _logger.Information("[ProfileFeature] Selected profiles: {Profiles}", settings.ActiveProfile);
        return Task.CompletedTask;
    }

    public async Task<object> RefreshAsync(JsonElement parameters, CancellationToken ct)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var manager = GetOrCreateProfileManager();
        var collection = await manager.LoadAsync(ct);
        _logger.Information("[ProfileFeature] Refreshed profiles: {Count} available", collection.Profiles.Count);
        return new { total = collection.Profiles.Count };
    }

    private ProfileManager GetOrCreateProfileManager()
    {
        if (_profileManager != null)
            return _profileManager;

        var settings = _storage.GetSettings();
        var sources = VpnEngine.BuildProfileSources(settings, _storage.DataDir);
        _profileManager = new ProfileManager(sources, _logger);
        return _profileManager;
    }
}

internal sealed class MemoryProfileSource : IProfileSource
{
    private readonly ProfileCollection _collection;
    public int Priority { get; }
    public string SourceName { get; }

    public MemoryProfileSource(ProfileCollection collection, int priority, string sourceName = "MemoryProfileCache")
    {
        _collection = collection;
        Priority = priority;
        SourceName = sourceName;
    }

    public bool IsAvailable() => true;
    public Task<ProfileCollection?> LoadAsync(CancellationToken ct = default) => Task.FromResult<ProfileCollection?>(_collection);
}

internal static class OfflineProfileHelper
{
    internal const long MaxProfileFileSize = 1024 * 1024; // 1 MiB

    public static ProfileCollection GetOfflineProfileCollection(AppSettings settings, string? dataDir, ILogger? logger = null)
    {
        var manager = CreateOfflineProfileManager(settings, dataDir, logger);
        var loadTask = manager.LoadAsync(CancellationToken.None);
        return loadTask.GetAwaiter().GetResult();
    }

    public static string ResolveLocalProfileDnsMode(AppSettings settings, string? dataDir, ILogger? logger = null)
    {
        var manager = CreateOfflineProfileManager(settings, dataDir, logger);
        var loadTask = manager.LoadAsync(CancellationToken.None);
        loadTask.GetAwaiter().GetResult();

        var activeNames = (settings.ActiveProfile ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (activeNames.Length == 0)
        {
            activeNames = new[] { "default" };
        }

        var merged = manager.MergeProfilesTolerant(activeNames, out _);
        if (merged != null && !string.IsNullOrWhiteSpace(merged.DnsMode))
        {
            return merged.DnsMode.Trim().ToLowerInvariant();
        }

        return "vpn_only";
    }

    internal static ProfileManager CreateOfflineProfileManager(AppSettings settings, string? dataDir, ILogger? logger = null)
    {
        var sources = BuildOfflineProfileSources(settings, dataDir, logger);
        return new ProfileManager(sources, logger);
    }

    internal static List<IProfileSource> BuildOfflineProfileSources(AppSettings settings, string? dataDir, ILogger? logger = null)
    {
        var sources = new List<IProfileSource>();
        int priority = 10;

        var effectiveDataDir = !string.IsNullOrWhiteSpace(dataDir) ? dataDir : AppPaths.DataDir;
        var cacheFile = Path.Combine(effectiveDataDir, "cache", "profiles.json");

        bool cacheAdded = false;

        foreach (var src in settings.ProfileSources ?? new List<ProfileSource>())
        {
            switch (src.Type?.ToLowerInvariant())
            {
                case "github" when !string.IsNullOrEmpty(src.Url):
                    if (!cacheAdded)
                    {
                        var cacheCollection = TryLoadCacheProfileBounded(cacheFile, logger);
                        if (cacheCollection != null)
                        {
                            sources.Add(new MemoryProfileSource(cacheCollection, priority, $"Cache({cacheFile})"));
                            cacheAdded = true;
                        }
                    }
                    break;

                case "local" when !string.IsNullOrEmpty(src.Path):
                    var localCollection = TryLoadLocalProfileBounded(src.Path, logger);
                    if (localCollection != null)
                    {
                        sources.Add(new MemoryProfileSource(localCollection, priority + 10, $"Local({src.Path})"));
                    }
                    break;
            }
            priority += 10;
        }

        if (!cacheAdded)
        {
            var cacheCollection = TryLoadCacheProfileBounded(cacheFile, logger);
            if (cacheCollection != null)
            {
                sources.Add(new MemoryProfileSource(cacheCollection, 30, $"Cache({cacheFile})"));
                cacheAdded = true;
            }
        }

        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        // platformBundled: priority 80
        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
        {
            var bundledCol = TryLoadLocalProfileBounded(platformBundled, logger);
            if (bundledCol != null)
                sources.Add(new MemoryProfileSource(bundledCol, 80, $"Bundled({platformBundled})"));
        }

        // defaultJson: priority 82 (was 78) - lower priority fallback when distinct from platformBundled
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson) && !defaultJson.Equals(platformBundled, StringComparison.Ordinal))
        {
            var defaultCol = TryLoadLocalProfileBounded(defaultJson, logger);
            if (defaultCol != null)
                sources.Add(new MemoryProfileSource(defaultCol, 82, $"Bundled({defaultJson})"));
        }

        // platformProfiles: priority 85
        var profilesDir = Path.Combine(effectiveDataDir, "profiles");
        var platformProfiles = Path.Combine(profilesDir, platformDefaultName);
        if (File.Exists(platformProfiles))
        {
            var platformProfCol = TryLoadLocalProfileBounded(platformProfiles, logger);
            if (platformProfCol != null)
                sources.Add(new MemoryProfileSource(platformProfCol, 85, $"ProfilesDir({platformProfiles})"));
        }

        // userDefault: priority 87 (was 83) - lower priority fallback when distinct from platformProfiles
        var userDefault = Path.Combine(profilesDir, "default.json");
        if (File.Exists(userDefault) && !userDefault.Equals(platformProfiles, StringComparison.Ordinal))
        {
            var userDefaultCol = TryLoadLocalProfileBounded(userDefault, logger);
            if (userDefaultCol != null)
                sources.Add(new MemoryProfileSource(userDefaultCol, 87, $"ProfilesDir({userDefault})"));
        }

        // Built-in fallback: priority 99
        sources.Add(new BuiltInProfileSource());

        return sources;
    }

    private static ProfileCollection? TryLoadLocalProfileBounded(string path, ILogger? logger = null)
    {
        try
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(expandedPath))
                return null;

            var fileInfo = new FileInfo(expandedPath);
            if (fileInfo.Length > MaxProfileFileSize)
            {
                logger?.Warning("[OfflineProfileHelper] Profile file {Path} exceeds 1 MiB limit ({Length} bytes) — skipping", expandedPath, fileInfo.Length);
                return null;
            }

            using var stream = new FileStream(expandedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxProfileFileSize)
            {
                logger?.Warning("[OfflineProfileHelper] Profile stream for {Path} exceeds 1 MiB — skipping", expandedPath);
                return null;
            }

            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != buffer.Length)
                return null;

            var collection = JsonSerializer.Deserialize(buffer, AppJsonContext.Default.ProfileCollection);
            if (collection?.Profiles != null && collection.Profiles.Count > 0)
            {
                return collection;
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[OfflineProfileHelper] Failed to read local profile from {Path}", path);
        }

        return null;
    }

    private static ProfileCollection? TryLoadCacheProfileBounded(string cachePath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var fileInfo = new FileInfo(cachePath);
            if (fileInfo.Length > MaxProfileFileSize)
            {
                logger?.Warning("[OfflineProfileHelper] Cache profile file {Path} exceeds 1 MiB limit ({Length} bytes) — skipping", cachePath, fileInfo.Length);
                return null;
            }

            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaxProfileFileSize)
            {
                logger?.Warning("[OfflineProfileHelper] Cache profile stream for {Path} exceeds 1 MiB — skipping", cachePath);
                return null;
            }

            var buffer = new byte[stream.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != buffer.Length)
                return null;

            var cacheFile = JsonSerializer.Deserialize(buffer, AppJsonContext.Default.ProfileCacheFile);
            if (cacheFile != null &&
                cacheFile.SchemaVersion == GitHubProfileSource.CurrentSchemaVersion &&
                cacheFile.Profiles?.Profiles != null &&
                cacheFile.Profiles.Profiles.Count > 0)
            {
                return cacheFile.Profiles;
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[OfflineProfileHelper] Failed to read cache profile from {Path}", cachePath);
        }

        return null;
    }
}
