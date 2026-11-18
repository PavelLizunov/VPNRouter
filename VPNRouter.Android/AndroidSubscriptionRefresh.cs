using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

internal static class AndroidSubscriptionRefresh
{
    internal const string Subscriptions = "subscriptions_json";
    internal const string StandaloneServers = "servers_json";
    internal const string SelectedName = "selected_server_name";
    internal const string SubscriptionUrl = "subscription_url";
    internal const string ManualUri = "vless_uri";
    internal const string ConfigMode = "config_mode";

    private static readonly Dictionary<string, Task> PendingRefreshes = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock WriteLock = new();

    internal static async Task<int> RefreshAsync(SubscriptionEntry entry, Action<SubscriptionEntry, bool> persist)
    {
        // Keep the request tied to the URL it actually fetched if the user edits the feed while waiting.
        var candidate = new SubscriptionEntry { Id = entry.Id, Url = entry.Url };
        // QR and page refresh can overlap. Commit each response before fetching the same source again.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (PendingRefreshes)
        {
            previous = PendingRefreshes.GetValueOrDefault(candidate.Url, Task.CompletedTask);
            PendingRefreshes[candidate.Url] = finished.Task;
        }
        try
        {
            await previous;
            return await FetchAndSaveAsync(candidate, persist);
        }
        finally
        {
            lock (PendingRefreshes)
            {
                if (PendingRefreshes[candidate.Url] == finished.Task) PendingRefreshes.Remove(candidate.Url);
                finished.SetResult();
            }
        }
    }

    private static async Task<int> FetchAndSaveAsync(SubscriptionEntry candidate, Action<SubscriptionEntry, bool> persist)
    {
        int count;
        try
        {
            count = await Task.Run(() => SubscriptionFetcher.RefreshEntryAsync(candidate));
        }
        catch (UnsupportedSubscriptionChainException)
        {
            persist(candidate, true);
            throw;
        }
        if (count > 0) persist(candidate, false);
        return count;
    }

    internal static void Save(SubscriptionEntry refreshed, bool rejected, bool selectOnSuccess,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        using var write = WriteLock.EnterScope();
        var json = read(Subscriptions);
        List<SubscriptionEntry> subscriptions;
        if (string.IsNullOrWhiteSpace(json) && read(SubscriptionUrl) == refreshed.Url)
        {
            // The old single-feed format kept its cache in servers_json and had no persistent ID.
            subscriptions = [new SubscriptionEntry
            {
                Id = refreshed.Id, Name = "Default", Url = refreshed.Url,
                Servers = ReadStandalone(read, options)
            }];
        }
        else
        {
            subscriptions = JsonSerializer.Deserialize<List<SubscriptionEntry>>(json ?? "[]", options) ?? [];
        }

        var affected = subscriptions.Where(s => AppliesTo(s, refreshed, rejected)).ToList();
        if (affected.Count == 0) throw new InvalidOperationException("Subscription changed during refresh.");
        var oldRows = affected.SelectMany(s => s.Servers ?? []).Where(row => row != null).ToList();
        foreach (var current in affected) CopyCache(refreshed, current);

        var updates = new Dictionary<string, string?>();
        // Once a registration is replaced, a later rejection cannot identify its former standalone copies.
        RemoveSupersededCopies(refreshed.Url, oldRows, subscriptions, read, options, updates);
        if (!rejected && selectOnSuccess)
        {
            affected[0].Enabled = true;
            updates[SelectedName] = refreshed.Servers[0].Name;
            updates[SubscriptionUrl] = refreshed.Url;
            updates[ManualUri] = null;
            updates[ConfigMode] = "subscribe";
        }

        updates[Subscriptions] = JsonSerializer.Serialize(subscriptions, options);
        commit(updates);
    }

    internal static bool AppliesTo(SubscriptionEntry? registration, SubscriptionEntry refreshed, bool rejected) =>
        registration != null && registration.Url == refreshed.Url && (rejected || registration.Id == refreshed.Id);

    internal static void CopyCache(SubscriptionEntry refreshed, SubscriptionEntry current)
    {
        current.Servers = [.. refreshed.Servers];
        current.LastServerCount = refreshed.LastServerCount;
        current.LastRefreshedAt = refreshed.LastRefreshedAt;
        if (refreshed.UserInfo != null) current.UserInfo = refreshed.UserInfo;
    }

    private static List<VlessServerEntry> ReadStandalone(Func<string, string?> read, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<List<VlessServerEntry>>(read(StandaloneServers) ?? "[]", options) ?? [];

    private static void RemoveSupersededCopies(string url, List<VlessServerEntry> oldRows,
        List<SubscriptionEntry> subscriptions, Func<string, string?> read, JsonSerializerOptions options,
        Dictionary<string, string?> updates)
    {
        if (oldRows.Count == 0) return;

        // A name or endpoint alone can also identify an independently saved manual server.
        string Identity(VlessServerEntry row) => JsonSerializer.Serialize(row, options);
        var oldCopies = oldRows.Select(Identity).ToHashSet(StringComparer.Ordinal);
        foreach (var subscription in subscriptions)
        {
            if (subscription?.Servers != null)
                oldCopies.ExceptWith(subscription.Servers.Where(row => row != null).Select(Identity));
        }

        var manual = ServerUriParser.TryParse(read(ManualUri) ?? string.Empty);
        if (manual != null)
        {
            var manualKey = Identity(manual);
            if (oldCopies.Contains(manualKey) && read(SubscriptionUrl) == url && read(ConfigMode) is null or "subscribe")
            {
                updates[ManualUri] = null;
                manual = null;
            }
            else
            {
                oldCopies.Remove(manualKey);
            }
        }
        var standalone = ReadStandalone(read, options);
        if (standalone.RemoveAll(row => oldCopies.Contains(Identity(row))) > 0)
            updates[StandaloneServers] = JsonSerializer.Serialize(standalone, options);

        var selected = read(SelectedName);
        bool IsSelected(VlessServerEntry row) => string.Equals(row?.Name, selected, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(selected) && oldRows.Any(IsSelected)
            && !standalone.Any(IsSelected) && !subscriptions.Any(s => s is { Enabled: true } && s.Servers?.Any(IsSelected) == true)
            && (manual == null || !IsSelected(manual)))
            updates[SelectedName] = null;
    }
}
