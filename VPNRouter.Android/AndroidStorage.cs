using Android.App;
using Android.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Android.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Android;

public static class AndroidStorage
{
    private const string PrefsName = "vpnrouter_settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AndroidJsonContext.Default,
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    private const string KeyVlessUri = AndroidSubscriptionRefresh.ManualUri;
    private const string KeySubscriptionUrl = AndroidSubscriptionRefresh.SubscriptionUrl;
    private const string KeyServersJson = AndroidSubscriptionRefresh.StandaloneServers;
    private const string KeySelectedServerName = AndroidSubscriptionRefresh.SelectedName;
    private const string KeyLanguage = "language";
    private const string KeyTheme = "theme";
    private const string KeySubscriptions = AndroidSubscriptionRefresh.Subscriptions;

    private const string KeyV4SubServersPruneDone = "v4_sub_servers_prune_done";

    private const string KeyV4PlaceholderPruneDone = "v4_placeholder_prune_done";
    private const string KeyPlaceholderPruneCount = "v4_placeholder_prune_count";

    private const string KeyConfigMode = AndroidSubscriptionRefresh.ConfigMode;
    private const string KeyCustomConfigJson = AndroidSubscriptionRefresh.CustomJson;
    private const string KeyCustomConfigName = AndroidSubscriptionRefresh.CustomName;

    public static string GetConfigMode() => AndroidSubscriptionRefresh.ReadMode(GetString);
    internal static string SavedServerInput() =>
        AndroidSubscriptionRefresh.SavedServerInput(GetConfigMode(), GetString, GetSubscriptions);

    internal static string? SubscriptionToRefresh(string input) =>
        AndroidSubscriptionRefresh.SubscriptionToRefresh(input, GetConfigMode(), GetString, GetSubscriptions);

    public static bool SetConfigMode(string value) => SetString(KeyConfigMode, value);

    private const string KeyTunnelLive = "tunnel_live";

    public static bool GetTunnelLive() => GetBool(KeyTunnelLive, false);

    internal static string? GetDnsttReadiness() =>
        AndroidDnsttReadiness.Validated(GetString("vpn_dnstt_readiness"));

    internal const string UiCommandKey = AndroidSubscriptionRefresh.UiCommand;
    internal const string StateEventKey = "vpn_state_event";

