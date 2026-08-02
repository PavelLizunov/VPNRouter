namespace VPNRouter.Core.Models;

/// <summary>
/// SR-4 (v2.32.0): post-deserialize null-safety sweep for <see cref="AppSettings"/>.
/// YamlDotNet overwrites C#-level initializers with <c>null</c> when the YAML
/// has an explicit empty value (e.g. <c>vless:</c> with no children, or
/// <c>servers: []</c> later mutated to plain <c>servers:</c> by hand). This
/// extension walks the full object graph and replaces any null sub-section /
/// collection with a fresh empty default so downstream code (ViewModels,
/// engines, validators) never NREs on a partial config.
///
/// <para>Idempotent: only fills nulls, never overwrites populated values.
/// Safe to call any number of times — Parse() runs it after deserialize,
/// Load() runs it on the freshly-defaulted instance returned on failure,
/// callers may run it again after their own mutations without ill effect.</para>
/// </summary>
public static class AppSettingsSane
{
    /// <summary>32-hex-char cryptographically random Clash-API bearer secret.</summary>
    internal static string GenerateClashApiSecret()
        => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>Walk the AppSettings tree and replace every null
    /// sub-object / collection with a fresh empty default.</summary>
    public static AppSettings EnsureSane(this AppSettings? settings)
    {
        // Tolerate null receiver — caller may pass us the result of a
        // failed Deserialize<AppSettings>() which YamlDotNet returns as
        // null for empty / whitespace-only YAML.
        settings ??= new AppSettings();

        settings.App              ??= new AppConfig();
        settings.ProfileSources   ??= new List<ProfileSource>();
        settings.Vless            ??= new VlessConfig();
        settings.Tun              ??= new TunSettings();
        settings.Dns              ??= new DnsSettings();
        settings.SingBox          ??= new SingBoxSettings();
        // P1 clash_api secret (2026-07-10): every loaded settings object MUST
        // carry a non-empty secret so the generated config and this process's
        // Clash-API consumers always agree. SettingsLoader persists it on the
        // happy path; a read-only config dir just means a per-process secret
        // (degrades to stats-only mismatch across App/Service, never a leak).
        if (string.IsNullOrEmpty(settings.SingBox.ClashApiSecret))
            settings.SingBox.ClashApiSecret = GenerateClashApiSecret();
        settings.Monitoring       ??= new MonitoringSettings();
        settings.CustomApps       ??= new List<string>();
        settings.CustomGroupApps  ??= new Dictionary<string, List<string>>();
        settings.CustomCategories ??= new List<CustomCategory>();
        settings.ExcludedApps     ??= new List<string>();
        settings.Update           ??= new UpdateSettings();
        settings.EmergencyChannel ??= new EmergencyChannelSettings();

        // Strip out null entries that YamlDotNet may emit for sequence
        // items written as bare hyphens (e.g. `profile_sources:\n  -`).
        settings.ProfileSources.RemoveAll(p => p == null!);
        settings.CustomApps.RemoveAll(s => s == null!);
        settings.CustomCategories.RemoveAll(c => c == null!);
        settings.ExcludedApps.RemoveAll(s => s == null!);
        foreach (var c in settings.CustomCategories)
            c.Apps ??= new List<string>();

        // Dictionary values: each key may map to null. Replace with empty list.
        foreach (var key in settings.CustomGroupApps.Keys.ToList())
        {
            if (settings.CustomGroupApps[key] == null!)
                settings.CustomGroupApps[key] = new List<string>();
        }

        EnsureSaneApp(settings.App);
        EnsureSaneVless(settings.Vless);
        EnsureSaneTun(settings.Tun);

        return settings;
    }

    private static void EnsureSaneApp(AppConfig app)
    {
        app.CustomConfigs       ??= new List<CustomConfigEntry>();
        app.SubscriptionServers ??= new List<VlessServerEntry>();
        app.Subscriptions       ??= new List<SubscriptionEntry>();
        app.CustomDirectRules   ??= new List<CustomDirectRule>();
        app.CustomRules         ??= new List<CustomRule>();
        app.UserFreeSources     ??= new List<UserFreeSource>();
        app.RoutingAppsInclude  ??= new List<string>();
        app.RoutingAppsExclude  ??= new List<string>();

        app.CustomConfigs.RemoveAll(c => c == null!);
        app.CustomDirectRules.RemoveAll(r => r == null!);
        app.CustomRules.RemoveAll(r => r == null!);
        app.UserFreeSources.RemoveAll(u => u == null!);
        app.RoutingAppsInclude.RemoveAll(s => s == null!);
        app.RoutingAppsExclude.RemoveAll(s => s == null!);

        // AM-1: only "include" / "exclude" are valid. Anything else
        // (null, empty, typo, legacy) falls back to "include" so the
        // engine has a deterministic branch.
        if (!string.Equals(app.RoutingAppsMode, "include", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(app.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
        {
            app.RoutingAppsMode = "include";
        }
        else
        {
            // Canonicalise casing.
            app.RoutingAppsMode = app.RoutingAppsMode!.ToLowerInvariant();
        }

        // v2.40.0-r9 (#3 core-audit): canonicalize RoutingMode the same way. Only
        // "split"/"full" are valid; trim + match case-insensitively, fall back to
        // "split". Without this, a stray-whitespace "full" silently degraded full
        // tunnel to split (everything direct on the real IP) — see the matching
        // SettingsLoader.Parse guard.
        app.RoutingMode =
            string.Equals(app.RoutingMode?.Trim(), "full", StringComparison.OrdinalIgnoreCase)
                ? "full" : "split";

        app.SubscriptionServers.RemoveAll(s => s == null!);
        foreach (var s in app.SubscriptionServers)
            EnsureSaneServerEntry(s);

        app.Subscriptions.RemoveAll(s => s == null!);
        foreach (var sub in app.Subscriptions)
        {
            sub.Servers ??= new List<VlessServerEntry>();
            sub.Servers.RemoveAll(s => s == null!);
            foreach (var srv in sub.Servers)
                EnsureSaneServerEntry(srv);
        }
    }

    private static void EnsureSaneVless(VlessConfig vless)
    {
        vless.Reality   ??= new VlessRealityConfig();
        vless.Tls       ??= new VlessTlsConfig();
        vless.Transport ??= new VlessTransportConfig();
        vless.Servers   ??= new List<VlessServerEntry>();

        vless.Transport.Headers ??= new Dictionary<string, string>();

        vless.Servers.RemoveAll(s => s == null!);
        foreach (var s in vless.Servers)
            EnsureSaneServerEntry(s);
    }

    private static void EnsureSaneTun(TunSettings tun)
    {
        tun.RouteExcludeAddress ??= new List<string>();
        tun.RouteExcludeAddress.RemoveAll(s => s == null!);
    }

    private static void EnsureSaneServerEntry(VlessServerEntry s)
    {
        s.Reality   ??= new VlessRealityConfig();
        s.Tls       ??= new VlessTlsConfig();
        s.Transport ??= new VlessTransportConfig();
        s.Transport.Headers ??= new Dictionary<string, string>();
    }
}
