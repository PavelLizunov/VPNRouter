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
    internal const string SelectedFeedId = "selected_subscription_id";
    internal const string SelectedFeedRow = "selected_subscription_row";
    internal const string ManualUri = "vless_uri";
    internal const string ConfigMode = "config_mode";
    internal const string UiCommand = "vpn_ui_command";
    internal const string CustomJson = "custom_config_json";
    internal const string CustomName = "custom_config_name";
    internal const string SupersededSelection = "vpn_superseded_selection";
    internal const string LegacyManualOwner = "subscription_manual_owner_hash";

    private static readonly Dictionary<string, Task> PendingRefreshes = new(StringComparer.Ordinal);

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

    // The caller holds the preference transaction through read, selection check and commit.
    internal static bool Save(SubscriptionEntry refreshed, bool rejected, string? selectingCommand,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
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
        bool superseded = selectingCommand != null && !IsCurrentSelection(selectingCommand, read);
        RemoveSupersededCopies(refreshed.Url, oldRows, subscriptions, read, options, updates,
            preserveSelection: superseded && !rejected, rejected: rejected);
        bool selected = !rejected && selectingCommand != null && !superseded;
        if (selected)
        {
            affected[0].Enabled = true;
            SelectFeedRow(updates, affected[0], refreshed.Servers[0]);
        }

        updates[Subscriptions] = JsonSerializer.Serialize(subscriptions, options);
        commit(updates);
        return selected;
    }

    internal static bool SaveSingleServer(VlessServerEntry server, string uri, string command,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (!IsCurrentSelection(command, read)) return false;
        var servers = ReadStandalone(read, options);
        var updates = new Dictionary<string, string?>();
        // Removing the old scalar URL must not sever its ownership of legacy cached rows.
        if (string.IsNullOrWhiteSpace(read(Subscriptions)) && read(SubscriptionUrl) is { Length: > 0 } previousUrl)
        {
            var legacy = new SubscriptionEntry { Id = "legacy-default", Name = "Default", Url = previousUrl, Servers = servers };
            updates[Subscriptions] = JsonSerializer.Serialize(new List<SubscriptionEntry> { legacy }, options);
        }
        // Earlier clients kept a duplicate of the superseded scalar in the standalone list.
        // Its digest still identifies it until this transaction changes the active selection.
        if (ReadRetainedManual(read) is { } retired)
        {
            var identity = JsonSerializer.Serialize(retired, options);
            servers.RemoveAll(row => JsonSerializer.Serialize(row, options) == identity);
        }
        SelectManualRow(updates, servers, server, uri, options);
        commit(updates);
        return true;
    }

    private static void SelectManualRow(Dictionary<string, string?> updates, List<VlessServerEntry> servers,
        VlessServerEntry server, string uri, JsonSerializerOptions options)
    {
        var index = VPNRouter.Core.Services.FreeConfigs.FreeConfigSelection.SelectOrAdd(servers, server);
        updates[SelectedFeedId] = null;
        updates[SelectedFeedRow] = null;
        updates[StandaloneServers] = JsonSerializer.Serialize(servers, options);
        updates[SelectedName] = servers[index].Name;
        updates[ManualUri] = uri;
        updates[LegacyManualOwner] = null;
        updates[SubscriptionUrl] = null;
        updates[ConfigMode] = "manual";
    }

    internal static bool IsCurrentSelection(string? command, Func<string, string?> read) =>
        command != null && command == read(UiCommand) && command != read(SupersededSelection);

    internal static string RowIdentity(VlessServerEntry row) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(row, VPNRouter.Core.Json.AppJsonContext.Default.VlessServerEntry)));

    private static void SelectFeedRow(Dictionary<string, string?> updates, SubscriptionEntry feed, VlessServerEntry row)
    {
        updates[SelectedName] = row.Name;
        updates[SelectedFeedId] = feed.Id;
        updates[SelectedFeedRow] = RowIdentity(row);
        updates[SubscriptionUrl] = feed.Url;
        updates[ManualUri] = null;
        updates[LegacyManualOwner] = null;
        updates[ConfigMode] = "subscribe";
    }

    internal static bool SaveFeedSelection(SubscriptionEntry source, VlessServerEntry row, string? command,
        List<SubscriptionEntry> subscriptions, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (command != null && !IsCurrentSelection(command, read)) return false;
        var feed = subscriptions.FirstOrDefault(s => s != null && s.Id == source.Id && s.Url == source.Url);
        var identity = RowIdentity(row);
        if (feed?.Servers == null || !feed.Servers.Any(s => s != null && s.Name == row.Name && RowIdentity(s) == identity))
            return false;
        feed.Enabled = true;
        var changes = new Dictionary<string, string?> { [Subscriptions] = JsonSerializer.Serialize(subscriptions, options) };
        if (command == null) changes[SupersededSelection] = read(UiCommand);
        SelectFeedRow(changes, feed, row);
        commit(changes);
        return true;
    }

    internal static void ImportProfile(ConfigShareDocument doc, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        var mode = (doc.ConfigMode ?? "subscribe").ToLowerInvariant();
        var feeds = doc.Subscriptions ?? [];
        var changes = new Dictionary<string, string?>
        {
            [SupersededSelection] = read(UiCommand),
            [ConfigMode] = mode,
            [Subscriptions] = JsonSerializer.Serialize(feeds, options),
            [SelectedName] = null, [SelectedFeedId] = null, [SelectedFeedRow] = null,
            [SubscriptionUrl] = null, [LegacyManualOwner] = null, [ManualUri] = null
        };
        if (mode == "manual")
        {
            if (string.IsNullOrWhiteSpace(doc.ManualVlessUri))
                changes[ManualUri] = string.Empty; // An exported empty profile has no active server.
            else
                SelectManualRow(changes, ReadStandalone(read, options), ServerUriParser.Parse(doc.ManualVlessUri),
                    doc.ManualVlessUri, options);
        }
        else if (mode == "subscribe")
        {
            var enabled = feeds.Where(f => f is { Enabled: true, Servers.Count: > 0 }).ToList();
            var source = enabled.Find(f => read(SelectedFeedId) == f.Id && read(SubscriptionUrl) == f.Url)
                ?? enabled.Find(f => read(SubscriptionUrl) == f.Url) ?? enabled.FirstOrDefault();
            if (source != null)
            {
                var active = source.Servers.Find(s => s != null && s.Name == read(SelectedName) &&
                    RowIdentity(s) == read(SelectedFeedRow));
                SelectFeedRow(changes, source, active ?? source.Servers[0]);
            }
            else changes[SubscriptionUrl] = feeds.FirstOrDefault(f => f?.Enabled == true)?.Url;
        }
        else if (mode == "custom" && doc.CustomConfig != null)
        {
            changes[CustomJson] = doc.CustomConfig.SingBoxJson;
            changes[CustomName] = doc.CustomConfig.Name;
        }
        else throw new FormatException("Invalid connection profile mode or payload.");
        commit(changes);
    }

    internal static VlessServerEntry? ResolveActiveServer(List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string mode, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        var name = read(SelectedName);
        if (ReadRetainedManual(read) is { } retained &&
            (string.IsNullOrEmpty(name) || string.Equals(name, retained.Name, StringComparison.OrdinalIgnoreCase)))
        {
            retained.UseAsSingleOutbound = true;
            return retained;
        }

        if (mode == "manual" && read(ManualUri) is { } uri)
        {
            var manual = ServerUriParser.TryParse(uri);
            if (manual == null) return null;
            // Old Simple saves could persist the fallback feed name when drawing the summary.
            var previous = standalone.Find(s => s != null && s.Name == name && s.UseAsSingleOutbound);
            var index = VPNRouter.Core.Services.FreeConfigs.FreeConfigSelection.SelectOrAdd(standalone, manual);
            var selected = standalone[index];
            if (!ReferenceEquals(previous, selected))
                commit(new Dictionary<string, string?>
                {
                    [StandaloneServers] = JsonSerializer.Serialize(standalone, options), [SelectedName] = selected.Name
                });
            return selected;
        }

        var feeds = subscriptions.Where(f => f is { Enabled: true, Servers: not null });
        var feedId = read(SelectedFeedId);
        var url = read(SubscriptionUrl);
        var scoped = mode == "subscribe" && (feedId != null || !string.IsNullOrEmpty(url));
        if (scoped)
            feeds = feeds.Where(f => (feedId == null || f.Id == feedId) && f.Url == url);
        var feedRows = feeds.SelectMany(f => f.Servers).Where(s => s != null);
        var available = mode == "subscribe" ? feedRows : standalone.Concat(feedRows);
        if (!string.IsNullOrEmpty(name))
        {
            var matches = available.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                var identity = scoped ? read(SelectedFeedRow) : null;
                return identity == null ? matches[0] : matches.Find(s => RowIdentity(s) == identity);
            }
        }
        var first = available.FirstOrDefault();
        if (first == null) return mode == "subscribe" ? null : ServerUriParser.TryParse(read(ManualUri) ?? string.Empty);
        if (!string.IsNullOrEmpty(first.Name) && first.Name != name)
            commit(new Dictionary<string, string?> { [SelectedName] = first.Name, [SelectedFeedRow] = null });
        return first;
    }

    internal static void PutSelectedServerFirst(List<VlessServerEntry> pool, VlessServerEntry selected)
    {
        // The generator finds its active row by name before applying the auto-select policy.
        var identity = RowIdentity(selected);
        pool.RemoveAll(row => row.Name == selected.Name && RowIdentity(row) == identity);
        pool.Insert(0, selected);
    }

    internal static bool HasServerInputEdit(string? displayed, string? saved)
    {
        var input = displayed?.Trim();
        return !string.IsNullOrEmpty(input) && !string.Equals(input, saved?.Trim(), StringComparison.Ordinal);
    }

    private static string ManualOwner(string url, string uri) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url + "\0" + uri)));

    internal static VlessServerEntry? ReadRetainedManual(Func<string, string?> read)
    {
        var owner = read(LegacyManualOwner);
        if (owner == null || read(ConfigMode) is not (null or "subscribe") ||
            read(SubscriptionUrl) is not { Length: > 0 } url || read(ManualUri) is not { Length: > 0 } uri ||
            owner != ManualOwner(url, uri)) return null;
        return ServerUriParser.TryParse(uri);
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
        Dictionary<string, string?> updates, bool preserveSelection, bool rejected)
    {
        var recordedOwner = read(LegacyManualOwner);
        if (oldRows.Count == 0 && !(rejected && recordedOwner != null)) return;

        // A name or endpoint alone can also identify an independently saved manual server.
        string Identity(VlessServerEntry row) => JsonSerializer.Serialize(row, options);
        var oldCopies = oldRows.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var retained = subscriptions.OfType<SubscriptionEntry>().SelectMany(s => s.Servers ?? [])
            .Where(row => row != null).Select(Identity).ToHashSet(StringComparer.Ordinal);
        oldCopies.ExceptWith(retained);

        var uri = read(ManualUri);
        var manual = ServerUriParser.TryParse(uri ?? string.Empty);
        string? retiredManualName = null;
        bool keepScalarOnly = false;
        if (manual != null)
        {
            var manualKey = Identity(manual);
            if (read(SubscriptionUrl) == url && read(ConfigMode) is null or "subscribe")
            {
                // A superseded success keeps the selected legacy URI but replaces the cache that
                // identified its owner. Retain only a digest so a later rejection can still clear it.
                var owner = ManualOwner(url, uri!);
                if (rejected && recordedOwner == owner && !retained.Contains(manualKey)) oldCopies.Add(manualKey);
                if (oldCopies.Contains(manualKey))
                {
                    if (preserveSelection)
                    {
                        updates[LegacyManualOwner] = owner;
                        keepScalarOnly = true;
                    }
                    else
                    {
                        updates[ManualUri] = null;
                        updates[LegacyManualOwner] = null;
                        retiredManualName = manual.Name;
                        manual = null;
                    }
                }
            }
            if (manual != null && !keepScalarOnly) oldCopies.Remove(manualKey);
        }
        var standalone = ReadStandalone(read, options);
        if (standalone.RemoveAll(row => oldCopies.Contains(Identity(row))) > 0)
            updates[StandaloneServers] = JsonSerializer.Serialize(standalone, options);

        if (preserveSelection) return;
        var selected = read(SelectedName);
        bool IsSelected(VlessServerEntry row) => string.Equals(row?.Name, selected, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(selected) && (oldRows.Any(IsSelected) ||
                string.Equals(selected, retiredManualName, StringComparison.OrdinalIgnoreCase))
            && !standalone.Any(IsSelected) && !subscriptions.Any(s => s is { Enabled: true } && s.Servers?.Any(IsSelected) == true)
            && (manual == null || !IsSelected(manual)))
            updates[SelectedName] = null;
    }
}
