using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class ProfileManager
{
    public static readonly JsonSerializerOptions SafeJsonOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    private readonly List<IProfileSource> _sources;
    private readonly ILogger _logger;
    private ProfileCollection? _cache;

    public ProfileCollection? Loaded => _cache;

    public ProfileManager(List<IProfileSource> sources, ILogger? logger = null)
    {
        _sources = sources.OrderBy(s => s.Priority).ToList();
        _logger = logger ?? Log.Logger;
    }

    public async Task<ProfileCollection> LoadAsync(CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            if (!source.IsAvailable()) continue;

            try
            {
                var collection = await source.LoadAsync(ct);
                if (collection != null && collection.Profiles.Count > 0)
                {
                    _logger.Information("[ProfileManager] Loaded {Count} profiles from {Source}",
                        collection.Profiles.Count, source.SourceName);
                    _cache = collection;
                    return collection;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[ProfileManager] Source '{Source}' exception", source.SourceName);
                _logger.Information("[ProfileManager] Source '{Source}' unavailable: {Reason} — trying next",
                    source.SourceName, ex.Message);
            }
        }

        _logger.Warning("[ProfileManager] All sources failed — using built-in fallback");
        _cache = BuiltInProfiles.Get();
        return _cache;
    }

    public Profile GetProfile(string name)
    {
        if (_cache == null)
            throw new InvalidOperationException("Profiles not loaded. Call LoadAsync first.");

        var trimmed = name?.Trim() ?? string.Empty;
        var profile = _cache.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
            throw new KeyNotFoundException($"Profile '{name}' not found. Available: {string.Join(", ", _cache.Profiles.Select(p => p.Name))}");

        return profile;
    }

    public Profile? TryGetProfile(string name)
    {
        if (_cache == null) return null;

        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        return _cache.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public Profile? MergeProfilesTolerant(IEnumerable<string> names, out List<string> missing)
    {
        missing = new List<string>();
        var resolved = new List<Profile>();
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var p = TryGetProfile(n);
            if (p != null)
                resolved.Add(p);
            else
                missing.Add(n);
        }

        if (missing.Count > 0)
        {
            _logger.Warning(
                "[ProfileManager] {Count} profile(s) not found — skipping: {Missing}. Available: {Available}",
                missing.Count,
                string.Join(", ", missing),
                string.Join(", ", _cache?.Profiles.Select(p => p.Name) ?? Enumerable.Empty<string>()));
        }

        if (resolved.Count == 0)
            return null;

        if (resolved.Count == 1)
            return resolved[0];

        var merged = new Profile
        {
            Name = string.Join("+", resolved.Select(p => p.Name)),
            Description = $"Merged: {string.Join(", ", resolved.Select(p => p.Name))}",
            Processes = resolved.SelectMany(p => p.Processes).ToList(),
            DnsMode = ResolveDnsMode(resolved.Select(p => p.DnsMode)),
            BlockOnVpnFail = resolved.Any(p => p.BlockOnVpnFail)
        };
        _logger.Information(
            "[ProfileManager] Merged {Count} profiles (tolerant) → '{Name}' with {Proc} process rules",
            resolved.Count, merged.Name, merged.Processes.Count);
        return merged;
    }

    public Profile MergeProfiles(IEnumerable<string> names)
    {
        var profiles = names.Select(GetProfile).ToList();

        if (profiles.Count == 1)
            return profiles[0];

        var merged = new Profile
        {
            Name = string.Join("+", profiles.Select(p => p.Name)),
            Description = $"Merged: {string.Join(", ", profiles.Select(p => p.Name))}",
            Processes = profiles.SelectMany(p => p.Processes).ToList(),
            DnsMode = ResolveDnsMode(profiles.Select(p => p.DnsMode)),
            BlockOnVpnFail = profiles.Any(p => p.BlockOnVpnFail)
        };

        _logger.Information("[ProfileManager] Merged {Count} profiles → '{Name}' with {Proc} process rules",
            profiles.Count, merged.Name, merged.Processes.Count);

        return merged;
    }

    public List<Profile> ListProfiles() => _cache?.Profiles ?? new List<Profile>();

    private static string ResolveDnsMode(IEnumerable<string> modes)
    {
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["vpn_only"] = 3,
            ["smart"]    = 2,
            ["direct"]   = 1
        };

        return modes
            .OrderByDescending(m => priority.GetValueOrDefault(m, 0))
            .First();
    }
}

