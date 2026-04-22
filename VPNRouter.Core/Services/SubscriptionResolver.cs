using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Resolves subscription-mode settings into a flat VLESS server list that
/// <see cref="ConfigGenerator"/> can consume. Performs four steps:
///   1. Legacy migration: old single <c>SubscriptionUrl</c> → <c>Subscriptions[0]</c>.
///   2. Optional network refresh of all enabled subscription URLs.
///   3. Aggregation of all enabled subscription servers → <c>Vless.Servers</c>.
///   4. Flip <c>ConfigMode</c> to <c>"generated"</c> so downstream code is
///      unaware of subscription mode.
///
/// <para>Used by both <c>VPNRouter.Service</c> and <c>VPNRouter.CLI</c> so
/// service / CLI / GUI startup paths remain equivalent. Before this helper,
/// only the Service (and GUI) knew about subscriptions — CLI would fail with
/// "No 'proxy' outbound defined" on any subscribe-mode config.</para>
/// </summary>
public static class SubscriptionResolver
{
    /// <summary>
    /// Resolve subscription-mode settings. Mutates <paramref name="settings"/>.
    /// No-op if <c>ConfigMode</c> is not <c>"subscribe"</c>.
    /// </summary>
    /// <param name="settings">AppSettings to mutate (Vless.Servers, Vless.ActiveServer, App.ConfigMode).</param>
    /// <param name="refreshFromNetwork">If true, fetch each enabled subscription URL before aggregating. If false, use cached servers only.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total server count aggregated into <c>Vless.Servers</c>. 0 if not in subscribe mode or no servers.</returns>
    public static async Task<int> ResolveAsync(
        AppSettings settings,
        bool refreshFromNetwork,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        var isSubscribe = settings.App.ConfigMode?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true;
        if (!isSubscribe) return 0;

        // Legacy migration: if only old SubscriptionUrl is set, promote to the Subscriptions list.
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

        // Refresh enabled subscriptions from network (best-effort — cached servers are fine if refresh fails).
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

        // Aggregate all enabled subscription servers → Vless.Servers and flip to generated mode.
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
