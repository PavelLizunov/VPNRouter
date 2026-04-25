using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Single source of truth for resolving the effective VLESS server list
/// from any source — manual entries, legacy single-server fields, or
/// active subscriptions. Mutates <c>settings.Vless.Servers</c> in place
/// so downstream consumers (<see cref="ConfigGenerator"/>,
/// <see cref="HealthMonitor"/>) get a consistent view regardless of
/// which entry path was taken (CLI / GUI / Service / hot-reload Apply).
///
/// <para>Background — bug discovered in v2.28.1 field-test (2026-04-26):
/// <see cref="VpnEngine.StartAsync"/> had its own aggregation guard,
/// but <see cref="VpnEngine.Apply"/> (the hot-reload path) didn't.
/// On a hot-reload triggered by a settings change, Apply read fresh
/// <c>settings.Vless.Servers = []</c> straight from YAML (subscriptions
/// store servers in <c>App.Subscriptions[].Servers</c>, NOT in
/// <c>Vless.Servers</c>) and produced a sing-box JSON with NO proxy
/// outbound. sing-box loaded this config, silently ignored route rules
/// pointing at the missing <c>"proxy"</c> tag, and routed all process
/// traffic to <c>route.final: "direct"</c>. The user's sing-box also
/// kept attempting urltest probes to the upstream VLESS server, which
/// hit the server without a proper VLESS handshake and produced 249
/// "flow mismatch: expected xtls-rprx-vision but got none" errors per
/// day in the server log. This resolver eliminates that class of bug
/// by ensuring all entry points use the same aggregation logic.</para>
/// </summary>
public static class VlessServersResolver
{
    /// <summary>
    /// Resolve the effective VLESS server list and write it into
    /// <c>settings.Vless.Servers</c> (in-memory only, not persisted).
    /// Idempotent: repeated calls are safe.
    /// </summary>
    /// <returns>The resolved server list (same reference as <c>settings.Vless.Servers</c> after the call).</returns>
    public static List<VlessServerEntry> Resolve(AppSettings settings, ILogger? logger = null)
    {
        var configMode = (settings.App.ConfigMode ?? "generated").Trim();
        var isSubscribe = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        // Path 1: subscribe mode — aggregate enabled subscription servers.
        // Always re-aggregate fresh so a subscription Refresh propagates
        // into the live config on the very next Apply / Start.
        if (isSubscribe)
        {
            var aggregated = (settings.App.Subscriptions ?? new())
                .Where(s => s != null && s.Enabled && s.Servers != null)
                .SelectMany(s => s.Servers)
                .Where(s => !string.IsNullOrWhiteSpace(s?.Server) && s.Server != "your.server.com")
                .ToList();

            if (aggregated.Count > 0)
            {
                settings.Vless.Servers = aggregated;

                // Carry over the active selection from the subscribe-mode
                // setting if the manual one isn't set.
                if (string.IsNullOrEmpty(settings.Vless.ActiveServer)
                    && !string.IsNullOrEmpty(settings.App.ActiveSubscriptionServer))
                {
                    settings.Vless.ActiveServer = settings.App.ActiveSubscriptionServer;
                }

                logger?.Information(
                    "[VlessServersResolver] Aggregated {Count} server(s) from {Subs} active subscription(s) (active: {Active})",
                    aggregated.Count,
                    settings.App.Subscriptions?.Count(s => s != null && s.Enabled) ?? 0,
                    string.IsNullOrEmpty(settings.Vless.ActiveServer) ? "first" : settings.Vless.ActiveServer);

                return aggregated;
            }

            // Subscribe mode but no subscription servers (yet).
            // Fall through to manual list — could still be populated.
            logger?.Warning(
                "[VlessServersResolver] config_mode=subscribe but no enabled subscription has servers. " +
                "Falling back to manually-configured Vless.Servers / Vless.Server.");
        }

        // Path 2: manual VLESS config — uses Vless.Servers list, falling
        // back to legacy scalar Vless.Server fields. GetEffectiveServers
        // already handles that fallback.
        var manual = settings.Vless.GetEffectiveServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
            .ToList();

        // GetEffectiveServers may return Vless.Servers BY REFERENCE; only
        // re-assign if we filtered something out (otherwise it's a no-op).
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

    /// <summary>
    /// Diagnostic helper for callers that need a clear "why is this empty"
    /// signal without throwing. Returns a localizable English-language reason
    /// the resolver couldn't find any servers, or null if the list is non-empty.
    /// </summary>
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
