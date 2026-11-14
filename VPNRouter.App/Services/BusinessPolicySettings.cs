using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal static class BusinessPolicySettings
{
    // The caller captures on the settings owner thread before background startup.
    // Engine/failover mutations then cannot reach the UI or another session.
    internal static AppSettings Capture(AppSettings source, IReadOnlyList<CustomRule> embeddedRules,
        BusinessPolicySnapshot? policy)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, AppJsonContext.Default.AppSettings);
        AppSettings captured;
        try
        {
            captured = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.AppSettings)
                ?? throw new InvalidDataException("Business settings snapshot is unavailable.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        // This writable runtime metadata is intentionally absent from stored settings.
        captured.Tun.AutoDetectedExcludeAddress = [.. source.Tun.AutoDetectedExcludeAddress];
        captured.CustomGroupApps = new(captured.CustomGroupApps, source.CustomGroupApps.Comparer);
        captured.Vless.Transport.Headers = new(captured.Vless.Transport.Headers, source.Vless.Transport.Headers.Comparer);
        foreach (var (original, copy) in Servers(source).Zip(Servers(captured)))
            copy.Transport.Headers = new(copy.Transport.Headers, original.Transport.Headers.Comparer);
        EnforceRules(captured, embeddedRules, policy);
        return captured;
    }

    private static IEnumerable<VlessServerEntry> Servers(AppSettings settings) =>
        settings.Vless.Servers.Concat(settings.App.SubscriptionServers)
            .Concat(settings.App.Subscriptions.SelectMany(s => s.Servers));

    internal static void EnforceRules(AppSettings target, IReadOnlyList<CustomRule> embeddedRules,
        BusinessPolicySnapshot? policy)
    {
        if (embeddedRules.Count == 0 || embeddedRules.Any(r => !r.Enabled ||
                ConfigGenerator.BuildCustomRouteRule(r) == null))
            throw new InvalidDataException("Business routing preset is invalid.");

        var rules = BusinessPolicyRouting.Compose(embeddedRules, policy);
        var processes = rules.Where(r => r.Type == "process_name" && r.Action == "proxy")
            .SelectMany(r => r.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customRules = rules.Where(r => r.Type != "process_name" || r.Action != "proxy").ToList();

        target.ActiveProfile = string.Empty;
        target.CustomApps = [];
        target.ExcludedApps = [];
        target.App.RoutingAppsMode = "include";
        target.App.RoutingAppsIncludeInitialized = true;
        target.App.RoutingAppsExclude = [];
        target.App.RoutingAppsInclude = processes;
        // The per-app path owns UDP siblings and TCP-only QUIC behavior.
        target.App.CustomRules = customRules;
        target.App.CustomRulesPriority = "custom_first";
        target.BusinessPolicy = policy;
    }
}
