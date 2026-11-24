#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// A generated-mode DNSTT row can be a copy of a subscription grant: the user imported
// the dnstt:// link before the feed rotated its domain, Noise key or SSH fields. A later
// refresh must not replace an arbitrary generated row, so capture binds the old row to
// exactly one enabled HTTPS registration whose previous cache contains that exact grant
// (including the client credential) and exactly one matching outbound id. The apply step
// repeats the same checks against the live settings and the published feed.
internal static class DnsttSelectionRefresh
{
    internal enum RefreshOutcome
    {
        Invalid,

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

        var enabled = (settings.App?.Subscriptions ?? new List<SubscriptionEntry>())
            .Where(feed => feed is { Enabled: true })
            .ToList();
        // A second enabled registration could own a grant with the same opaque id. Do not choose one,
        // and DNSTT grants are imported only from an HTTPS response.
        if (enabled.Count != 1 || !IsHttps(enabled[0].Url)) return null;
        var feed = enabled[0];
        if (feed.Servers is not { Count: > 0 } previous) return null;

        var exact = previous
            .Where(row => row is not null && ProfileOf(row) == profile)
            .Take(2)
            .ToArray();
        if (exact.Length != 1) return null;
        if (previous.Count(row => string.Equals(row?.OutboundId, active.OutboundId, StringComparison.OrdinalIgnoreCase)) != 1)
            return null;

        return new Binding(feed.Id, feed.Url, previous, active, active.Name, active.OutboundId, profile);
    }

    // Evaluated before the feed replacement is published. Invalid means the replacement would
    // destroy the only binding to the old cache (selection/edit race, ambiguity or a missing grant),
    // so the caller must retain the previous cache.
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
        var matches = nextRows
            .Where(row => row is { Dnstt: not null } &&
                string.Equals(row.OutboundId, binding.OutboundId, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length != 1) return RefreshOutcome.Invalid;
        if (nextRows.Count(row => string.Equals(row?.OutboundId, binding.OutboundId, StringComparison.OrdinalIgnoreCase)) != 1)
            return RefreshOutcome.Invalid;

        var next = matches[0];
        if (ProfileOf(next) is not { } nextProfile)
            return RefreshOutcome.Invalid;
        if (string.IsNullOrWhiteSpace(next.Server) || next.Port is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(nextProfile.Domain) || string.IsNullOrWhiteSpace(nextProfile.NoiseKey) ||
            string.IsNullOrWhiteSpace(nextProfile.SshHostKey) || string.IsNullOrWhiteSpace(nextProfile.SshPrivateKey))
            return RefreshOutcome.Invalid;

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

    private static bool IsGenerated(string? mode) =>
        (mode ?? "generated").Trim().Equals("generated", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpaqueGrantId(string? outboundId) =>
        !string.IsNullOrWhiteSpace(outboundId) &&
        outboundId.StartsWith("dnstt:", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
