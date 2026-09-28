namespace VPNRouter.Core.Models;

public static class AppSettingsSane
{
    internal static string GenerateClashApiSecret()
        => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static AppSettings EnsureSane(this AppSettings? settings)
    {
        settings ??= new AppSettings();

        settings.App              ??= new AppConfig();
        settings.ProfileSources   ??= new List<ProfileSource>();
        settings.Vless            ??= new VlessConfig();
        settings.Tun              ??= new TunSettings();
        settings.Dns              ??= new DnsSettings();
        settings.SingBox          ??= new SingBoxSettings();
        if (string.IsNullOrEmpty(settings.SingBox.ClashApiSecret))
            settings.SingBox.ClashApiSecret = GenerateClashApiSecret();
        settings.Monitoring       ??= new MonitoringSettings();
        settings.CustomApps       ??= new List<string>();
        settings.CustomGroupApps  ??= new Dictionary<string, List<string>>();
        settings.CustomCategories ??= new List<CustomCategory>();
        settings.ExcludedApps     ??= new List<string>();
        settings.Update           ??= new UpdateSettings();

        settings.ProfileSources.RemoveAll(p => p == null!);
        settings.CustomApps.RemoveAll(s => s == null!);
        settings.CustomCategories.RemoveAll(c => c == null!);
        settings.ExcludedApps.RemoveAll(s => s == null!);
        foreach (var c in settings.CustomCategories)
            c.Apps ??= new List<string>();

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

        if (!string.Equals(app.RoutingAppsMode, "include", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(app.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
        {
            app.RoutingAppsMode = "include";
        }
        else
        {
            app.RoutingAppsMode = app.RoutingAppsMode!.ToLowerInvariant();
        }

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
