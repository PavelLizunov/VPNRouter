using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// The selected exit is meaningful only within its registration. Opaque hop IDs can change
// on refresh, but an automatic replacement must still take an unambiguous, usable detour.
internal sealed record SubscriptionChainSelection(string FeedId, string Url, VlessServerEntry Target, string? Route)
{
    internal static SubscriptionChainSelection? Capture(AppConfig app,
        IReadOnlyList<SubscriptionEntry>? registeredFeeds = null)
    {
        if (!string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase)) return null;
        var preference = SubscriptionServerPreference.For(app);
        if (preference is { IsCategory: true }) return null;

        var feeds = (registeredFeeds ?? app.Subscriptions).Where(s => s.Enabled).ToList();
        var scoped = preference?.Preferred(app, feeds);
        var source = scoped?.Feed ?? feeds.FirstOrDefault(s => s.Id == app.ActiveSubscriptionId);
        var row = preference == null
            ? source?.FindExactRow(app.ActiveSubscriptionServer, app.ActiveSubscriptionRow)
            : scoped?.Server;
        if (preference == null && row == null && app.ActiveSubscriptionRow == null)
        {
            // A legacy name-only setting has the same first-match semantics as GetActiveServers.
            var pool = source != null ? source.Servers.AsEnumerable() : feeds.SelectMany(s => s.Servers);
            row = pool.FirstOrDefault(s => !string.IsNullOrEmpty(app.ActiveSubscriptionServer) &&
                string.Equals(s.Name, app.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase)) ?? pool.FirstOrDefault();
            if (row != null) source ??= feeds.First(s => s.Servers.Contains(row));
        }
        return source == null || row == null || (row.Dnstt == null && string.IsNullOrEmpty(row.DetourVia)) ? null :
            new(source.Id, source.Url, row, SubscriptionEntry.RouteKey(row, source.Servers));
    }

    internal void RebindPin(AppConfig app, List<SubscriptionEntry> feeds, VlessServerEntry successor)
    {
        if (SubscriptionServerPreference.For(app) is not { IsCategory: false } pin ||
            pin.FeedId != FeedId || pin.RowKey != SubscriptionEntry.RowKey(Target)) return;

        var feed = feeds.FirstOrDefault(s => s.Enabled && s.Id == FeedId && s.Url == Url);
        if (feed != null && SubscriptionEntry.RouteKey(successor, feed.Servers) == Route)
            app.SubscriptionPreference = pin with { RowKey = SubscriptionEntry.RowKey(successor) };
    }

    internal VlessServerEntry? Match(SubscriptionEntry? feed, IEnumerable<VlessServerEntry> enabledRows)
    {
        if (feed == null || !feed.Enabled || feed.Id != FeedId || feed.Url != Url) return null;
        if (Target.Dnstt is { } authority)
        {
            var matches = feed.Servers.Where(row => row.Dnstt is { } next
                && SingBoxFeatures.DnsttAvailable && row.OutboundId == Target.OutboundId
                && row.Server == Target.Server && row.Port == Target.Port
                && next.Domain == authority.Domain && next.NoiseKey == authority.NoiseKey
                && next.SshHostKey == authority.SshHostKey).ToList();
            if (matches.Count <= 1) return matches.FirstOrDefault();
            var sameGrant = matches.Where(row => row.Dnstt!.SshPrivateKey == authority.SshPrivateKey).Take(2).ToArray();
            return sameGrant.Length == 1 ? sameGrant[0] : null;
        }
        var byId = enabledRows.Where(s => !string.IsNullOrEmpty(s.OutboundId))
            .ToLookup(s => s.OutboundId, StringComparer.OrdinalIgnoreCase);
        var targetKey = SubscriptionEntry.OutboundKey(Target);
        var candidates = feed.Servers.Where(row =>
        {
            if (string.IsNullOrEmpty(row.DetourVia) || row.Port != Target.Port ||
                row.Server != Target.Server || !IsUsableChainTarget(row) ||
                SubscriptionEntry.OutboundKey(row) != targetKey) return false;
            var hops = byId[row.DetourVia].Take(2).ToArray();
            return hops.Length == 1 && feed.Servers.Contains(hops[0]) &&
                string.IsNullOrEmpty(hops[0].DetourVia) && IsUsableChainUpstream(hops[0]);
        }).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        var sameRoute = candidates.Where(s => SubscriptionEntry.RouteKey(s, feed.Servers) == Route).ToList();
        if (sameRoute.Count <= 1) return sameRoute.FirstOrDefault();
        var oldKey = SubscriptionEntry.RowKey(Target);
        var exact = sameRoute.Where(s => SubscriptionEntry.RowKey(s) == oldKey).Take(2).ToArray();
        return exact.Length == 1 ? exact[0] : null;
    }

    // One direct VLESS entry is the only accepted upstream. A VMess exit is a valid
    // target when the app feed marks it as chained; it never becomes an entry.
    internal static bool IsUsableChainTarget(VlessServerEntry row) =>
        IsUsableChainUpstream(row) || IsUsableVmessTarget(row);

    internal static bool IsUsableChainUpstream(VlessServerEntry row) =>
        string.Equals(row.Protocol ?? "vless", "vless", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(row.Transport?.Type, "xhttp", StringComparison.OrdinalIgnoreCase) &&
        row.Awg == null && !row.IsDnsTunnel && !string.IsNullOrWhiteSpace(row.Server) &&
        row.Server != "your.server.com" && row.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(row.Uuid) &&
        !VlessServersResolver.IsPlaceholderEntry(row) &&
        (!string.Equals(row.Security, "reality", StringComparison.OrdinalIgnoreCase) ||
            VlessUriParser.IsValidRealityPublicKey(row.Reality?.PublicKey));

    private static bool IsUsableVmessTarget(VlessServerEntry row)
    {
        var transport = row.Transport?.Type;
        var tls = row.Tls;
        return string.Equals(row.Protocol ?? "vless", "vmess", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(row.Server) && row.Server != "your.server.com" &&
            row.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(row.Uuid) &&
            row.AlterId == 0 && VmessUriParser.TryNormalizeAeadSecurity(row.VmessSecurity, out _) &&
            tls is not null && tls.Enabled && !string.IsNullOrWhiteSpace(tls.ServerName) &&
            !VlessServersResolver.IsPlaceholderEntry(row) &&
            (string.IsNullOrEmpty(transport) ||
                transport.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                transport.Equals("ws", StringComparison.OrdinalIgnoreCase));
    }
}
