using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SubscriptionResolver
{
    public static async Task<int> ResolveAsync(
        AppSettings settings,
        bool refreshFromNetwork,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        var isSubscribe = settings.App.ConfigMode?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true;
        if (!isSubscribe) return 0;

        if (settings.App.Subscriptions.Count == 0
            && !string.IsNullOrEmpty(settings.App.SubscriptionUrl))
        {
            settings.App.Subscriptions.Add(new SubscriptionEntry
            {
                Name = "Default",
                Url = settings.App.SubscriptionUrl,
                Enabled = true,
                Servers = settings.App.SubscriptionServers ?? new()
            });
            logger?.Information("[SubscriptionResolver] Migrated legacy SubscriptionUrl to Subscriptions list");
        }

        if (refreshFromNetwork && settings.App.Subscriptions.Count > 0)
        {
            var enabled = settings.App.Subscriptions.Where(s => s.Enabled).ToList();
            if (enabled.Count > 0)
            {
                logger?.Information("[SubscriptionResolver] Refreshing {Count} subscription(s)...", enabled.Count);
                try
                {
                    await Task.WhenAll(enabled.Select(s =>
                        SubscriptionFetcher.RefreshEntryAsync(s, logger, ct)));
                    var total = enabled.Sum(s => s.Servers.Count);
                    logger?.Information("[SubscriptionResolver] Subscriptions refreshed: {Total} servers", total);
                }
                catch (Exception ex)
                {
                    logger?.Warning(ex, "[SubscriptionResolver] Refresh failed, using cached servers");
                }
            }
        }

        var aggregated = settings.App.Subscriptions
            .Where(s => s.Enabled)
            .SelectMany(s => s.Servers)
            .ToList();

        if (aggregated.Count > 0)
        {
            settings.Vless.Servers = aggregated;
            settings.Vless.ActiveServer = settings.App.ActiveSubscriptionServer;
            settings.App.ConfigMode = "generated";
            logger?.Information("[SubscriptionResolver] Aggregated {Count} servers, active: {Active}",
                aggregated.Count, settings.App.ActiveSubscriptionServer);
        }

        return aggregated.Count;
    }
}
