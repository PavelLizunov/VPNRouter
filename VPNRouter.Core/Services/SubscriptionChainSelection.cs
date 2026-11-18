using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// The selected exit is meaningful only within its registration. Opaque hop IDs can change
// on refresh, but an automatic replacement must still take an unambiguous, usable detour.
internal sealed record SubscriptionChainSelection(string FeedId, string Url, VlessServerEntry Target, string? Route)
{
    internal static SubscriptionChainSelection? Capture(AppConfig app)
    {
        if (!string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase)) return null;
        var feeds = app.Subscriptions.Where(s => s.Enabled).ToList();
        var source = feeds.FirstOrDefault(s => s.Id == app.ActiveSubscriptionId);
        var row = source?.FindExactRow(app.ActiveSubscriptionServer, app.ActiveSubscriptionRow);
        if (row == null && app.ActiveSubscriptionRow == null)
        {
            // A legacy name-only setting has the same first-match semantics as GetActiveServers.
            var pool = source != null ? source.Servers.AsEnumerable() : feeds.SelectMany(s => s.Servers);
            row = pool.FirstOrDefault(s => !string.IsNullOrEmpty(app.ActiveSubscriptionServer) &&
                string.Equals(s.Name, app.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase)) ?? pool.FirstOrDefault();
            if (row != null) source ??= feeds.First(s => s.Servers.Contains(row));
        }
        return source == null || row == null || string.IsNullOrEmpty(row.DetourVia) ? null :
            new(source.Id, source.Url, row, SubscriptionEntry.RouteKey(row, source.Servers));
    }

    internal VlessServerEntry? Match(SubscriptionEntry? feed, IEnumerable<VlessServerEntry> enabledRows)
    {
        if (feed == null || !feed.Enabled || feed.Id != FeedId || feed.Url != Url) return null;
        var byId = enabledRows.Where(s => !string.IsNullOrEmpty(s.OutboundId))
            .ToLookup(s => s.OutboundId, StringComparer.OrdinalIgnoreCase);
        var targetKey = SubscriptionEntry.OutboundKey(Target);
        var candidates = feed.Servers.Where(row =>
        {
            if (string.IsNullOrEmpty(row.DetourVia) || row.Port != Target.Port ||
                row.Server != Target.Server || !UsableVless(row) || SubscriptionEntry.OutboundKey(row) != targetKey) return false;
            var hops = byId[row.DetourVia].Take(2).ToArray();
            return hops.Length == 1 && feed.Servers.Contains(hops[0]) &&
                string.IsNullOrEmpty(hops[0].DetourVia) && UsableVless(hops[0]);
        }).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        var sameRoute = candidates.Where(s => SubscriptionEntry.RouteKey(s, feed.Servers) == Route).ToList();
        if (sameRoute.Count <= 1) return sameRoute.FirstOrDefault();
        var oldKey = SubscriptionEntry.RowKey(Target);
        var exact = sameRoute.Where(s => SubscriptionEntry.RowKey(s) == oldKey).Take(2).ToArray();
        return exact.Length == 1 ? exact[0] : null;
    }

    private static bool UsableVless(VlessServerEntry row) =>
        string.Equals(row.Protocol ?? "vless", "vless", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(row.Transport?.Type, "xhttp", StringComparison.OrdinalIgnoreCase) &&
        row.Awg == null && !row.IsDnsTunnel && !string.IsNullOrWhiteSpace(row.Server) &&
        row.Server != "your.server.com" && row.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(row.Uuid) &&
        !VlessServersResolver.IsPlaceholderEntry(row) &&
        (!string.Equals(row.Security, "reality", StringComparison.OrdinalIgnoreCase) ||
            VlessUriParser.IsValidRealityPublicKey(row.Reality?.PublicKey));
}
