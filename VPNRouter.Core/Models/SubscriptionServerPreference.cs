using VPNRouter.Core.Services;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public sealed record SubscriptionServerPreference
{
    [YamlMember(Alias = "category", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? CategoryName { get; set; }

    [YamlMember(Alias = "feed_id", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? FeedId { get; set; }

    [YamlMember(Alias = "row_key", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? RowKey { get; set; }

    internal static SubscriptionServerPreference InCategory(ProtocolGroup category) =>
        new() { CategoryName = category.ToString() };

    internal ProtocolGroup? GetCategory() =>
        Enum.TryParse<ProtocolGroup>(CategoryName, out var group) && Enum.IsDefined(group) ? group : null;

    internal static SubscriptionServerPreference? For(AppConfig app) =>
        string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase)
            ? app.SubscriptionPreference
            : null;

    internal IEnumerable<(SubscriptionEntry Feed, VlessServerEntry Server)> Candidates(IEnumerable<SubscriptionEntry> subscriptions)
    {
        var category = GetCategory();
        if (CategoryName != null && category == null) yield break;

        foreach (var feed in subscriptions.Where(feed => feed.Enabled))
        {
            if (CategoryName == null && (RowKey == null || FeedId != feed.Id)) continue;
            foreach (var server in feed.Servers)
            {
                if (category is { } group
                    ? ProtocolGroups.Of(server) == group && CanAutoSelect(server)
                    : !string.IsNullOrWhiteSpace(server.Server) && server.Server != "your.server.com" &&
                        SubscriptionEntry.RowKey(server) == RowKey)
                    yield return (feed, server);
            }
        }
    }

    internal static bool CanAutoSelect(VlessServerEntry server) =>
        server.Dnstt == null && !server.IsTurnRelay && string.IsNullOrEmpty(server.DetourVia) &&
        !string.IsNullOrWhiteSpace(server.Server) && server.Port is > 0 and <= 65535 &&
        !VlessServersResolver.IsPlaceholderEntry(server) && ConfigGenerator.IsServerRuntimeAvailable(server);

    internal (SubscriptionEntry Feed, VlessServerEntry Server)? Preferred(
        AppConfig app, IEnumerable<SubscriptionEntry>? subscriptions = null)
    {
        var candidates = Candidates(subscriptions ?? app.Subscriptions).ToList();
        if (candidates.Count == 0) return null;

        if (CategoryName != null && app.ActiveSubscriptionRow != null)
        {
            var active = candidates.FindIndex(row => row.Feed.Id == app.ActiveSubscriptionId &&
                row.Server.Name == app.ActiveSubscriptionServer &&
                SubscriptionEntry.RowKey(row.Server) == app.ActiveSubscriptionRow);
            if (active >= 0) return candidates[active];
        }

        return candidates[0];
    }
}
