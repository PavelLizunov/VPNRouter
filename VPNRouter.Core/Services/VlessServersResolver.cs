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

        if (SubscriptionServerPreference.For(settings.App) is { } preference)
        {
            settings.Vless.Servers = subscriptionAggregated;
            if (preference.Preferred(settings.App) is not { } chosen) return [];

            settings.App.ActiveSubscriptionId = chosen.Feed.Id;
            settings.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(chosen.Server);
            settings.App.ActiveSubscriptionServer = settings.Vless.ActiveServer = chosen.Server.Name;
            PrioritizeSelectedRow(settings, subscriptionAggregated);
            return subscriptionAggregated;
        }

        var hasActiveSubscriptionServers = subscriptionAggregated.Count > 0;

        var activeEntry = (settings.Vless.Servers ?? new())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s?.Name)
                && s.Name.Equals(settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase));

        var activeIsLegitimateManual = activeEntry != null && !IsPlaceholderEntry(activeEntry);

        if (hasActiveSubscriptionServers
            && (isSubscribe || (isGenerated && !activeIsLegitimateManual)))
        {
            if (isSubscribe || string.IsNullOrEmpty(settings.Vless.ActiveServer))
                PrioritizeSelectedRow(settings, subscriptionAggregated);
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
                    var newActive = (subscriptionAggregated.FirstOrDefault(s => !s.IsTurnRelay)
                        ?? subscriptionAggregated[0]).Name;
                    logger?.Warning(
                        "[VlessServersResolver] Active server outside subscription scope, using first available server");
                    settings.Vless.ActiveServer = newActive;
                    settings.App.ActiveSubscriptionServer = newActive;
                }
            }

            logger?.Information(
                "[VlessServersResolver] Aggregated {Count} server(s) from {Subs} active subscription(s) " +
                "(mode: {Mode})",
                subscriptionAggregated.Count,
                settings.App.Subscriptions?.Count(s => s != null && s.Enabled) ?? 0,
                isSubscribe ? "subscribe" : "generated");

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

    // Older consumers resolve names (or an unnamed selection) from the flattened list.
    // Only move the chosen row in that projection. The registered feeds keep their own order and detour data.
    internal static void PrioritizeSelectedRow(AppSettings settings, List<VlessServerEntry> pool)
    {
        var app = settings.App;
        if (app.ActiveSubscriptionId == null || app.ActiveSubscriptionRow == null) return;
        var feed = app.Subscriptions.FirstOrDefault(s => s.Enabled && s.Id == app.ActiveSubscriptionId);
        var row = feed?.FindExactRow(app.ActiveSubscriptionServer, app.ActiveSubscriptionRow);
        if (row == null || !pool.Contains(row)) return;
        settings.Vless.ActiveServer = row.Name;
        var preceding = pool.TakeWhile(other => !ReferenceEquals(other, row));
        if (preceding.Any(other => string.IsNullOrEmpty(row.Name) || settings.Vless.AutoSelectBestServer ||
                string.Equals(other.Name, row.Name, StringComparison.OrdinalIgnoreCase) || other.Server == row.Server))
        {
            pool.Remove(row);
            pool.Insert(0, row);
        }
    }

    internal static bool IsPlaceholderEntry(VlessServerEntry entry) =>
        PlaceholderDefense.LayerA_ResolverScopeGuard.IsPlaceholderEntry(entry);

    internal static List<VlessServerEntry> GetActiveServers(AppSettings settings)
    {
        var preference = SubscriptionServerPreference.For(settings.App);
        if (preference == null) return settings.Vless?.GetActiveServers() ?? [];
        if (preference.Preferred(settings.App) is not { } chosen) return [];

        // Select one exit per connection. Naive UDP pairing can still use the full resolved pool.
        return VlessConfig.WithRequiredHop(chosen.Server, chosen.Feed.Servers);
    }

    public static string? DescribeEmptyReason(AppSettings settings)
    {
        if (SubscriptionServerPreference.For(settings.App) is { } preference &&
            preference.Preferred(settings.App) == null)
            return Localization.Strings.SmpSelectionUnavailable;

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
