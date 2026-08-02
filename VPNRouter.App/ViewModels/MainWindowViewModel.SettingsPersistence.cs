using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Settings Load/Save ──

    private void LoadSettingsIntoUI()
    {
        _isLoadingUI = true;
        try
        {
        // Language — v2.24.4: auto-detect from OS on first launch.
        // Empty string in config means "never chose a language yet" →
        // sniff the current UI culture and persist the choice so the
        // menu toggle still works predictably. Russian locale → ru,
        // everything else → en.
        var storedLang = _settings.App.Language ?? string.Empty;
        if (string.IsNullOrWhiteSpace(storedLang))
        {
            var osLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            storedLang = string.Equals(osLang, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
            _settings.App.Language = storedLang;
            try { _settingsStore.Save(_settings); } catch { }
        }
        IsRussian = storedLang.Equals("ru", StringComparison.OrdinalIgnoreCase);
        Strings.Lang = IsRussian ? "ru" : "en";

        // Theme preference: "light" | "dark" | "system" (default "system" →
        // follow the OS appearance). ApplyTheme resolves the effective variant
        // and sets IsDarkTheme. v2.40.x (Fix #7).
        ThemePreference = NormalizeThemePref(_settings.App.Theme);
        ApplyTheme();

        // UI complexity mode. v2.21.7: always start in Simple on launch —
        // even if the user was in Advanced when they last quit. They can
        // still flip to Advanced via the header pill; this just makes the
        // landing screen predictably the compact one every time the app
        // opens. Toggling via ToggleUiModeCommand still persists UiMode
        // to settings for internal bookkeeping (FreeConfigsVm lazy-load,
        // etc), it's only the ctor-side load that now ignores the
        // persisted value.
        IsSimpleMode = true;

        // v2.27 Bug B: SmpAutostartChecked is now a computed property over
        // ServiceVm.IsInstalled/IsRunning + AutostartVpn, so we don't assign
        // it here. The UI will read it on first bind, and re-reads fire from
        // OnAutostartVpnChanged + the ServiceVm.PropertyChanged handler.

        // Pre-fill Simple-mode input from existing settings so a user who
        // already has a config doesn't stare at an empty 'Paste VLESS...'
        // field. For subscriptions we show the first enabled URL; for
        // single-VLESS we can't reconstruct the original URI, so leave
        // empty — SmpToggleConnectAsync treats empty-input + existing
        // Vless.Servers as 'just connect with what we have'.
        var firstEnabledSub = _settings.App.Subscriptions?
            .FirstOrDefault(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        if (firstEnabledSub != null)
            SmpInput = firstEnabledSub.Url;

        // Config mode (three-way: generated / custom / subscribe)
        // Mode is determined by which tab is active. On load, select the
        // correct tab based on saved config_mode.
        var configMode = _settings.App.ConfigMode ?? "generated";
        IsSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        IsVlessMode = !configMode.Equals("custom", StringComparison.OrdinalIgnoreCase) && !IsSubscribeMode;
        // v2.30.2-r1 Bug 1 fix: SelectedServerModeIndex init is now
        // data-driven (defer to after Servers/CustomConfigs are populated
        // — see section below). The legacy `IsVlessMode ? 0 : 1` mirror
        // forced the Servers page to land on "Custom" sub-tab whenever
        // the user was in Subscribe mode, even though the page would
        // visually highlight "Custom" while the actual VLESS list was
        // shown. User report 2026-05-01: «после открытия страницы
        // сервер выделено Кастомные конфиги хотя открыто серверы».
        SubscriptionUrl = _settings.App.SubscriptionUrl ?? "";
        // Set initial tab: 0=Manual, 1=Subscribe, 2=Network, 3=Applications
        SelectedTabIndex = IsSubscribeMode ? 1 : 0;

        // Routing mode
        IsSplitTunnel = !(_settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);

        // v2.32 (r10) — Apps Include/Exclude 2-mode. AM-1 chip added the
        // field + schema v3 migration; this hydrates the VM observable.
        // AppSettingsSane already canonicalises to lowercase + falls back
        // to "include" on unknown values.
        RoutingAppsMode = (_settings.App.RoutingAppsMode ?? "include").Trim().ToLowerInvariant();

        // Russian geo bypass
        BypassRussianTraffic = _settings.App.BypassRussianTraffic;
        // v2.30.0-r17: Custom-rules-priority. "custom_first" → checkbox on.
        CustomRulesAboveToggles = string.Equals(
            _settings.App.CustomRulesPriority,
            "custom_first",
            System.StringComparison.OrdinalIgnoreCase);

        // v2.30.0 — full custom rules (direct/proxy/block) text format.
        // Round-trip: SaveSettings serialises CustomRulesText back to
        // _settings.App.CustomRules.
        // Migration from v2.29 CustomDirectRules already happened in
        // SettingsMigrator.Migrate_1_to_2 — at this point CustomRules
        // holds whatever the user has, CustomDirectRules is empty.
        // v2.30.0-r2: also rebuild the CustomRulesList structured rows
        // (separate ListBox view in the new Network → Rules section).
        // Both views (textbox + rows) drive the same _settings.App.CustomRules.
        _isSyncingCustomRules = true;
        try
        {
            CustomRulesText = VPNRouter.Core.Services.CustomRulesParser
                .SerializeToText(_settings.App.CustomRules);
        }
        finally { _isSyncingCustomRules = false; }
        RebuildCustomRulesList();

        // Strict mode
        StrictMode = _settings.App.StrictMode;
        TunMtu = _settings.Tun.Mtu;

        // IPv4 + DNS flush + Strict DNS
        ForceIpv4Only = _settings.App.ForceIpv4Only;
        FlushDnsOnStart = _settings.App.FlushDnsOnStart;
        StrictDns = _settings.App.StrictDns;
        BlockAds = _settings.App.BlockAds;
        AutoSelectBestServer = _settings.Vless.AutoSelectBestServer;
        ConnectionIntentIndex = IntentToIndex(_settings.App.ConnectionIntent);
        // Wave 39 — DNS leak lockdown (firewall block of UDP/53, TCP/53,
        // TCP/853 on non-TUN interfaces while VPN is active).
        IsDnsLeakLockdownEnabled = _settings.App.DnsLeakLockdown;

        // Autostart
        AutostartVpn = _settings.App.AutostartVpn;
        AutostartZapret = _settings.App.AutostartZapret;
        AutostartTgProxy = _settings.App.AutostartTgProxy;
#if PLATFORM_WINDOWS
        AutostartUi = AutostartHelper.IsEnabled();
#endif
        LoadZapretStrategies();
        ZapretCustomArgs = _settings.App.ZapretCustomArgs;
        // Detect zapret state from actual process, not saved flag
        if (IsZapretRunning())
        {
            ZapretEnabled = true;
            ZapretStatus = IsRussian ? "Работает (из предыдущей сессии)" : "Running (from previous session)";
        }
        else
        {
            ZapretEnabled = false;
            ZapretStatus = Strings.Stopped;
        }

#if PLATFORM_WINDOWS
        DiscordHostsInstalled = VPNRouter.Core.Services.HostsManager.IsInstalled();
        FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();

        // Self-heal hosts files written by older builds that duplicated the
        // finland*.discord.media voice entries across BOTH the Discord and
        // Flowseal blocks (~200 redundant lines). No-op once deduped; only
        // the Flowseal copy is stripped, the native Discord block stays owner.
        if (DiscordHostsInstalled && FlowsealHostsInstalled)
        {
            try { VPNRouter.Core.Services.HostsManager.ReconcileDiscordDuplicates(_logger); }
            catch (Exception ex) { _logger.Warning(ex, "[VM] Discord/Flowseal hosts reconcile failed (non-fatal)"); }
        }

        if (VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            GameFilterModeIndex = (int)VPNRouter.Core.Services.ZapretActions.GetGameFilterMode();
            IpSetModeIndex = (int)VPNRouter.Core.Services.ZapretActions.GetIpSetMode();
            ZapretAutoUpdateCheck = VPNRouter.Core.Services.ZapretActions.IsAutoUpdateCheckEnabled();
        }

        // Telegram proxy
        TgProxyPort = _settings.App.TgProxyPort > 0 ? _settings.App.TgProxyPort : 1443;
        TgProxySecret = _settings.App.TgProxySecret;
        TgProxyVersionText = TgProxyUpdater.IsInstalled()
            ? (TgProxyUpdater.GetLocalVersion() ?? "?")
            : (IsRussian ? "Не установлен" : "Not installed");
        if (TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            TgProxyEnabled = true;
            TgProxyStatus = IsRussian ? "Работает (из предыдущей сессии)" : "Running (from previous session)";
            if (!string.IsNullOrEmpty(TgProxySecret))
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        }
        else
        {
            TgProxyEnabled = false;
            TgProxyStatus = Strings.Stopped;
        }
#endif

        // Update channel
        ReceivePrereleases = _settings.Update.IsExperimental;

        // Load servers + select the active one
        Servers.Clear();
        ServerViewModel? activeServer = null;
        foreach (var entry in _settings.Vless.GetEffectiveServers())
        {
            var vm = new ServerViewModel(entry);
            Servers.Add(vm);
            if (!string.IsNullOrEmpty(_settings.Vless.ActiveServer) &&
                entry.Name?.Equals(_settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase) == true)
                activeServer = vm;
        }
        ServerViewModel.RefreshUdpSiblingFlags(Servers); // r8 #6: "naive + hy2" only on a real sibling
        ServerViewModel.RefreshProviderRiskFlags(Servers); // R3: subnet-risk flags from the store
        SelectedServer = activeServer ?? Servers.FirstOrDefault();

        // v2.32 (r10, F-C) — flag legacy vless.servers entries that aren't
        // in any enabled subscription. F-B migration strips these on load,
        // but mark anyway for the rare cases (migration not yet fired,
        // user manually re-added an entry) so ServersPage can show
        // "Not in subscription" badge + tooltip.
        MarkOrphanServers();

        // Migrate legacy single subscription → first entry in Subscriptions list
        if (_settings.App.Subscriptions.Count == 0
            && !string.IsNullOrWhiteSpace(_settings.App.SubscriptionUrl))
        {
            _settings.App.Subscriptions.Add(new SubscriptionEntry
            {
                Name = "Default",
                Url = _settings.App.SubscriptionUrl,
                Enabled = true,
                Servers = _settings.App.SubscriptionServers ?? new(),
                LastServerCount = (_settings.App.SubscriptionServers ?? new()).Count,
                LastRefreshedAt = DateTimeOffset.UtcNow
            });
            _logger.Information("[VM] Migrated legacy subscription_url → Subscriptions[0]");
        }

        // Load subscriptions into VM
        Subscriptions.Clear();
        foreach (var entry in _settings.App.Subscriptions)
            Subscriptions.Add(new SubscriptionViewModel(entry));

        // Rebuild aggregated server pool from all enabled subscriptions
        RebuildSubscriptionPool();

        // Load custom configs
        CustomConfigs.Clear();
        CustomConfigViewModel? activeConfig = null;
        foreach (var entry in _settings.App.CustomConfigs ?? new())
        {
            var isActive = entry.Name == _settings.App.ActiveCustomConfig;
            var vm = new CustomConfigViewModel(entry, isActive);
            CustomConfigs.Add(vm);
            if (isActive) activeConfig = vm;
        }
        // Ensure exactly one config is active. If none matched by name
        // (first launch, or saved name deleted), activate the first one.
        if (activeConfig == null && CustomConfigs.Count > 0)
        {
            activeConfig = CustomConfigs[0];
            activeConfig.IsActive = true;
            // Persist so engine reads the right config on Connect
            _settings.App.ActiveCustomConfig = activeConfig.Name;
        }
        SelectedCustomConfig = activeConfig;

        // v2.30.2-r1 Bug 1 fix: data-driven sub-tab default. Now that
        // both Servers + CustomConfigs are populated, pick the sub-tab
        // that actually has content to show:
        //   - Servers list non-empty (or CustomConfigs empty) → "Серверы" (0)
        //   - Servers empty AND CustomConfigs non-empty → "Свои конфиги" (1)
        //
        // This matters because the Subscribe-mode user typically has zero
        // CustomConfigs but does have manual VLESS rows in Servers — the
        // pre-r1 logic mirrored ConfigMode and forced sub-tab=1 (Custom),
        // which highlighted the wrong sub-tab visually while the page
        // continued to render the VLESS list.
        var subTabHasManual = Servers.Count > 0;
        var subTabHasCustom = CustomConfigs.Count > 0;
        var subTabIndex = (subTabHasManual || !subTabHasCustom) ? 0 : 1;
        SelectedServerModeIndex = subTabIndex;
        _logger?.Information(
            "[VM] Sub-tab init: ServerModeIndex={Idx} (manual={M}, custom={C}, configMode={CM})",
            subTabIndex, Servers.Count, CustomConfigs.Count, _settings.App.ConfigMode);

        // Load apps from profiles + custom apps
        LoadApps();

        RefreshLocalization();
        }
        finally
        {
            _isLoadingUI = false;
        }
    }

    private void SaveSettings()
    {
        // Guard: don't save while LoadSettingsIntoUI is populating fields
        if (_isLoadingUI) return;

        // Auto-backup current config.yaml before overwriting (rolling .bak)
        try
        {
            var configPath = AppPaths.ConfigYamlPath;
            if (File.Exists(configPath))
                File.Copy(configPath, configPath + ".bak", overwrite: true);
        }
        catch (Exception ex) { _logger.Debug(ex, "[Settings] Backup failed"); }

        // Config mode (three-way) — v2.28.2-r2 guard:
        //
        // The ServerModeIndex sub-tab handler (OnSelectedServerModeIndexChanged)
        // flips IsVlessMode whenever the user clicks the "Custom" sub-tab,
        // which would normally land here as ConfigMode = "custom". But if the
        // user is just *peeking* at the Custom sub-tab without having actually
        // imported / selected a custom JSON config, persisting "custom" is a
        // foot-gun: on next StartAsync the engine reads ConfigMode="custom"
        // + empty CustomConfig path → throws "Custom config not found" → VPN
        // doesn't start. User reported this exact scenario after clicking
        // through tabs (2026-04-26 field test).
        //
        // Guard: only persist "custom" if there's actually a custom config
        // ready to use (either ActiveCustomConfig points at one OR the legacy
        // CustomConfig path is set OR there's at least one entry in the
        // CustomConfigs list). Otherwise fall back based on what's available:
        // subscriptions present → "subscribe", else → "generated".
        var wantsCustomMode = !IsSubscribeMode && !IsVlessMode;
        var hasCustomConfig = !string.IsNullOrWhiteSpace(_settings.App.ActiveCustomConfig)
                              || !string.IsNullOrWhiteSpace(_settings.App.CustomConfig)
                              || (_settings.App.CustomConfigs?.Count ?? 0) > 0;
        var hasActiveSubscription = (_settings.App.Subscriptions?.Any(s => s != null && s.Enabled) ?? false)
                                    || !string.IsNullOrWhiteSpace(_settings.App.SubscriptionUrl);

        if (wantsCustomMode && hasActiveSubscription)
        {
            // v2.30.1-r2 regression fix: subscription wins over peeking
            // at Custom sub-tab.
            //
            // The previous logic only fell back to "subscribe" when
            // hasCustomConfig was false. If the user had EVER imported
            // a custom config (so hasCustomConfig=true) AND was running
            // a subscription, the sequence:
            //
            //   Subscribe tab (IsSubscribeMode=true) → Servers tab
            //     (OnSelectedTabIndexChanged flips IsSubscribeMode=false,
            //      IsVlessMode=true) → Custom sub-tab
            //     (OnSelectedServerModeIndexChanged flips IsVlessMode=false
            //      + calls SaveSettings)
            //
            // would persist ConfigMode="custom" — even though the user
            // never explicitly chose to swap modes. The next Apply (e.g.
            // from Rules / Network page) would then reconnect using the
            // custom config branch instead of subscription.
            //
            // User report 2026-04-30: "я применил настройки и буд-то
            // переподключилось не на подписку а на конфиг".
            //
            // Fix: when an active subscription exists, peeking at sub-
            // tabs cannot flip ConfigMode away from "subscribe". To
            // genuinely switch to custom mode, the user must disable
            // every subscription first (the explicit Enabled checkbox
            // on each subscription entry).
            _settings.App.ConfigMode = "subscribe";
            _logger?.Information(
                "[Settings] Subscription is active — keeping ConfigMode=subscribe " +
                "even though Custom sub-tab is selected (user is peeking, not switching)");
        }
        else if (wantsCustomMode && !hasCustomConfig)
        {
            // No custom config ready and no subscription either → pick
            // the next best persistable mode so VPN can still start on
            // restart.
            _settings.App.ConfigMode = "generated";
            _logger?.Information(
                "[Settings] User clicked Custom sub-tab but no custom config is configured — keeping ConfigMode=generated instead of 'custom'");
        }
        else
        {
            _settings.App.ConfigMode = IsSubscribeMode ? "subscribe" : IsVlessMode ? "generated" : "custom";
        }

        // Persist all subscription entries (multi-subscription support)
        _settings.App.Subscriptions = Subscriptions.Select(sv => sv.ToEntry()).ToList();

        // Active server name — from aggregated pool
        var activeSub = SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault();
        _settings.App.ActiveSubscriptionServer = activeSub?.Name ?? "";

        // Clear legacy single-subscription fields (kept in model for read-only migration)
        _settings.App.SubscriptionUrl = string.Empty;
        _settings.App.SubscriptionServers = new();

        // Routing mode
        _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

        // v2.32 (r10) — Apps Include/Exclude 2-mode persist. Already
        // persisted eagerly in OnRoutingAppsModeChanged but written here
        // too so SaveSettings is the single source of truth on save.
        var appsModeCanon = (RoutingAppsMode ?? "include").Trim().ToLowerInvariant();
        if (appsModeCanon != "include" && appsModeCanon != "exclude") appsModeCanon = "include";
        _settings.App.RoutingAppsMode = appsModeCanon;

        // v2.30.0 — full custom rules (direct/proxy/block). Parse the
        // textbox + persist the structured list + populate two diagnostic
        // boxes (parse errors, conflict warnings). Valid lines still save
        // even if some lines errored.
        // CustomDirectRules legacy field is left empty; the migrator
        // already moved any v2.29 entries to CustomRules.
        try
        {
            var parsed = VPNRouter.Core.Services.CustomRulesParser
                .ParseFromText(CustomRulesText);
            _settings.App.CustomRules = parsed.Rules;
            CustomRulesErrorText = parsed.Errors.Count == 0
                ? string.Empty
                : string.Join("\n", parsed.Errors.Select(e =>
                    $"line {e.LineNumber}: {e.Reason}"));
            var conflicts = VPNRouter.Core.Services.CustomRulesParser
                .DetectConflicts(parsed.Rules);
            CustomRulesConflictText = conflicts.Count == 0
                ? string.Empty
                : string.Join("\n", conflicts);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] CustomRules parse failed");
        }

        // Russian geo bypass
        _settings.App.BypassRussianTraffic = BypassRussianTraffic;
        // v2.30.0-r17: persist priority too (set by OnCustomRulesAboveTogglesChanged
        // already, but mirror here for safety in case the OnChanged didn't fire
        // — e.g. during a programmatic load + immediate save).
        _settings.App.CustomRulesPriority = CustomRulesAboveToggles ? "custom_first" : "toggles_first";

        // Strict mode
        _settings.App.StrictMode = StrictMode;
        _settings.Tun.Mtu = TunMtu < 576 ? 576 : (TunMtu > 9000 ? 9000 : TunMtu);

        // IPv4 + DNS flush + Strict DNS
        _settings.App.ForceIpv4Only = ForceIpv4Only;
        _settings.App.FlushDnsOnStart = FlushDnsOnStart;
        _settings.App.StrictDns = StrictDns;
        _settings.App.BlockAds = BlockAds;
        _settings.Vless.AutoSelectBestServer = AutoSelectBestServer;
        _settings.App.ConnectionIntent = IntentFromIndex(ConnectionIntentIndex);
        // Wave 39 — DNS leak lockdown setting (default flipped per
        // SettingsMigrator: true for fresh installs, false for upgrades).
        _settings.App.DnsLeakLockdown = IsDnsLeakLockdownEnabled;
        _settings.App.AutostartVpn = AutostartVpn;
        _settings.App.AutostartZapret = AutostartZapret;
        _settings.App.AutostartTgProxy = AutostartTgProxy;
        _settings.App.AutostartUi = AutostartUi;
        _settings.App.ZapretEnabled = ZapretEnabled;
        _settings.App.ZapretStrategy = ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
            ? ZapretStrategies[ZapretStrategyIndex] : "multisplit";
        _settings.App.ZapretCustomArgs = ZapretCustomArgs;
        _settings.App.TgProxyEnabled = TgProxyEnabled;
        _settings.App.TgProxyPort = TgProxyPort;
        _settings.App.TgProxySecret = TgProxySecret;

        // Update channel
        _settings.Update.Channel = ReceivePrereleases ? "experimental" : "stable";

        // Theme & language. v2.40.x (Fix #7): persist the PREFERENCE
        // ("light"/"dark"/"system"), not the resolved variant — otherwise a
        // "system" choice would be flattened to whatever was showing.
        _settings.App.Theme = NormalizeThemePref(ThemePreference);
        _settings.App.Language = IsRussian ? "ru" : "en";
        _settings.App.UiMode = IsSimpleMode ? "simple" : "advanced";

        // Servers — save all + mark which one is active
        _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
        var activeVless = SelectedServer ?? Servers.FirstOrDefault();
        _settings.Vless.ActiveServer = activeVless?.Name ?? "";
        if (_settings.Vless.Servers.Count > 0)
        {
            // Write active server to root fields for backward compat
            var entry = activeVless?.ToEntry() ?? _settings.Vless.Servers[0];
            _settings.Vless.Server = entry.Server;
            _settings.Vless.Port = entry.Port;
            _settings.Vless.Uuid = entry.Uuid;
            _settings.Vless.Flow = entry.Flow;
            _settings.Vless.Security = entry.Security;
            _settings.Vless.Reality = entry.Reality;
        }

        // Custom configs
        _settings.App.CustomConfigs = CustomConfigs.Select(c => c.ToEntry()).ToList();
        var active = CustomConfigs.FirstOrDefault(c => c.IsActive);
        _settings.App.ActiveCustomConfig = active?.Name ?? "";

        // Safety: only persist Apps tab data if LoadApps has actually run.
        // Without this guard, an early SaveSettings (e.g. before user opens
        // Apps tab) would wipe ActiveProfile and CustomApps from disk.
        if (_appsLoaded)
        {
            var activeProfileNames = AppGroups
                .Where(g => g.IsChecked && g.Name != "Custom Apps")
                .Select(g => g.Name);
            _settings.ActiveProfile = string.Join(",", activeProfileNames);

            var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
            _settings.CustomApps = customGroup?.Apps
                .Select(a => a.ProcessName)
                .ToList() ?? new();

            // Persist user-added apps for every default group (except Custom Apps / custom categories)
            var customGroupApps = new Dictionary<string, List<string>>();
            foreach (var group in AppGroups)
            {
                if (group.Name == "Custom Apps" || group.IsCustomCategory) continue;
                var extras = group.Apps.Where(a => a.IsCustom).Select(a => a.ProcessName).ToList();
                if (extras.Count > 0)
                    customGroupApps[group.Name] = extras;
            }
            _settings.CustomGroupApps = customGroupApps;

            // Persist user-created categories (full content)
            _settings.CustomCategories = AppGroups
                .Where(g => g.IsCustomCategory)
                .Select(g => new CustomCategory
                {
                    Name = g.Name,
                    Enabled = g.IsChecked,
                    Apps = g.Apps.Select(a => a.ProcessName).ToList()
                })
                .ToList();

            // Bug-r9-I (2026-05-11): persist per-app exclusions inside
            // active default groups. Pre-r9-I the per-app checkbox was a
            // transient view state — only the group-level IsChecked made
            // it to disk. User reported (verbatim): «я каждый раз когда
            // захожу отправляю фаерфокс в исключения... а когда перезапускаю
            // винду галочка на нем опять стоит». Now an unchecked app
            // inside an active group survives Save → reload → reboot via
            // ExcludedApps + VpnEngine.RemoveExcludedApps.
            //
            // Custom Apps + IsCustomCategory groups are excluded from the
            // sweep because they already model "off" by removing/disabling
            // — no need for a parallel exclusion list there.
            //
            // AM-3 (2026-05-12): the sweep only runs in INCLUDE mode.
            // AppItem.IsChecked is now bridged to the active mode list,
            // so in exclude mode unchecked apps don't mean "exclude from
            // VPN" — they mean "this app isn't on the user's exclude
            // list", which is the opposite. Running the sweep in
            // exclude mode would push every unchecked app into
            // ExcludedApps and silently corrupt the legacy
            // VpnEngine.RemoveExcludedApps fallback path. We keep the
            // legacy field stable in exclude mode (leave existing
            // entries as-is so Apply / restart paths that still read
            // legacy data don't surprise the user).
            var sweepIsIncludeMode = !string.Equals(
                _settings.App.RoutingAppsMode, "exclude",
                StringComparison.OrdinalIgnoreCase);
            if (sweepIsIncludeMode)
            {
                var excluded = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in AppGroups)
                {
                    if (group.Name == "Custom Apps" || group.IsCustomCategory) continue;
                    if (!group.IsChecked) continue;
                    foreach (var app in group.Apps)
                    {
                        if (app.IsChecked) continue;
                        if (string.IsNullOrWhiteSpace(app.ProcessName)) continue;
                        if (seen.Add(app.ProcessName))
                            excluded.Add(app.ProcessName);
                    }
                }
                _settings.ExcludedApps = excluded;
            }
        }

        _settingsStore.Save(_settings, AppPaths.ConfigYamlPath);
    }

    partial void OnReceivePrereleasesChanged(bool value)
    {
        if (_isLoadingUI) return;
        _settings.Update.Channel = value ? "experimental" : "stable";
        _settingsStore.Save(_settings, AppPaths.ConfigYamlPath);
    }

}
