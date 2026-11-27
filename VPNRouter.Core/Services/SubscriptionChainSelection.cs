using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// A refresh may update a selected profile only within its registration.
// Detour targets must retain a usable hop. Direct native profiles can rotate credentials.
internal sealed record SubscriptionChainSelection(string FeedId, string Url, VlessServerEntry Target, string? Route)
{
    internal static bool RequiresMatchingSuccessor(VlessServerEntry? row) =>
        row != null && (row.IsNativeTransport || row.Dnstt != null || !string.IsNullOrEmpty(row.DetourVia));

    internal static SubscriptionChainSelection? Capture(AppConfig app,
        IReadOnlyList<SubscriptionEntry>? registeredFeeds = null)
    {
        if (!string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase)) return null;
        var preference = SubscriptionServerPreference.For(app);
        if (preference is { CategoryName: not null }) return null;

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
                string.Equals(s.Name, app.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase))
                ?? pool.FirstOrDefault(s => !s.IsTurnRelay);
            if (row != null) source ??= feeds.First(s => s.Servers.Contains(row));
        }
        return source == null || row == null || !RequiresMatchingSuccessor(row) ? null :
            new(source.Id, source.Url, row, SubscriptionEntry.RouteKey(row, source.Servers));
    }

    internal void RebindPin(AppConfig app, List<SubscriptionEntry> feeds, VlessServerEntry successor)
    {
        if (SubscriptionServerPreference.For(app) is not { CategoryName: null } pin ||
            pin.FeedId != FeedId || pin.RowKey != SubscriptionEntry.RowKey(Target)) return;

        var feed = feeds.FirstOrDefault(s => s.Enabled && s.Id == FeedId && s.Url == Url);
        if (feed != null && (Target.IsNativeTransport || SubscriptionEntry.RouteKey(successor, feed.Servers) == Route))
            app.SubscriptionPreference = pin with { RowKey = SubscriptionEntry.RowKey(successor) };
    }

    internal VlessServerEntry? Match(SubscriptionEntry? feed, IEnumerable<VlessServerEntry> enabledRows)
    {
        if (feed == null || !feed.Enabled || feed.Id != FeedId || feed.Url != Url) return null;
        if (Target.IsNativeTransport)
        {
            var exactRow = feed.FindExactRow(Target.Name, SubscriptionEntry.RowKey(Target));
            if (exactRow is { } && ConfigGenerator.IsServerRuntimeAvailable(exactRow)) return exactRow;
            var sameProfile = feed.Servers.Where(row =>
                string.IsNullOrEmpty(row.DetourVia) && row.Protocol == Target.Protocol &&
                row.Name == Target.Name && row.Server == Target.Server && row.Port == Target.Port &&
                ConfigGenerator.IsServerRuntimeAvailable(row)).Take(2).ToList();
            return sameProfile.Count == 1 ? sameProfile[0] : null;
        }
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

    // A direct VLESS entry or a complete TurnRelay hop is a valid upstream. A
    // VMess/VLESS exit is a valid target when the app feed marks it as chained;
    // neither TurnRelay nor VMess ever becomes a standalone target.
    internal static bool IsUsableChainTarget(VlessServerEntry row) =>
        !row.IsTurnRelay && (IsUsableVlessUpstream(row) || IsUsableVmessTarget(row));

    internal static bool IsUsableChainUpstream(VlessServerEntry row) =>
        row.IsTurnRelay ? IsUsableTurnRelayUpstream(row) : IsUsableVlessUpstream(row);

    internal static bool IsUsableTurnRelayUpstream(VlessServerEntry row) =>
        SingBoxFeatures.TurnRelayAvailable && row.TurnRelay is { } profile && profile.IsComplete() &&
        TurnRelayConfig.IsLiteralIp(row.Server) && row.Port is > 0 and <= 65535 &&
        TurnRelayUriParser.IsValidId(row.OutboundId) &&
        !VlessServersResolver.IsPlaceholderEntry(row);

    private static bool IsUsableVlessUpstream(VlessServerEntry row) =>
        string.Equals(row.Protocol ?? "vless", "vless", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(row.Transport?.Type, "xhttp", StringComparison.OrdinalIgnoreCase) &&
        row.Awg == null && !row.IsDnsTunnel && !row.IsTurnRelay && !string.IsNullOrWhiteSpace(row.Server) &&
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
