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
    internal const string UiCommand = "vpn_ui_command";
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
        bool superseded = selectingCommand != null && selectingCommand != read(UiCommand);
        RemoveSupersededCopies(refreshed.Url, oldRows, subscriptions, read, options, updates,
            preserveSelection: superseded && !rejected, rejected: rejected);
        bool selected = !rejected && selectingCommand != null && !superseded;
        if (selected)
        {
            affected[0].Enabled = true;
            updates[SelectedName] = refreshed.Servers[0].Name;
            updates[SubscriptionUrl] = refreshed.Url;
            updates[ManualUri] = null;
            updates[LegacyManualOwner] = null;
            updates[ConfigMode] = "subscribe";
        }

        updates[Subscriptions] = JsonSerializer.Serialize(subscriptions, options);
        commit(updates);
        return selected;
    }

    internal static bool SaveSingleServer(VlessServerEntry server, string uri, string command,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (read(UiCommand) != command) return false;
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
        var index = VPNRouter.Core.Services.FreeConfigs.FreeConfigSelection.SelectOrAdd(servers, server);
        updates[StandaloneServers] = JsonSerializer.Serialize(servers, options);
        updates[SelectedName] = servers[index].Name;
        updates[ManualUri] = uri;
        updates[LegacyManualOwner] = null;
        updates[SubscriptionUrl] = null;
        updates[ConfigMode] = "manual";
        commit(updates);
        return true;
    }

    internal static VlessServerEntry? ResolveActiveServer(string? selectedName, List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string mode, string? manualUri, Action<string> rememberFallback)
    {
        // Earlier Simple saves kept the explicitly typed URI as the only manual selection.
        if (mode == "manual" && string.IsNullOrEmpty(selectedName) && standalone.Count == 0 &&
            !string.IsNullOrWhiteSpace(manualUri))
        {
            var manual = ServerUriParser.TryParse(manualUri);
            if (manual != null) manual.UseAsSingleOutbound = true;
            return manual;
        }

        var available = standalone.Concat(subscriptions.Where(s => s is { Enabled: true, Servers: not null })
            .SelectMany(s => s.Servers)).Where(s => s != null);
        if (!string.IsNullOrEmpty(selectedName) && available.FirstOrDefault(s =>
                string.Equals(s.Name, selectedName, StringComparison.OrdinalIgnoreCase)) is { } selected)
            return selected;
        if (available.FirstOrDefault() is { } first)
        {
            if (!string.IsNullOrEmpty(first.Name)) rememberFallback(first.Name);
            return first;
        }
        return ServerUriParser.TryParse(manualUri ?? string.Empty);
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
