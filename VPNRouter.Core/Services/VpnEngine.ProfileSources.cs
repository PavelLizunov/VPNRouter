using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    internal static string ResolveCustomConfigPath(AppSettings settings)
    {
        if (settings.App.CustomConfigs?.Count > 0)
        {
            var entry = !string.IsNullOrEmpty(settings.App.ActiveCustomConfig)
                ? settings.App.CustomConfigs
                    .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig)
                    ?? settings.App.CustomConfigs[0]
                : settings.App.CustomConfigs[0];

            var path = Environment.ExpandEnvironmentVariables(entry.Path);
            if (File.Exists(path))
                return path;

            var pdPath = CustomConfigInjector.GetProgramDataPath(entry.Name);
            if (File.Exists(pdPath))
                return pdPath;
        }

        return Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
    }

    internal static string ComputeTunFingerprint(Models.TunSettings tun)
    {
        var excludes = tun.GetEffectiveRouteExcludeAddress();
        var excludeKey = string.Join(",",
            excludes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal));

        return string.Join("|",
            (tun.InterfaceName ?? "").Trim().ToLowerInvariant(),
            (tun.Ipv4Address ?? "").Trim().ToLowerInvariant(),
            tun.Ipv6Enabled ? "1" : "0",
            tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tun.AutoRoute ? "1" : "0",
            tun.StrictRoute ? "1" : "0",
            excludeKey);
    }

    internal static void MergeUserCustomization(
        ProfileCollection collection,
        AppSettings settings)
    {
        if (settings.CustomGroupApps?.Count > 0)
        {
            foreach (var (groupName, extras) in settings.CustomGroupApps)
            {
                var profile = collection.Profiles.FirstOrDefault(p =>
                    p.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (profile == null) continue;
                foreach (var app in extras ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    if (profile.Processes.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
            }
        }

        if (settings.CustomCategories?.Count > 0)
        {
            foreach (var cat in settings.CustomCategories)
            {
                if (string.IsNullOrWhiteSpace(cat.Name)) continue;
                if (collection.Profiles.Any(p => p.Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var profile = new Profile
                {
                    Name = cat.Name,
                    Description = "User category",
                    DnsMode = "vpn_only",
                    BlockOnVpnFail = false,
                    Processes = new List<ProcessRule>()
                };
                foreach (var app in cat.Apps ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
                collection.Profiles.Add(profile);
            }
        }
    }

    internal static void RemoveExcludedApps(Profile? profile, IReadOnlyList<string>? excludedApps)
    {
        if (profile == null) return;
        if (excludedApps == null || excludedApps.Count == 0) return;

        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in excludedApps)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            excludeSet.Add(StripExeSuffix(raw));
        }
        if (excludeSet.Count == 0) return;

        profile.Processes.RemoveAll(p =>
            p != null && !string.IsNullOrEmpty(p.Name)
            && excludeSet.Contains(StripExeSuffix(p.Name)));
    }

    private static string StripExeSuffix(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    internal static List<IProfileSource> BuildProfileSources(AppSettings settings)
    {
        var sources = new List<IProfileSource>();
        int priority = 10;

        foreach (var src in settings.ProfileSources)
        {
            switch (src.Type?.ToLowerInvariant())
            {
                case "github" when !string.IsNullOrEmpty(src.Url):
                    sources.Add(new GitHubProfileSource(src.Url, priority));
                    break;
                case "local" when !string.IsNullOrEmpty(src.Path):
                    sources.Add(new LocalProfileSource(src.Path, priority + 10));
                    break;
            }
            priority += 10;
        }

        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));

        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));

        var platformProfiles = Path.Combine(AppPaths.ProfilesDir, platformDefaultName);
        if (File.Exists(platformProfiles))
            sources.Add(new LocalProfileSource(platformProfiles, 85));
        var userDefault = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userDefault) && !userDefault.Equals(platformProfiles, StringComparison.Ordinal))
            sources.Add(new LocalProfileSource(userDefault, 83));

        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    internal static List<IProfileSource> BuildBundledOnlyProfileSources()
    {
        var sources = new List<IProfileSource>();
        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));
        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    private static readonly string[] ExpectedV222Groups =
    {
        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
    };

    internal static void QuarantineStaleUserCatalogue(ILogger? logger)
    {
        try
        {
            var userPath = Path.Combine(AppPaths.ProfilesDir, "default.json");
            if (!File.Exists(userPath)) return;

            bool shouldQuarantine = false;
            string reason = "";

            try
            {
                var json = File.ReadAllText(userPath);
                var collection = JsonSerializer.Deserialize(
                    json, Json.AppJsonContext.Default.ProfileCollection);
                if (collection == null || collection.Profiles == null || collection.Profiles.Count == 0)
                {
                    shouldQuarantine = true;
                    reason = "empty or unparseable";
                }
                else
                {
                    var present = new HashSet<string>(
                        collection.Profiles.Select(p => p.Name),
                        StringComparer.OrdinalIgnoreCase);
                    var missing = ExpectedV222Groups.Count(g => !present.Contains(g));
                    if (missing >= 3)
                    {
                        shouldQuarantine = true;
                        reason = $"{missing} of {ExpectedV222Groups.Length} standard groups missing";
                    }
                }
            }
            catch (Exception ex)
            {
                shouldQuarantine = true;
                reason = $"parse error: {ex.Message}";
            }

            if (!shouldQuarantine) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{userPath}.migrated-{stamp}";
            File.Move(userPath, backup);
            logger?.Warning(
                "[VpnEngine] Quarantined stale user catalogue {Path} ({Reason}) → {Backup}. Using bundled defaults.",
                userPath, reason, backup);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[VpnEngine] Could not quarantine stale user catalogue");
        }
    }
}