    internal static string? BeginUiCommand()
    {
        try
        {
            return WithCommandPreferences(prefs =>
            {
                string id = Guid.NewGuid().ToString("N");
                using var editor = prefs.Edit() ?? throw new InvalidOperationException("Preferences unavailable.");
                editor.PutString(UiCommandKey, id);
                editor.Apply();
                return id;
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter", $"VPN command reservation failed ({ex.GetType().Name})");
            return null;
        }
    }

    internal static bool IsCurrentUiCommand(string? id) => AndroidSubscriptionRefresh.IsCurrentSelection(id, GetString);

    internal static bool HasConfirmedTunnel() => TunnelStateResync.IsConfirmedConnected(
        ResolveVpnState(), VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.HasLiveTunnel());

    public static AndroidPerAppPolicy? GetActivePerAppPolicy()
    {
        if (!HasConfirmedTunnel()) return null;
        var json = GetString("vpn_active_per_app_policy");
        if (json is null) return null;
        try
        {
            var policy = JsonSerializer.Deserialize(json, AndroidJsonContext.Default.AndroidPerAppPolicy);
            return policy is { ActiveMode: not null, ActivePackages: not null, RequestedMode: not null, RequestedPackages: not null }
                ? policy : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The connection-state record of <c>VpnRouterService</c>, resolved for this process (see <see cref="VpnStateResolver"/>).</summary>
    public static VpnStateSnapshot ResolveVpnState() =>
        VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.ResolveVpnState();

    public static string? GetCustomConfigJson() => GetString(KeyCustomConfigJson);
    public static bool SetCustomConfigJson(string? value) => SetString(KeyCustomConfigJson, value);

    public static string GetCustomConfigName() => GetString(KeyCustomConfigName) ?? "custom";
    public static bool SetCustomConfigName(string? value) => SetString(KeyCustomConfigName, value);

    public static string? GetVlessUri() => GetString(KeyVlessUri);
    public static bool SetVlessUri(string? value) => SetString(KeyVlessUri, value);

    public static string? GetSubscriptionUrl() => GetString(KeySubscriptionUrl);
    internal static string? SelectedSubscriptionId => GetString(AndroidSubscriptionRefresh.SelectedFeedId);
    public static bool SetSubscriptionUrl(string? value)
    {
        if (value == GetSubscriptionUrl()) return true;

        try
        {
            // Materialize the old layout before changing the URL that identifies its cache.
            var previous = GetString(KeySubscriptions) == null ? GetSubscriptions() : null;
            var prefs = Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            using var editor = prefs?.Edit();
            if (editor == null) return false;
            if (previous != null) editor.PutString(KeySubscriptions, JsonSerializer.Serialize(previous, JsonOptions));
            editor.Remove(AndroidSubscriptionRefresh.SelectedFeedId);
            editor.Remove(AndroidSubscriptionRefresh.SelectedFeedRow);
            if (string.IsNullOrWhiteSpace(value)) editor.Remove(KeySubscriptionUrl);
            else editor.PutString(KeySubscriptionUrl, value);
            return editor.Commit();
        }
        catch
        {
            return false;
        }
    }

    public static List<SubscriptionEntry> GetSubscriptions()
    {
        var json = GetString(KeySubscriptions);
        var result = StorageBlobRecovery.LoadOrRecover<List<SubscriptionEntry>>(
            json,
            j => JsonSerializer.Deserialize<List<SubscriptionEntry>>(j, JsonOptions));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeySubscriptions, json);
            StampRecoveryNotice(
                $"subscriptions cache unreadable ({result.Reason}: {result.Detail}); reset to defaults");
        }

        var legacy = GetString(KeySubscriptionUrl);
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            return [AndroidSubscriptionRefresh.LegacyFeed(legacy, GetServers())];
        }
        return new List<SubscriptionEntry>();
    }

    public static bool SetSubscriptions(IEnumerable<SubscriptionEntry>? subs)
    {
        try
        {
            return WithCommandPreferences(prefs =>
            {
                AndroidSubscriptionRefresh.SaveRegistrations(GetSubscriptions(), subs?.ToList() ?? [], GetConfigMode(),
                    key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions);
                return true;
            });
        }
        catch
        {
            return false;
        }
    }

    private static T WithCommandPreferences<T>(Func<ISharedPreferences, T> action)
    {
        var prefs = Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Preferences unavailable.");
        // The service takes the same Java monitor when a tile, stop or revoke supersedes the UI.
        var reference = VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.ServiceClassReference;
        Java.Interop.JniEnvironment.Monitors.MonitorEnter(reference);
        try { return action(prefs); }
        finally { Java.Interop.JniEnvironment.Monitors.MonitorExit(reference); }
    }

    private static void CommitStrings(ISharedPreferences prefs, IReadOnlyDictionary<string, string?> changes)
    {
        using var editor = prefs.Edit() ?? throw new InvalidOperationException("Preferences unavailable.");
        foreach (var (key, value) in changes)
        {
            if (value == null) editor.Remove(key);
            else editor.PutString(key, value);
        }
        if (!editor.Commit()) throw new InvalidOperationException("VPN settings could not be saved.");
    }

    internal static string? SubscriptionImportRevision => GetString(AndroidSubscriptionRefresh.ImportRevision);

    internal static bool SaveSubscriptionRefresh(SubscriptionEntry entry, bool rejected, string? selectingCommand, string? importRevision) =>
        WithCommandPreferences(prefs => AndroidSubscriptionRefresh.Save(entry, rejected, selectingCommand,
            key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions, importRevision));

    internal static void PruneSubServerDuplicatesOnce()
    {
        if (GetBool(KeyV4SubServersPruneDone, defaultValue: false)) return;

        try
        {
            var subsJson = GetString(KeySubscriptions);
            if (string.IsNullOrWhiteSpace(subsJson))
            {
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            List<SubscriptionEntry>? subs;
            try
            {
                subs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(subsJson, JsonOptions);
            }
            catch
            {
                return;
            }
            if (subs == null || subs.Count == 0)
            {
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            var subKeys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs)
            {
                if (s?.Servers == null) continue;
                foreach (var srv in s.Servers)
                {
                    if (srv == null) continue;
                    subKeys.Add($"{srv.Server}:{srv.Port}:{srv.Uuid}");
                }
            }
            if (subKeys.Count == 0)
            {
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            var standalone = GetServers();
            int before = standalone.Count;
            standalone.RemoveAll(s =>
                s != null && subKeys.Contains($"{s.Server}:{s.Port}:{s.Uuid}"));

            if (standalone.Count != before)
            {
                global::Android.Util.Log.Info("VpnRouter.Storage",
                    $"PruneSubServerDuplicatesOnce: removed {before - standalone.Count} duplicate(s) from KeyServersJson");
                SetServers(standalone);
            }
            SetBool(KeyV4SubServersPruneDone, true);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Storage",
                $"PruneSubServerDuplicatesOnce threw: {ex.GetType().Name}");
        }
    }

    internal static void PruneKnownPlaceholdersOnce()
    {
        if (GetBool(KeyV4PlaceholderPruneDone, defaultValue: false)) return;

        try
        {
            int totalRemoved = 0;

            var standalone = GetServers();
            int beforeStandalone = standalone.Count;
            standalone.RemoveAll(s => PlaceholderDefense.IsPlaceholder(s));
            int removedStandalone = beforeStandalone - standalone.Count;
            if (removedStandalone > 0)
            {
                SetServers(standalone);
                totalRemoved += removedStandalone;
                global::Android.Util.Log.Info("VpnRouter.Storage",
                    $"PruneKnownPlaceholdersOnce: removed {removedStandalone} placeholder entry(ies) from KeyServersJson");
            }

            var subsJson = GetString(KeySubscriptions);
            if (!string.IsNullOrWhiteSpace(subsJson))
            {
                List<SubscriptionEntry>? subs = null;
                try
                {
                    subs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(subsJson, JsonOptions);
                }
                catch
                {
                    return;
                }
                if (subs != null)
                {
                    int removedFromSubs = 0;
                    foreach (var sub in subs)
                    {
                        if (sub?.Servers == null) continue;
                        var before = sub.Servers.Count;
                        sub.Servers.RemoveAll(s => PlaceholderDefense.IsPlaceholder(s));
                        removedFromSubs += before - sub.Servers.Count;
                    }
                    if (removedFromSubs > 0)
                    {
                        SetSubscriptions(subs);
                        totalRemoved += removedFromSubs;
                        global::Android.Util.Log.Info("VpnRouter.Storage",
                            $"PruneKnownPlaceholdersOnce: removed {removedFromSubs} placeholder entry(ies) across {subs.Count} subscription(s)");
                    }
                }
            }

            var selectedName = GetSelectedServerName();
            if (!string.IsNullOrEmpty(selectedName))
            {
                bool stillExists = standalone.Any(s =>
                    string.Equals(s?.Name, selectedName, System.StringComparison.OrdinalIgnoreCase));
                if (!stillExists && !string.IsNullOrWhiteSpace(subsJson))
                {
                    try
                    {
                        var freshSubs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(GetString(KeySubscriptions) ?? "[]", JsonOptions);
                        stillExists = freshSubs?.Any(sub =>
                            sub?.Servers?.Any(srv =>
                                string.Equals(srv?.Name, selectedName, System.StringComparison.OrdinalIgnoreCase)) == true) ?? false;
                    }
                    catch { }
                }
                if (!stillExists)
                {
                    SetSelectedServerName(null);
                    global::Android.Util.Log.Info("VpnRouter.Storage",
                        "Cleared a stale server selection");
                }
            }

            if (totalRemoved > 0)
            {
                SetInt(KeyPlaceholderPruneCount, totalRemoved);
            }

            SetBool(KeyV4PlaceholderPruneDone, true);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Storage",
                $"PruneKnownPlaceholdersOnce threw: {ex.GetType().Name}");
        }
    }

    public static int GetPlaceholderPruneCount() => GetInt(KeyPlaceholderPruneCount, defaultValue: 0);

    public static void ClearPlaceholderPruneCount() => SetInt(KeyPlaceholderPruneCount, 0);

    public static List<VlessServerEntry> GetServers()
    {
        var json = GetString(KeyServersJson);
        var result = StorageBlobRecovery.LoadOrRecover<List<VlessServerEntry>>(
            json,
            j => JsonSerializer.Deserialize<List<VlessServerEntry>>(j, JsonOptions));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyServersJson, json);
            StampRecoveryNotice(
                $"server cache unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<VlessServerEntry>();
    }

    public static bool SetServers(IEnumerable<VlessServerEntry>? servers)
    {
        try
        {
            if (servers == null)
                return SetString(KeyServersJson, null);
            var list = new List<VlessServerEntry>(servers);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            return SetString(KeyServersJson, json);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetSelectedServerName() => GetString(KeySelectedServerName);
    public static bool SetSelectedServerName(string? value) => SetString(KeySelectedServerName, value);

    internal static bool SelectSingleServer(VlessServerEntry server, string uri, string command) =>
        WithCommandPreferences(prefs => AndroidSubscriptionRefresh.SaveSingleServer(server, uri, command,
            key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions));

    internal static bool SelectSubscriptionServer(SubscriptionEntry source, VlessServerEntry row, string? command) =>
        WithCommandPreferences(prefs => AndroidSubscriptionRefresh.SaveFeedSelection(source, row, command, GetSubscriptions(),
            key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions));

    internal static void ImportProfile(ConfigShareDocument doc) =>
        WithCommandPreferences(prefs =>
        {
            AndroidSubscriptionRefresh.ImportProfile(doc, key => prefs.GetString(key, null),
                changes => CommitStrings(prefs, changes), JsonOptions);
            return true;
        });

    internal static void SaveCustomProfile(string raw) =>
        WithCommandPreferences(prefs =>
        {
            AndroidSubscriptionRefresh.SaveCustomProfile(raw, key => prefs.GetString(key, null),
                changes => CommitStrings(prefs, changes), JsonOptions);
            return true;
        });

    public static VlessServerEntry? GetActiveServer() =>
        WithCommandPreferences(prefs => AndroidSubscriptionRefresh.ResolveActiveServer(GetServers(), GetSubscriptions(),
            GetConfigMode(), key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions));

    internal static VlessServerEntry? GetActiveServer(string command) =>
        WithCommandPreferences(prefs => AndroidSubscriptionRefresh.ResolveForConnect(command, GetServers(), GetSubscriptions(),
            GetConfigMode(), key => prefs.GetString(key, null), changes => CommitStrings(prefs, changes), JsonOptions));

    internal static AndroidSubscriptionRefresh.FeedRow? SelectedSubscriptionRow() =>
        WithCommandPreferences(prefs =>
        {
            string? Read(string key) => prefs.GetString(key, null);
            return AndroidSubscriptionRefresh.ReadMode(Read) == "subscribe" && AndroidSubscriptionRefresh.ReadRetainedManual(Read) == null
                ? AndroidSubscriptionRefresh.FindFeedRow(GetSubscriptions(), Read) : null;
        });

    internal static void PrepareServerPool(VlessConfig config, VlessServerEntry selected) =>
        WithCommandPreferences(prefs =>
        {
            AndroidSubscriptionRefresh.PreparePool(config, selected, GetServers(), GetSubscriptions(), GetConfigMode(),
                key => prefs.GetString(key, null), JsonOptions);
            return true;
        });

    public static string? GetLanguage() => GetString(KeyLanguage);
    public static bool SetLanguage(string? value) => SetString(KeyLanguage, value);

    public static string GetTheme() =>
        ValidateOrDefault(KeyTheme, GetString(KeyTheme), AllowedThemes, "light");
    public static bool SetTheme(string? value) => SetString(KeyTheme, value);

    private const string KeyPerAppMode = "per_app_mode";
    private const string KeyPerAppPackages = "per_app_packages";
    private const string KeyPerAppLastMode = "per_app_last_mode";

    public static string GetPerAppMode()
    {
        var raw = GetString(KeyPerAppMode);
        var normalized = VPNRouter.Core.Models.PerAppFilterMode.Normalize(raw);
        if (!string.IsNullOrWhiteSpace(raw) &&
            !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
        {
            QuarantineBadValue(KeyPerAppMode, raw);
            StampRecoveryNotice(
                $"per-app filter mode '{raw}' unknown; reset to '{normalized}'");
            SetString(KeyPerAppMode, normalized);
        }
        return normalized;
    }
    public static bool SetPerAppMode(string? value) => SetString(KeyPerAppMode, value);

    public static string GetPerAppLastMode() =>
        VPNRouter.Core.Models.PerAppFilterMode.ResolveLastMode(GetString(KeyPerAppLastMode));
    public static bool SetPerAppLastMode(string? value) => SetString(KeyPerAppLastMode, value);

    public static List<string> GetPerAppPackages()
    {
        var json = GetString(KeyPerAppPackages);
        var result = StorageBlobRecovery.LoadOrRecover<List<string>>(
            json,
            j => JsonSerializer.Deserialize<List<string>>(j, JsonOptions));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyPerAppPackages, json);
            StampRecoveryNotice(
                $"per-app package list unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<string>();
    }

    public static bool SavePerAppSelection(string mode, IEnumerable<string> packages, bool rememberPickerMode = false)
    {
        var prefs = Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        if (editor is null) return false;
        editor.PutString(KeyPerAppPackages, JsonSerializer.Serialize(new List<string>(packages), JsonOptions));
        editor.PutString(KeyPerAppMode, mode);
        if (rememberPickerMode) editor.PutString(KeyPerAppLastMode, mode);
        editor.Apply();
        return true;
    }

    public static bool SetPerAppPackages(IEnumerable<string>? packages)
    {
        try
        {
            if (packages is null) return SetString(KeyPerAppPackages, null);
            var list = new List<string>(packages);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            return SetString(KeyPerAppPackages, json);
        }
        catch
        {
            return false;
        }
    }

    private const string KeyRoutingMode = "routing_mode";
    private const string KeyBypassRussianTraffic = "bypass_ru";
    private const string KeyBlockOnVpnFail = "block_on_vpn_fail";
    private const string KeyDnsStrategy = "dns_strategy";
    private const string KeyBlockAds = "block_ads";
    private const string KeyAutoSelectBest = "auto_select_best_server";
    private const string KeyPostNotifPrompt = "post_notif_prompt_shown";
    private const string KeyAlwaysOnPrompt = "alwayson_lockdown_prompt_shown";
    private const string KeyExternalControl = "external_control_enabled";
    private const string KeyUpdateChannel = "update_channel";
    private const string KeyAutostartVpn = "autostart_vpn";
    private const string KeyAutostartZapret = "autostart_zapret";
    private const string KeyAutostartTgProxy = "autostart_tgproxy";

    private const string KeyAutoReconnectOnNetworkChange = "auto_reconnect_on_network_change";

    private const string KeyBatteryOptPromptShown = "battery_opt_prompt_shown";
    private const string KeyBatteryOptLastPrompt = "battery_opt_last_prompt";

    private static readonly HashSet<string> AllowedRoutingModes =
        new(StringComparer.OrdinalIgnoreCase) { "split", "full" };
    private static readonly HashSet<string> AllowedDnsStrategies =
        new(StringComparer.OrdinalIgnoreCase) { "ipv4_only", "ipv6_only", "prefer_ipv4", "prefer_ipv6", "default" };
    private static readonly HashSet<string> AllowedUpdateChannels =
        new(StringComparer.OrdinalIgnoreCase) { "stable", "experimental" };
    private static readonly HashSet<string> AllowedThemes =
        new(StringComparer.OrdinalIgnoreCase) { "light", "dark", "system" };

    public static string GetRoutingMode() =>
        VPNRouter.Core.Models.PerAppFilterMode.RoutingModeFor(GetPerAppMode());

    public static bool SetRoutingMode(string value)
    {
        var newPerAppMode = VPNRouter.Core.Models.PerAppFilterMode.PerAppModeForRoutingChange(
            value, GetPerAppMode(), GetPerAppLastMode());
        return newPerAppMode is null || SetPerAppMode(newPerAppMode);
    }

    public static bool GetBypassRussianTraffic() => GetBool(KeyBypassRussianTraffic, defaultValue: true);
    public static bool SetBypassRussianTraffic(bool value) => SetBool(KeyBypassRussianTraffic, value);

    public static bool GetBlockOnVpnFail() => GetBool(KeyBlockOnVpnFail, defaultValue: false);
    public static bool SetBlockOnVpnFail(bool value) => SetBool(KeyBlockOnVpnFail, value);

    public static string GetDnsStrategy() =>
        ValidateOrDefault(KeyDnsStrategy, GetString(KeyDnsStrategy), AllowedDnsStrategies, "ipv4_only");
    public static bool SetDnsStrategy(string value) => SetString(KeyDnsStrategy, value);

    public static bool GetBlockAds() => GetBool(KeyBlockAds, defaultValue: false);
    public static bool SetBlockAds(bool value) => SetBool(KeyBlockAds, value);

    public static bool GetAutoSelectBestServer() => GetBool(KeyAutoSelectBest, defaultValue: false);
    public static bool SetAutoSelectBestServer(bool value) => SetBool(KeyAutoSelectBest, value);

    public static bool GetPostNotifPromptShown() => GetBool(KeyPostNotifPrompt, defaultValue: false);
    public static bool SetPostNotifPromptShown(bool value) => SetBool(KeyPostNotifPrompt, value);

    public static bool GetAlwaysOnPromptShown() => GetBool(KeyAlwaysOnPrompt, defaultValue: false);
    public static bool SetAlwaysOnPromptShown(bool value) => SetBool(KeyAlwaysOnPrompt, value);

    public static bool GetExternalControlEnabled() => GetBool(KeyExternalControl, defaultValue: false);
    public static bool SetExternalControlEnabled(bool value) => SetBool(KeyExternalControl, value);

    public static string GetUpdateChannel() =>
        ValidateOrDefault(KeyUpdateChannel, GetString(KeyUpdateChannel), AllowedUpdateChannels, "stable");
    public static bool SetUpdateChannel(string value) => SetString(KeyUpdateChannel, value);

    public static bool GetAutostartVpn() => GetBool(KeyAutostartVpn, defaultValue: false);
    public static bool SetAutostartVpn(bool value) => SetBool(KeyAutostartVpn, value);

    public static bool GetAutostartZapret() => GetBool(KeyAutostartZapret, defaultValue: false);
    public static bool SetAutostartZapret(bool value) => SetBool(KeyAutostartZapret, value);

    public static bool GetAutostartTgProxy() => GetBool(KeyAutostartTgProxy, defaultValue: false);
    public static bool SetAutostartTgProxy(bool value) => SetBool(KeyAutostartTgProxy, value);

    private const string KeyDpiBypassMode = "dpi_bypass_mode";
    private static readonly HashSet<string> AllowedDpiBypassModes =
        new(StringComparer.OrdinalIgnoreCase) { "off", "standard", "aggressive" };

    public static string GetDpiBypassMode() =>
        ValidateOrDefault(KeyDpiBypassMode, GetString(KeyDpiBypassMode), AllowedDpiBypassModes, "off");
    public static bool SetDpiBypassMode(string value) => SetString(KeyDpiBypassMode, value);

    private const string KeyActiveProfile = "active_profile";

    public static bool SetActiveProfile(string? value) => SetString(KeyActiveProfile, value);

    public static bool GetAutoReconnectOnNetworkChange() =>
        GetBool(KeyAutoReconnectOnNetworkChange, defaultValue: true);
    public static bool SetAutoReconnectOnNetworkChange(bool value) =>
        SetBool(KeyAutoReconnectOnNetworkChange, value);

    public static bool GetBatteryOptPromptShown() =>
        GetBool(KeyBatteryOptPromptShown, defaultValue: false);
    public static bool SetBatteryOptPromptShown(bool value) =>
        SetBool(KeyBatteryOptPromptShown, value);

    public static string? GetBatteryOptLastPrompt() => GetString(KeyBatteryOptLastPrompt);
    public static bool SetBatteryOptLastPrompt(string value) => SetString(KeyBatteryOptLastPrompt, value);

    private const string KeyPublicActiveSubTab = "public_active_sub_tab";

    public static bool GetPublicActiveSubTabIsSaved() =>
        GetBool(KeyPublicActiveSubTab, defaultValue: false);
    public static bool SetPublicActiveSubTabIsSaved(bool value) =>
        SetBool(KeyPublicActiveSubTab, value);

    private const string KeyAdvancedActiveTab = "advanced_active_tab";

    public static bool SetAdvancedActiveTab(string tabName)
        => SetString(KeyAdvancedActiveTab, tabName ?? "Servers");

    private const string KeySettingsActiveSubSection = "settings_active_subsection";

    public static int GetSettingsActiveSubSection()
    {
        var raw = GetString(KeySettingsActiveSubSection);
        if (string.IsNullOrEmpty(raw)) return 0;
        return int.TryParse(raw, out var idx) && idx >= 0 && idx <= 5 ? idx : 0;
    }

    public static bool SetSettingsActiveSubSection(int index)
        => SetString(KeySettingsActiveSubSection, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const string KeyApplicationsActiveCategory = "applications_active_category";
    private const string KeyCustomCategoriesJson = "custom_categories_json";

    public static string? GetApplicationsActiveCategory()
        => GetString(KeyApplicationsActiveCategory);
    public static bool SetApplicationsActiveCategory(string? id)
        => SetString(KeyApplicationsActiveCategory, id);

    public static List<CustomCategory> GetCustomCategories()
    {
        var json = GetString(KeyCustomCategoriesJson);
        var result = StorageBlobRecovery.LoadOrRecover<List<CustomCategory>>(
            json,
            j => JsonSerializer.Deserialize<List<CustomCategory>>(j, JsonOptions));

        if (result.Loaded) return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyCustomCategoriesJson, json);
            StampRecoveryNotice(
                $"custom categories cache unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<CustomCategory>();
    }

    public static bool SetCustomCategories(IEnumerable<CustomCategory>? cats)
    {
        try
        {
            if (cats is null) return SetString(KeyCustomCategoriesJson, null);
            var list = new List<CustomCategory>(cats);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            return SetString(KeyCustomCategoriesJson, json);
        }
        catch
        {
            return false;
        }
    }

    private const string KeyServerTestResults = "server_test_results";

    public sealed class ServerTestResultDto
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }
        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }
        [JsonPropertyName("last_tested_at")]
        public DateTimeOffset LastTestedAt { get; set; }
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public static string BuildServerKey(VlessServerEntry srv)
        => $"{srv.Server}:{srv.Port}:{srv.Uuid}:{srv.Flow}";

    public static Dictionary<string, ServerTestResultDto> GetServerTestResults()
    {
        var json = GetString(KeyServerTestResults);
        var result = StorageBlobRecovery.LoadOrRecover<Dictionary<string, ServerTestResultDto>>(
            json,
            j => JsonSerializer.Deserialize<Dictionary<string, ServerTestResultDto>>(j, JsonOptions));

        if (result.Loaded)
            return new Dictionary<string, ServerTestResultDto>(result.Value!, System.StringComparer.OrdinalIgnoreCase);

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyServerTestResults, json);
            StampRecoveryNotice(
                $"server test history unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
    }

    public static bool SetServerTestResults(Dictionary<string, ServerTestResultDto>? results)
    {
        try
        {
            if (results is null || results.Count == 0)
                return SetString(KeyServerTestResults, null);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var pruned = new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in results)
            {
                if (kvp.Value.LastTestedAt < cutoff) continue;
                pruned[kvp.Key] = kvp.Value;
            }
            const int MaxEntries = 300;
            if (pruned.Count > MaxEntries)
            {
                var ordered = new List<KeyValuePair<string, ServerTestResultDto>>(pruned);
                ordered.Sort((a, b) => b.Value.LastTestedAt.CompareTo(a.Value.LastTestedAt));
                pruned = new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < MaxEntries; i++) pruned[ordered[i].Key] = ordered[i].Value;
            }
            var json = JsonSerializer.Serialize(pruned, JsonOptions);
            return SetString(KeyServerTestResults, json);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetString(string key)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return null;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var v = prefs?.GetString(key, null);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetString(string key, string? value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            if (string.IsNullOrWhiteSpace(value))
                editor.Remove(key);
            else
                editor.PutString(key, value);
            editor.Apply();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool GetBool(string key, bool defaultValue)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return defaultValue;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs?.GetBoolean(key, defaultValue) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool SetBool(string key, bool value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            editor.PutBoolean(key, value);
            editor.Apply();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetInt(string key, int defaultValue)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return defaultValue;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs?.GetInt(key, defaultValue) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool SetInt(string key, int value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            editor.PutInt(key, value);
            editor.Apply();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly object _recoveryLock = new();
    private static string? _lastRecoveryNotice;

    public static string? LastRecoveryNotice
    {
        get { lock (_recoveryLock) return _lastRecoveryNotice; }
    }

    public static string? ConsumeRecoveryNotice()
    {
        lock (_recoveryLock)
        {
            var n = _lastRecoveryNotice;
            _lastRecoveryNotice = null;
            return n;
        }
    }

    private static void StampRecoveryNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            global::Android.Util.Log.Warn("VpnRouter.SelfRepair", "Recovered an invalid preference");
        }
        catch { }

        lock (_recoveryLock)
        {
            _lastRecoveryNotice = string.IsNullOrEmpty(_lastRecoveryNotice)
                ? message
                : $"{_lastRecoveryNotice}; {message}";
        }
    }

    private static string ValidateOrDefault(
        string key, string? raw, HashSet<string> allowed, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        var match = allowed.FirstOrDefault(v =>
            string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match)) return match!;

        QuarantineBadValue(key, raw);
        StampRecoveryNotice(
            $"setting '{key}' had unknown value '{raw}'; reset to '{defaultValue}'");
        SetString(key, defaultValue);
        return defaultValue;
    }

    private static void QuarantineBadValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var quarantineKey = $"{key}__corrupt_{ts}";
            SetString(quarantineKey, value);
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"quarantine of '{key}' failed: {ex.GetType().Name}");
            }
            catch { }
        }
    }

    public static int RepairAllOnLoad()
    {
        try
        {
            var enumKeys = new List<AndroidStorageSane.EnumKeySpec>
            {
                new(KeyRoutingMode, AllowedRoutingModes, "split"),
                new(KeyDnsStrategy, AllowedDnsStrategies, "ipv4_only"),
                new(KeyUpdateChannel, AllowedUpdateChannels, "stable"),
                new(KeyTheme, AllowedThemes, "light"),
                new(KeyDpiBypassMode, AllowedDpiBypassModes, "off"),
            };

            var outcome = AndroidStorageSane.RepairAllOnLoad(
                get: GetString,
                set: (k, v) => { SetString(k, v); },
                enumKeys: enumKeys,
                quarantine: QuarantineBadValue);

            foreach (var change in outcome.Changes)
                StampRecoveryNotice(change);

            return outcome.Changes.Count;
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"RepairAllOnLoad failed: {ex.GetType().Name}");
            }
            catch { }
            return 0;
        }
    }

    private const string KeySafeModeBannerPending = "safe_mode_banner_pending";

    public static void QueueSafeModeBannerForUi() => SetBool(KeySafeModeBannerPending, true);

    public static bool ConsumeSafeModeBanner()
    {
        if (!GetBool(KeySafeModeBannerPending, defaultValue: false)) return false;
        try { SetBool(KeySafeModeBannerPending, false); }
        catch { }
        return true;
    }

    private const string KeySafeModeOnNextLaunch = "safe_mode_on_next_launch";

    public static bool SetSafeModeOnNextLaunch(bool value)
        => SetBool(KeySafeModeOnNextLaunch, value);

    public static bool ResetUserSettings()
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;

            var liveKeys = new[]
            {
                KeyVlessUri, KeySubscriptionUrl, KeyServersJson, AndroidSubscriptionRefresh.LegacyManualOwner,
                KeySelectedServerName, AndroidSubscriptionRefresh.SelectedFeedId, AndroidSubscriptionRefresh.SelectedFeedRow,
                AndroidSubscriptionRefresh.ImportRevision,
                AndroidSubscriptionRefresh.SupersededSelection, KeyLanguage, KeyTheme,
                KeySubscriptions, KeyPerAppMode, KeyPerAppPackages,
                KeyPerAppLastMode, KeyRoutingMode, KeyBypassRussianTraffic,
                KeyBlockOnVpnFail, KeyDnsStrategy, KeyBlockAds, KeyUpdateChannel,
                KeyAutostartVpn, KeyAutostartZapret, KeyAutostartTgProxy,
                KeyDpiBypassMode,
                KeyActiveProfile,
                KeyExternalControl,
            };
            foreach (var k in liveKeys) editor.Remove(k);
            return editor.Commit();
        }
        catch
        {
            return false;
        }
    }
}
