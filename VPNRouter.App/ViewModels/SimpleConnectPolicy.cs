using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

internal enum SubscriptionInputAction
{
    // The pasted URL is a subscription we already hold with cached servers: nothing to replace, nothing to wait for.
    KeepCached,

    // We hold it but have no servers from it yet: refresh it (in place), do not replace the entry.
    KeepAndRefresh,

    Disabled,

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

        var disabledMatch = false;
        foreach (var sub in existing)
        {
            if (sub is null || !string.Equals(Normalize(sub.Url), url, StringComparison.Ordinal)) continue;
            if (!sub.Enabled)
            {
                disabledMatch = true;
                continue;
            }
            return sub.Servers is { Count: > 0 } ? SubscriptionInputAction.KeepCached : SubscriptionInputAction.KeepAndRefresh;
        }

        return disabledMatch ? SubscriptionInputAction.Disabled : SubscriptionInputAction.Replace;
    }

    internal static bool SameUrl(string? left, string? right) => Normalize(left) == Normalize(right);

    private static string Normalize(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return text;
        return uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri.AbsoluteUri.TrimEnd('/') : uri.AbsoluteUri;
    }

    // UDP based protocols answer a blind datagram never (QUIC) and the probe waits its whole timeout for nothing; AmneziaWG is not probed at all.
    // Probing such a selected server only costs the wait (2 s) and then a pointless probe of the whole list: keep it, connecting tells.
    internal static bool CannotBeVerifiedByProbe(VlessServerEntry server)
        => (server.Protocol ?? "vless").Trim().ToLowerInvariant() is "hysteria2" or "hy2" or "tuic" or "amneziawg" or "awg";

    // The probe of the selected server could not judge it (AmneziaWG, IPv6-only, unknown protocol): that is not a dead server, so Connect keeps
    // the user's choice instead of switching to another server and saving that.
    internal static bool KeepSelectedAfterProbe(IReadOnlyList<ServerLiveness> selectedProbe)
        => selectedProbe.Count > 0 && !selectedProbe[0].Judged;

    // The pre-flight of Connect: a server that is already selected and answers does not need the other twelve probed (a silent UDP port costs
    // two seconds each). Only when the selected server is missing, not answering, or the intent asks for a specific kind is the whole list probed.
    internal static bool ShouldProbeSelectedFirst(string? activeName, IEnumerable<VlessServerEntry> candidates,
        bool generalIntent, VlessServerEntry? selected = null) =>
        generalIntent && (selected != null ? candidates.Contains(selected) :
            !string.IsNullOrEmpty(activeName) && candidates.Any(c => c.Name == activeName));
}
