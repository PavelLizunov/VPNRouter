using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

internal enum SubscriptionInputAction
{
    // The pasted URL is a subscription we already hold with cached servers: nothing to replace, nothing to wait for.
    KeepCached,

    // We hold it but have no servers from it yet: refresh it (in place), do not replace the entry.
    KeepAndRefresh,

    // A URL we do not hold: it replaces the home screen's subscription and is fetched.
    Replace,
}

// Every press of Connect on the home screen used to re-create the subscription from the URL in the field (the field is pre-filled with the saved
// URL), which dropped the cached servers (and any other subscription), fetched the URL again and blocked the connect when that fetch failed.
internal static class SimpleConnectPolicy
{
    internal static SubscriptionInputAction DecideSubscriptionInput(string? input, IReadOnlyList<SubscriptionEntry>? existing)
    {
        var url = Normalize(input);
        if (url.Length == 0 || existing is null) return SubscriptionInputAction.Replace;

        foreach (var sub in existing)
        {
            if (sub is null || !sub.Enabled) continue;
            if (!string.Equals(Normalize(sub.Url), url, StringComparison.OrdinalIgnoreCase)) continue;
            return sub.Servers is { Count: > 0 } ? SubscriptionInputAction.KeepCached : SubscriptionInputAction.KeepAndRefresh;
        }

        return SubscriptionInputAction.Replace;
    }

    private static string Normalize(string? url) => (url ?? string.Empty).Trim().TrimEnd('/');

    // The probe of the selected server could not judge it (AmneziaWG, IPv6-only, unknown protocol): that is not a dead server, so Connect keeps
    // the user's choice instead of switching to another server and saving that.
    internal static bool KeepSelectedAfterProbe(IReadOnlyList<ServerLiveness> selectedProbe)
        => selectedProbe.Count > 0 && !selectedProbe[0].Judged;

    // The pre-flight of Connect: a server that is already selected and answers does not need the other twelve probed (a silent UDP port costs
    // two seconds each). Only when the selected server is missing, not answering, or the intent asks for a specific kind is the whole list probed.
    internal static bool ShouldProbeSelectedFirst(string? activeName, IEnumerable<VlessServerEntry> candidates, bool generalIntent)
        => generalIntent
           && !string.IsNullOrEmpty(activeName)
           && candidates.Any(c => string.Equals(c.Name, activeName, StringComparison.Ordinal));
}