public class LocalProfileSource : IProfileSource
{
    private readonly string _path;
    public int Priority { get; }
    public string SourceName => $"Local({_path})";

    public LocalProfileSource(string path, int priority = 20)
    {
        _path = Environment.ExpandEnvironmentVariables(path);
        Priority = priority;
    }

    public bool IsAvailable() => File.Exists(_path);

    public Task<ProfileCollection?> LoadAsync(CancellationToken ct = default)
    {
        var json = File.ReadAllText(_path);
        var result = JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.ProfileCollection);
        return Task.FromResult(result);
    }
}

public sealed class ProfileCacheFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = GitHubProfileSource.CurrentSchemaVersion;

    [JsonPropertyName("cached_at")]
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("upstream_url")]
    public string UpstreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("profiles")]
    public ProfileCollection Profiles { get; set; } = new();
}

public class GitHubProfileSource : IProfileSource
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _url;
    private readonly string _cacheDir;
    private readonly IHttpClient _http;

    public int Priority { get; }
    public string SourceName => $"GitHub({_url})";

    public GitHubProfileSource(string url, int priority = 10, IHttpClient? http = null)
    {
        _url = url;
        Priority = priority;
        _cacheDir = AppPaths.CacheDir;
        _http = http ?? PolicyHttpClient.Shared;
    }

    public bool IsAvailable()
    {
        return true;
    }

    public async Task<ProfileCollection?> LoadAsync(CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(_cacheDir, "profiles.json");

        try
        {
            var httpResponse = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(_url), Timeout: TimeSpan.FromSeconds(10)),
                ct);
            if (!httpResponse.IsSuccess())
                throw new HttpRequestException($"GitHub profile fetch returned HTTP {httpResponse.StatusCode}");
            var json = httpResponse.AsString();
            var result = JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.ProfileCollection);

            if (result != null)
            {
                Directory.CreateDirectory(_cacheDir);
                var wrapper = new ProfileCacheFile
                {
                    SchemaVersion = CurrentSchemaVersion,
                    CachedAt = DateTime.UtcNow,
                    UpstreamUrl = _url,
                    Profiles = result,
                };
                var wrapperJson = JsonSerializer.Serialize(wrapper, Json.AppJsonContext.Default.ProfileCacheFile);
                await File.WriteAllTextAsync(cacheFile, wrapperJson, ct);
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var loaded = CacheRecovery.LoadOrRecover<ProfileCacheFile>(
                cacheFile,
                CurrentSchemaVersion,
                json => JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.ProfileCacheFile),
                wrap => wrap.Profiles is not null,
                Log.Logger);

            if (loaded.Loaded && loaded.Value!.Profiles.Profiles.Count > 0)
            {
                Log.Logger.Information(
                    "[GitHubProfileSource] HTTP failed ({Err}); using offline cache from {When:O}",
                    ex.Message, loaded.Value.CachedAt);
                return loaded.Value.Profiles;
            }

            throw;
        }
    }
}

public class BuiltInProfileSource : IProfileSource
{
    public int Priority => 99;
    public string SourceName => "Built-in";
    public bool IsAvailable() => true;
    public Task<ProfileCollection?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult<ProfileCollection?>(BuiltInProfiles.Get());
}

