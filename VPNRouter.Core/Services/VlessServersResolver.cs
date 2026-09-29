using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class VlessServersResolver
{
    public static List<VlessServerEntry> Resolve(AppSettings settings, ILogger? logger = null)
    {
        var configMode = (settings.App.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        var isGenerated = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);

        var subscriptionAggregated = (settings.App.Subscriptions ?? new())
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s?.Server) && s.Server != "your.server.com")
            .ToList();

        var hasActiveSubscriptionServers = subscriptionAggregated.Count > 0;

        var activeEntry = (settings.Vless.Servers ?? new())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s?.Name)
                && s.Name.Equals(settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase));

        var activeIsLegitimateManual = activeEntry != null && !IsPlaceholderEntry(activeEntry);

        if (hasActiveSubscriptionServers
            && (isSubscribe || (isGenerated && !activeIsLegitimateManual)))
        {
            settings.Vless.Servers = subscriptionAggregated;

            if (!string.IsNullOrEmpty(settings.App.ActiveSubscriptionServer)
                && (isSubscribe || string.IsNullOrEmpty(settings.Vless.ActiveServer)))
            {
                settings.Vless.ActiveServer = settings.App.ActiveSubscriptionServer;
            }

            if (!string.IsNullOrEmpty(settings.Vless.ActiveServer))
            {
                var oldActive = settings.Vless.ActiveServer;
                var matchByName = subscriptionAggregated.Any(s =>
                    !string.IsNullOrEmpty(s.Name)
                    && s.Name.Equals(oldActive, StringComparison.OrdinalIgnoreCase));

                if (!matchByName)
                {
                    var newActive = subscriptionAggregated[0].Name;
                    logger?.Warning(
                        "[VlessServersResolver] Active server '{Old}' not in current scope " +
                        "(subscription mode). Falling back to '{New}'.",
                        oldActive,
                        newActive);
                    settings.Vless.ActiveServer = newActive;
                    settings.App.ActiveSubscriptionServer = newActive;
                }
            }

            logger?.Information(
                "[VlessServersResolver] Aggregated {Count} server(s) from {Subs} active subscription(s) " +
                "(mode: {Mode}, active: {Active})",
                subscriptionAggregated.Count,
                settings.App.Subscriptions?.Count(s => s != null && s.Enabled) ?? 0,
                configMode,
                string.IsNullOrEmpty(settings.Vless.ActiveServer) ? "first" : settings.Vless.ActiveServer);

            return subscriptionAggregated;
        }

        if (isSubscribe)
        {
            logger?.Warning(
                "[VlessServersResolver] config_mode=subscribe but no enabled subscription has servers. " +
                "Falling back to manually-configured Vless.Servers / Vless.Server.");
        }

        var manual = settings.Vless.GetEffectiveServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
            .ToList();

        if (manual.Count != (settings.Vless.Servers?.Count ?? 0))
            settings.Vless.Servers = manual;

        if (manual.Count > 0)
        {
            logger?.Debug(
                "[VlessServersResolver] Using {Count} manually-configured VLESS server(s)",
                manual.Count);
        }

        return manual;
    }

    internal static bool IsPlaceholderEntry(VlessServerEntry entry) =>
        PlaceholderDefense.LayerA_ResolverScopeGuard.IsPlaceholderEntry(entry);

    public static string? DescribeEmptyReason(AppSettings settings)
    {
        var configMode = (settings.App.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        if (isSubscribe)
        {
            if (settings.App.Subscriptions == null || settings.App.Subscriptions.Count == 0)
                return "Subscribe mode is selected but no subscription URLs are configured. Add a subscription in the Subscribe tab.";

            var enabled = settings.App.Subscriptions.Where(s => s != null && s.Enabled).ToList();
            if (enabled.Count == 0)
                return "Subscribe mode is selected but every subscription is disabled. Enable at least one subscription.";

            var withServers = enabled.Where(s => s.Servers != null && s.Servers.Count > 0).ToList();
            if (withServers.Count == 0)
                return "Subscribe mode: no subscription has fetched any servers yet. Click 'Refresh All' on the Subscribe tab — if it fails, check the subscription URL.";
        }

        if ((settings.Vless.Servers == null || settings.Vless.Servers.Count == 0)
            && string.IsNullOrWhiteSpace(settings.Vless.Server))
        {
            return "VLESS server is not configured. Add a server manually in the Servers tab, or enable a subscription.";
        }

        return null;
    }
}
