#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core.Json;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// A generated-mode DNSTT row can be a copy of a subscription grant: the user imported
// the dnstt:// link before the feed rotated its domain, Noise key or SSH fields. A later
// refresh must not replace an arbitrary generated row, so capture binds the old row to
// exactly one enabled HTTPS registration and an exact old grant, including the client
// credential. A saved binding hash retains that match when a feed displaces another row.
// The apply step checks the live selection and requires a unique replacement id.
internal static class DnsttSelectionRefresh
{
    internal enum RefreshOutcome
    {
        // The binding no longer describes the live selection, settings or feed registrations.
        Invalid,

        // The feed could not be fetched or parsed. An empty body or zero parsed rows is not
        // proof that the grant was revoked, so the caller keeps the old cache and may retry.
        FetchFailed,

        // The feed parsed successfully but no longer advertises the selected outbound id.
        Missing,

        // The feed parsed successfully but advertises more than one matching outbound id or
        // an unusable replacement, so it cannot be reconciled safely.
        Ambiguous,

        Unchanged,

        Applied,
    }

    internal readonly record struct GrantProfile(
        string OutboundId,
        string Protocol,
        string Security,
        string Server,
        int Port,
        string Domain,
        string NoiseKey,
        string SshHostKey,
        string SshPrivateKey);

    internal sealed record Binding(
        string FeedId,
        string FeedUrl,
        IReadOnlyList<VlessServerEntry> PreviousRows,
        VlessServerEntry Active,
        string ActiveName,
        string OutboundId,
        GrantProfile Profile);

    internal static string? KeyOf(Binding? binding) =>
        binding == null ? null : BindingHash(binding.FeedId, binding.FeedUrl, binding.Profile);

    private static string BindingHash(string feedId, string url, GrantProfile profile)
    {
        List<string> fields = [feedId, url, profile.OutboundId, profile.Protocol, profile.Security,
            profile.Server, profile.Port.ToString(CultureInfo.InvariantCulture), profile.Domain,
            profile.NoiseKey, profile.SshHostKey, profile.SshPrivateKey];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields, AppJsonContext.Default.ListString);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static SubscriptionEntry? SingleFeed(AppSettings settings)
    {
        var enabled = settings.App?.Subscriptions?.Where(feed => feed is { Enabled: true }).Take(2).ToArray();
        return enabled is { Length: 1 } && IsHttps(enabled[0].Url) ? enabled[0] : null;
    }