public static class BuiltInProfiles
{
    public static ProfileCollection Get() => new()
    {
        Profiles = new List<Profile>
        {
            new()
            {
                Name = "Discord_Privacy",
                Description = "Discord через VPN",
                Processes = new List<ProcessRule>
                {
                    new() { Name = "Discord.exe", IncludeChildren = true,
                        ScanPatterns = new[] { "Discord*.exe", "DiscordUpdate.exe" } }
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = true
            },
            new()
            {
                Name = "Work_Suite",
                Description = "Рабочие приложения",
                Processes = new List<ProcessRule>
                {
                    new() { Name = "Telegram.exe", IncludeChildren = true,
                        ScanPatterns = new[] { "Telegram*.exe" } },
                    new() { Name = "claude.exe", IncludeChildren = true,
                        ScanPatterns = new[] { "claude*.exe", "Claude*.exe" } }
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = true
            },
            new()
            {
                Name = "Browsers",
                Description = "Все браузеры",
                Processes = new List<ProcessRule>
                {
                    new() { Name = "chrome.exe",              IncludeChildren = true, ScanPatterns = new[] { "chrome.exe" } },
                    new() { Name = "msedge.exe",              IncludeChildren = true, ScanPatterns = new[] { "msedge.exe" } },
                    new() { Name = "firefox.exe",             IncludeChildren = true, ScanPatterns = new[] { "firefox.exe" } },
                    new() { Name = "brave.exe",               IncludeChildren = true, ScanPatterns = new[] { "brave.exe" } },
                    new() { Name = "opera.exe",               IncludeChildren = true, ScanPatterns = new[] { "opera.exe", "opera_autoupdate.exe" } },
                    new() { Name = "vivaldi.exe",             IncludeChildren = true, ScanPatterns = new[] { "vivaldi.exe" } },
                    new() { Name = "yandex.exe",              IncludeChildren = true, ScanPatterns = new[] { "yandex.exe", "browser.exe" } },
                    new() { Name = "tor.exe",                 IncludeChildren = true, ScanPatterns = new[] { "tor.exe", "firefox.exe" } },
                    new() { Name = "waterfox.exe",            IncludeChildren = true, ScanPatterns = new[] { "waterfox.exe" } },
                    new() { Name = "librewolf.exe",           IncludeChildren = true, ScanPatterns = new[] { "librewolf.exe" } },
                    new() { Name = "floorp.exe",              IncludeChildren = true, ScanPatterns = new[] { "floorp.exe" } },
                    new() { Name = "ungoogled-chromium.exe",  IncludeChildren = true, ScanPatterns = new[] { "ungoogled-chromium.exe", "chromium.exe" } },
                    new() { Name = "arc.exe",                 IncludeChildren = true, ScanPatterns = new[] { "arc.exe" } },
                    new() { Name = "maxthon.exe",             IncludeChildren = true, ScanPatterns = new[] { "maxthon.exe", "Maxthon.exe" } },
                    new() { Name = "seamonkey.exe",           IncludeChildren = true, ScanPatterns = new[] { "seamonkey.exe" } },
                    new() { Name = "palemoon.exe",            IncludeChildren = true, ScanPatterns = new[] { "palemoon.exe" } },
                    new() { Name = "basilisk.exe",            IncludeChildren = true, ScanPatterns = new[] { "basilisk.exe" } },
                    new() { Name = "iridium.exe",             IncludeChildren = true, ScanPatterns = new[] { "iridium.exe" } },
                    new() { Name = "iron.exe",                IncludeChildren = true, ScanPatterns = new[] { "iron.exe" } },
                    new() { Name = "cent_browser.exe",        IncludeChildren = true, ScanPatterns = new[] { "cent_browser.exe" } },
                    new() { Name = "thorium.exe",             IncludeChildren = true, ScanPatterns = new[] { "thorium.exe" } },
                    new() { Name = "whale.exe",               IncludeChildren = true, ScanPatterns = new[] { "whale.exe" } },
                    new() { Name = "zen.exe",                 IncludeChildren = true, ScanPatterns = new[] { "zen.exe" } }
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false
            }
        }
    };
}
