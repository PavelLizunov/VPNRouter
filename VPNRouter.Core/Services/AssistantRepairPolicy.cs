using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal enum AssistantRepairRoutingIssue
{
    None,
    AutoRouteDisabled,
    EmptyInclude,
    CustomConfiguration,
}

internal sealed record AssistantRepairServer(string FeedId, VlessServerEntry Entry);

internal static class AssistantRepairPolicy
{
    internal static int? SuggestMtu(TunSettings tun, HealthCheck.PathMtuProbeResult? probe)
    {
        int candidate = ConfigGenerator.NormalizeTunMtu(tun.Mtu);
        if (probe is { PlainPingBlocked: false, SkippedTunnelUp: false, BestPayload: int payload }
            && payload >= 1332 && payload <= TunSettings.MaximumMtu)
            candidate = Math.Min(candidate, payload);
        return candidate != tun.Mtu ? candidate : null;
    }

    internal static AssistantRepairRoutingIssue FindRoutingIssue(AppSettings settings, bool hasEffectiveInclude)
    {
        if (settings.App.ConfigMode is not ("generated" or "subscribe"))
            return AssistantRepairRoutingIssue.CustomConfiguration;
        if (!settings.Tun.AutoRoute)
            return AssistantRepairRoutingIssue.AutoRouteDisabled;
        if (!string.Equals(settings.App.RoutingMode, "full", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase)
            && !hasEffectiveInclude)
            return AssistantRepairRoutingIssue.EmptyInclude;
        return AssistantRepairRoutingIssue.None;
    }

    internal static IReadOnlyList<AssistantRepairServer> AlternateServers(AppSettings settings)
    {
        if (settings.App.ConfigMode != "subscribe" || !settings.Vless.AutoSelectBestServer)
            return [];

        var feeds = settings.App.Subscriptions.Where(feed => feed.Enabled
            && (string.IsNullOrEmpty(settings.App.ActiveSubscriptionId) || feed.Id == settings.App.ActiveSubscriptionId))
            .Take(2).ToArray();
        if (feeds is not [var feed]) return [];

        var matching = feed.Servers.Where(row => string.Equals(row.Name, settings.App.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase)
            && (settings.App.ActiveSubscriptionRow == null || SubscriptionEntry.RowKey(row) == settings.App.ActiveSubscriptionRow))
            .Take(2).ToArray();
        if (matching is not [var active] || !SupportsTrial(active)) return [];

        return feed.Servers.Where(server => !ReferenceEquals(server, active) && SupportsTrial(server)
                && string.Equals(server.Protocol ?? "vless", active.Protocol ?? "vless", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(active.Flow) || !string.IsNullOrEmpty(server.Flow)))
            .Take(3).Select(server => new AssistantRepairServer(feed.Id, server)).ToArray();
    }

    private static bool SupportsTrial(VlessServerEntry server) =>
        server.Dnstt == null && string.IsNullOrEmpty(server.DetourVia)
        && !string.IsNullOrWhiteSpace(server.Name) && !string.IsNullOrWhiteSpace(server.Server)
        && !VlessServersResolver.IsPlaceholderEntry(server);
}