    private static bool MatchesCachedGrant(IEnumerable<VlessServerEntry> rows, GrantProfile profile)
    {
        var matches = rows.Where(row => string.Equals(row?.OutboundId, profile.OutboundId,
            StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 && ProfileOf(matches[0]) == profile;
    }

    internal static Binding? Capture(AppSettings settings)
    {
        if (settings?.Vless?.Servers is not { } servers ||
            !IsGenerated(settings.App?.ConfigMode) ||
            string.IsNullOrEmpty(settings.Vless.ActiveServer))
            return null;

        var selected = servers
            .Where(row => row is not null &&
                string.Equals(row.Name, settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (selected.Length != 1) return null;
        var active = selected[0];
        if (!IsOpaqueGrantId(active.OutboundId) || ProfileOf(active) is not { } profile)
            return null;
        if (servers.Count(row => string.Equals(row?.OutboundId, active.OutboundId, StringComparison.OrdinalIgnoreCase)) != 1)
            return null;

        var feed = SingleFeed(settings);
        if (feed?.Servers is not { } previous) return null;
        if (!MatchesCachedGrant(previous, profile) &&
            active.DnsttRefreshBinding != BindingHash(feed.Id, feed.Url, profile)) return null;

        return new Binding(feed.Id, feed.Url, previous, active, active.Name, active.OutboundId, profile);
    }

    internal static bool MatchesRegisteredGrant(AppSettings settings, VlessServerEntry candidate)
    {
        if (!IsOpaqueGrantId(candidate.OutboundId) || ProfileOf(candidate) is not { } profile ||
            SingleFeed(settings) is not { Servers: not null } feed) return false;
        if (MatchesCachedGrant(feed.Servers, profile)) return true;
        var key = BindingHash(feed.Id, feed.Url, profile);
        return settings.Vless.Servers.Any(row => row?.DnsttRefreshBinding == key && ProfileOf(row) == profile);
    }

    internal static bool SameGrant(VlessServerEntry? left, VlessServerEntry? right) =>
        ProfileOf(left) is { } profile && ProfileOf(right) == profile;

    // Keep proof of the previous exact match for other saved rows if this publication
    // removes or rotates them. Only the selected row may receive replacement credentials.
    internal static void RememberDisplacedGrants(AppSettings settings, SubscriptionCacheUpdate update)
    {
        if (update.Failed || !IsGenerated(settings.App.ConfigMode) ||
            !ReferenceEquals(SingleFeed(settings), update.Origin) ||
            !settings.Vless.Servers.Any(row => row?.Dnstt != null)) return;
        var previous = update.PreviousRows.ToLookup(row => row?.OutboundId, StringComparer.OrdinalIgnoreCase);
        var current = update.Candidate.Servers.ToLookup(row => row?.OutboundId, StringComparer.OrdinalIgnoreCase);
        foreach (var row in settings.Vless.Servers)
        {
            if (row?.Dnstt == null || !IsOpaqueGrantId(row.OutboundId) || ProfileOf(row) is not { } profile)
                continue;
            if (MatchesCachedGrant(previous[profile.OutboundId], profile) &&
                !MatchesCachedGrant(current[profile.OutboundId], profile))
                row.DnsttRefreshBinding = BindingHash(update.Origin.Id, update.Origin.Url, profile);
        }
    }

    // Evaluated before the feed replacement is published. Invalid means the replacement would
    // destroy the only binding to the old cache (selection/edit race or a changed registration),
    // so the caller must retain the previous cache. FetchFailed is a transient or unparseable
    // answer (including an empty body); only a successfully parsed feed can report Missing or
    // Ambiguous. Both are blocking for a manual connect because the old credential is no longer
    // known to be valid.
    internal static RefreshOutcome Evaluate(
        AppSettings settings,
        Binding? binding,
        SubscriptionCacheUpdate update)
    {
        if (binding == null || update is null || settings?.Vless?.Servers is not { } servers)
            return RefreshOutcome.Invalid;
        if (!IsGenerated(settings.App?.ConfigMode))
            return RefreshOutcome.Invalid;

        // The published registration must still be the one whose previous cache was captured.
        if (!string.Equals(update.Origin.Id, binding.FeedId, StringComparison.Ordinal) ||
            !string.Equals(update.Candidate.Id, binding.FeedId, StringComparison.Ordinal) ||
            !string.Equals(update.Origin.Url, binding.FeedUrl, StringComparison.Ordinal) ||
            !string.Equals(update.Candidate.Url, binding.FeedUrl, StringComparison.Ordinal) ||
            !update.Origin.Enabled || !update.Candidate.Enabled)
            return RefreshOutcome.Invalid;
        if (!ReferenceEquals(update.PreviousRows, binding.PreviousRows))
            return RefreshOutcome.Invalid;

        var feed = settings.App?.Subscriptions?.FirstOrDefault(entry => entry?.Id == binding.FeedId);
        if (feed == null || !ReferenceEquals(feed, update.Origin) ||
            !feed.Enabled || !string.Equals(feed.Url, binding.FeedUrl, StringComparison.Ordinal) ||
            !IsHttps(feed.Url))
            return RefreshOutcome.Invalid;
        var enabled = (settings.App?.Subscriptions ?? new List<SubscriptionEntry>())
            .Where(entry => entry is { Enabled: true })
            .ToList();
        if (enabled.Count != 1 || !ReferenceEquals(enabled[0], feed))
            return RefreshOutcome.Invalid;
        // Before publishing, the registration still owns the captured cache; after publishing,
        // it owns the candidate list. A third list means another refresh won the race.
        if (!ReferenceEquals(feed.Servers, binding.PreviousRows) &&
            !ReferenceEquals(feed.Servers, update.Candidate.Servers))
            return RefreshOutcome.Invalid;

        // The selected row must still be the exact old grant; a user edit or another
        // selection invalidates the binding instead of being overwritten.
        if (!string.Equals(settings.Vless.ActiveServer, binding.ActiveName, StringComparison.OrdinalIgnoreCase))
            return RefreshOutcome.Invalid;
        if (servers.Count(row => row is not null &&
                string.Equals(row.Name, binding.ActiveName, StringComparison.OrdinalIgnoreCase)) != 1 ||
            servers.Count(row => string.Equals(row?.OutboundId, binding.OutboundId, StringComparison.OrdinalIgnoreCase)) != 1)
            return RefreshOutcome.Invalid;
        var active = servers.FirstOrDefault(row => ReferenceEquals(row, binding.Active));
        if (active == null || ProfileOf(active) != binding.Profile)
            return RefreshOutcome.Invalid;

        if (update.Candidate.Servers is not { } nextRows)
            return RefreshOutcome.Invalid;
        // FetchAsync marks a failed fetch, an empty body and a body that parsed to zero usable
        // rows as Failed. None of those is an authoritative removal of the grant, so keep the
        // old cache and let the next ordinary refresh or connect retry.
        if (update.Failed)
            return RefreshOutcome.FetchFailed;

        var matches = nextRows
            .Where(row => row is { Dnstt: not null } &&
                string.Equals(row.OutboundId, binding.OutboundId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length == 0) return RefreshOutcome.Missing;
        if (matches.Length != 1) return RefreshOutcome.Ambiguous;
        if (nextRows.Count(row => string.Equals(row?.OutboundId, binding.OutboundId, StringComparison.OrdinalIgnoreCase)) != 1)
            return RefreshOutcome.Ambiguous;

        var next = matches[0];
        if (ProfileOf(next) is not { } nextProfile)
            return RefreshOutcome.Ambiguous;
        if (string.IsNullOrWhiteSpace(next.Server) || next.Port is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(nextProfile.Domain) || string.IsNullOrWhiteSpace(nextProfile.NoiseKey) ||
            string.IsNullOrWhiteSpace(nextProfile.SshHostKey) || string.IsNullOrWhiteSpace(nextProfile.SshPrivateKey))
            return RefreshOutcome.Ambiguous;

        return nextProfile == binding.Profile ? RefreshOutcome.Unchanged : RefreshOutcome.Applied;
    }

    internal static bool TryApply(
        AppSettings settings,
        Binding? binding,
        SubscriptionCacheUpdate update,
        ILogger? logger = null)
    {
        if (Evaluate(settings, binding, update) != RefreshOutcome.Applied)
            return false;

        var active = binding!.Active;
        var next = FindNext(update, binding.OutboundId);
        if (next?.Dnstt is not { } rotated)
            return false;

        // Keep the user's label. The opaque id, endpoint and grant fields are the profile.
        active.OutboundId = next.OutboundId;
        active.Server = next.Server;
        active.Port = next.Port;
        active.Protocol = next.Protocol;
        active.Security = next.Security;
        active.Dnstt = new DnsttProfile
        {
            Domain = rotated.Domain,
            NoiseKey = rotated.NoiseKey,
            SshHostKey = rotated.SshHostKey,
            SshPrivateKey = rotated.SshPrivateKey,
        };
        logger?.Information(
            "[DnsttSelectionRefresh] Updated the selected generated DNSTT profile from its subscription");
        return true;
    }

    private static VlessServerEntry? FindNext(SubscriptionCacheUpdate update, string outboundId)
    {
        var matches = update.Candidate.Servers
            .Where(row => row is { Dnstt: not null } &&
                string.Equals(row.OutboundId, outboundId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static GrantProfile? ProfileOf(VlessServerEntry? entry)
    {
        if (entry?.Dnstt is not { } grant) return null;
        return new GrantProfile(
            entry.OutboundId ?? string.Empty,
            entry.Protocol ?? string.Empty,
            entry.Security ?? string.Empty,
            entry.Server ?? string.Empty,
            entry.Port,
            grant.Domain ?? string.Empty,
            grant.NoiseKey ?? string.Empty,
            grant.SshHostKey ?? string.Empty,
            grant.SshPrivateKey ?? string.Empty);
    }

    internal static bool IsGenerated(string? mode) =>
        (mode ?? "generated").Trim().Equals("generated", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpaqueGrantId(string? outboundId) =>
        !string.IsNullOrWhiteSpace(outboundId) &&
        outboundId.StartsWith("dnstt:", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
