using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed record SubscriptionCacheUpdate(SubscriptionEntry Origin, List<VlessServerEntry> PreviousRows,
    SubscriptionEntry Candidate, bool Failed)
{
    // Failed covers a transport/HTTP error and a response that yielded zero usable rows. An empty
    // or unparseable body is not an authoritative "the grant was removed" signal.
    internal static async Task<SubscriptionCacheUpdate> FetchAsync(SubscriptionEntry origin, ILogger? logger, CancellationToken ct)
    {
        var previous = origin.Servers;
        // RefreshEntryAsync replaces the list and updates metadata. Keep those writes detached until
        // the selected chain has been checked against the proposed contents of every enabled feed.
        var candidate = new SubscriptionEntry
        {
            Id = origin.Id, Url = origin.Url, Enabled = origin.Enabled, Servers = previous,
            UserInfo = origin.UserInfo, LastRefreshedAt = origin.LastRefreshedAt, LastServerCount = origin.LastServerCount
        };
        try
        {
            var count = await SubscriptionFetcher.RefreshEntryAsync(candidate, logger, ct);
            return new(origin, previous, candidate, count == 0);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            logger?.Warning("[Subscription] Could not refresh a registered feed");
            return new(origin, previous, candidate, true);
        }
    }

    internal static bool TryPublish(IReadOnlyList<SubscriptionCacheUpdate> updates, IReadOnlyList<SubscriptionEntry> feeds,
        SubscriptionChainSelection? chain, out VlessServerEntry? successor)
    {
        successor = null;
        if (updates.Any(u => !feeds.Contains(u.Origin) || u.Origin.Id != u.Candidate.Id ||
            u.Origin.Url != u.Candidate.Url || u.Origin.Enabled != u.Candidate.Enabled ||
            !ReferenceEquals(u.Origin.Servers, u.PreviousRows))) return false;

        if (chain != null)
        {
            var replacements = updates.ToDictionary(u => u.Origin, u => u.Candidate);
            var proposed = feeds.Where(f => f.Enabled).Select(f => replacements.GetValueOrDefault(f, f)).ToList();
            successor = chain.Match(proposed.FirstOrDefault(f => f.Id == chain.FeedId), proposed.SelectMany(f => f.Servers));
            if (successor == null) return false;
        }
        foreach (var update in updates)
        {
            var source = update.Origin;
            var candidate = update.Candidate;
            source.Servers = candidate.Servers;
            source.UserInfo = candidate.UserInfo;
            source.LastRefreshedAt = candidate.LastRefreshedAt;
            source.LastServerCount = candidate.LastServerCount;
        }
        return true;
    }
}
