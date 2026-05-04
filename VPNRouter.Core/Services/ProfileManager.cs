using Newtonsoft.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Loads profiles from ordered sources: GitHub → Local → Built-in fallback.
/// Supports merging multiple profiles by name.
/// </summary>
public class ProfileManager
{
    /// <summary>v2.31.0-r1 (CO-4 audit fix): cap JSON nesting depth on
    /// untrusted profile sources (GitHub URLs, local user-supplied files)
    /// to prevent stack-overflow / DoS via deeply-nested arrays. Profiles
    /// are flat objects with at most ~3 levels (collection→profile→
    /// processes[]→rule); 32 leaves enormous head-room while neutralizing
    /// adversarial input. Per Newtonsoft Json.NET MaxDepth guidance.</summary>
    internal static readonly JsonSerializerSettings SafeJsonSettings = new()
    {
        MaxDepth = 32,
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

    // ─── Load ─────────────────────────────────────────────────────────────────

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
                // v2.31.6-r19: profile sources are tried in order with built-in
                // fallback as last resort. The vast majority of "failures" here
                // are 404s from optional remote sources (e.g. an example
                // GitHub URL the user never set up). Logging the full stack
                // every reload spammed vpnrouter.log with stack traces. Keep
                // a concise INFO for the common case; raw exception goes to
                // DEBUG so diagnostics can opt in.
                _logger.Debug(ex, "[ProfileManager] Source '{Source}' exception", source.SourceName);
                _logger.Information("[ProfileManager] Source '{Source}' unavailable: {Reason} — trying next",
                    source.SourceName, ex.Message);
            }
        }

        _logger.Warning("[ProfileManager] All sources failed — using built-in fallback");
        _cache = BuiltInProfiles.Get();
        return _cache;
    }

    // ─── Get / Merge ──────────────────────────────────────────────────────────

    public Profile GetProfile(string name)
    {
        if (_cache == null)
            throw new InvalidOperationException("Profiles not loaded. Call LoadAsync first.");

        // Trim whitespace so CLI users typing `--profile "  Foo  "` or config
        // files with accidental padding around names still resolve correctly.
        var trimmed = name?.Trim() ?? string.Empty;
        var profile = _cache.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
            throw new KeyNotFoundException($"Profile '{name}' not found. Available: {string.Join(", ", _cache.Profiles.Select(p => p.Name))}");

        return profile;
    }

    /// <summary>
    /// Returns the named profile or null if it doesn't exist. Never throws.
    /// Use this when the caller wants to log-and-skip missing names rather
    /// than abort the whole operation.
    /// </summary>
    public Profile? TryGetProfile(string name)
    {
        if (_cache == null) return null;

        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        return _cache.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tolerant variant of <see cref="MergeProfiles"/>. Resolves each name;
    /// unknown names are logged and returned via <paramref name="missing"/>
    /// but do not throw. Returns null if ALL names were missing.
    /// </summary>
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

    /// <summary>
    /// Merges multiple profiles into one. Conflict resolution:
    /// - processes: union of all
    /// - dns_mode: strictest wins (vpn_only > smart > direct)
    /// - block_on_vpn_fail: true wins over false
    /// </summary>
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

    // ─── Private ──────────────────────────────────────────────────────────────

    private static string ResolveDnsMode(IEnumerable<string> modes)
    {
        // Strictness order: vpn_only > smart > direct
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

// ─── Profile Sources ──────────────────────────────────────────────────────────

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
        // v2.31.0-r1 (CO-4): MaxDepth-capped deserialization on local files
        // — user could place a malicious profiles.json that crashes the
        // app or causes stack overflow via nested arrays.
        var result = JsonConvert.DeserializeObject<ProfileCollection>(json, ProfileManager.SafeJsonSettings);
        return Task.FromResult(result);
    }
}

public class GitHubProfileSource : IProfileSource
{
    private readonly string _url;
    private readonly string _cacheDir;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public int Priority { get; }
    public string SourceName => $"GitHub({_url})";

    public GitHubProfileSource(string url, int priority = 10)
    {
        _url = url;
        Priority = priority;
        _cacheDir = AppPaths.CacheDir;
    }

    public bool IsAvailable()
    {
        // Quick connectivity check — assume available, fail gracefully in LoadAsync
        return true;
    }

    public async Task<ProfileCollection?> LoadAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(_url, ct);
        // v2.31.0-r1 (CO-4): MaxDepth-capped deserialization on the GitHub
        // profile URL — the channel is HTTPS but a compromised tap or
        // typosquatted URL could feed adversarial JSON. ProfileCollection
        // is shallow (~3 levels), 32 leaves enormous head-room.
        var result = JsonConvert.DeserializeObject<ProfileCollection>(json, ProfileManager.SafeJsonSettings);

        // Cache to disk for offline fallback
        if (result != null)
        {
            Directory.CreateDirectory(_cacheDir);
            var cacheFile = Path.Combine(_cacheDir, "profiles.json");
            await File.WriteAllTextAsync(cacheFile, json, ct);
        }

        return result;
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

// ─── Built-in fallback profiles ───────────────────────────────────────────────

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
