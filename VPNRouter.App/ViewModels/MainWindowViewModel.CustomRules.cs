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
    /// <summary>v2.30.0-r17 — when true, custom rules win over global
    /// toggles (BypassRussianTraffic + BlockAds). Default false (toggles
    /// first, same as r1-r16). Mirrors AppSettings.App.CustomRulesPriority
    /// "custom_first" / "toggles_first". User report 2026-04-29: «хочу
    /// чтоб кастомные правила были выше или переключатель что брать в
    /// приоритет».</summary>
    [ObservableProperty] private bool _customRulesAboveToggles;

    partial void OnCustomRulesAboveTogglesChanged(bool value)
    {
        if (_isLoadingUI) return;
        _settings.App.CustomRulesPriority = value ? "custom_first" : "toggles_first";
        SaveSettings();
        if (IsConnected) HasPendingAppChanges = true;
    }

    /// <summary>
    /// v2.30.0 — text-format mirror of <see cref="AppSettings.App.CustomRules"/>.
    /// User edits this multi-line string in the Network → Routing →
    /// "Custom rules (advanced)" textbox; SaveSettings parses it back
    /// to the structured list via <see cref="CustomRulesParser"/>.
    /// Errors during parse populate <see cref="CustomRulesErrorText"/>;
    /// catch-all rule warnings populate <see cref="CustomRulesConflictText"/>.
    /// </summary>
    [ObservableProperty] private string _customRulesText = string.Empty;

    /// <summary>v2.30.0 — error diagnostic shown below the textbox; empty
    /// when all lines parsed cleanly.</summary>
    [ObservableProperty] private string _customRulesErrorText = string.Empty;

    /// <summary>v2.30.0 — conflict warning (e.g. catch-all rule shadows
    /// subsequent rules). Surfaced in a separate diagnostic block below
    /// the parse-error block.</summary>
    [ObservableProperty] private string _customRulesConflictText = string.Empty;

    // v2.30.0-r2 — structured row-table for Network → Rules section.
    // Mirrors AppSettings.App.CustomRules. CustomRulesText (textbox) +
    // CustomRulesList (rows) are TWO views of the SAME underlying data.
    // Rebuilt on settings load + after each user edit (add/delete/toggle/
    // textbox change). To avoid feedback loop, _isSyncingCustomRules
    // suppresses cross-update during the rebuild.
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> CustomRulesList { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    private bool _isSyncingCustomRules;

    // v2.30.0-r4 — search filter + bulk actions for large rule sets.
    // User concern: «обычно если импортирую какой-то список правил из
    // git ок включает в себя 100 и более правил». Without virtualization
    // + search, 100+ rows became painful: ItemsControl rendered all,
    // no way to find specific rule, no bulk operations. r4 adds:
    //   1. ListBox + VirtualizingStackPanel (handled in XAML).
    //   2. CustomRulesSearchText filter — substring match across
    //      action/type/value/comment.
    //   3. FilteredCustomRulesList — view rebuilt on filter change,
    //      bound by ListBox.ItemsSource.
    //   4. CustomRulesCountText — "showing N of M" display.
    //   5. Bulk action commands: Clear all, Enable all, Disable all.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomRulesCountText))]
    private string _customRulesSearchText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> FilteredCustomRulesList { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();

    /// <summary>v2.30.0-r4 — "Showing 12 of 248 rules" display.</summary>
    public string CustomRulesCountText
    {
        get
        {
            var total = CustomRulesList.Count;
            var shown = string.IsNullOrWhiteSpace(CustomRulesSearchText)
                ? total
                : FilteredCustomRulesList.Count;
            if (total == 0) return string.Empty;
            if (string.IsNullOrWhiteSpace(CustomRulesSearchText) || shown == total)
                return IsRussian ? $"Всего: {total}" : $"Total: {total}";
            return IsRussian
                ? $"Показано: {shown} из {total}"
                : $"Showing: {shown} of {total}";
        }
    }

    /// <summary>v2.30.0-r4 — apply CustomRulesSearchText to CustomRulesList,
    /// repopulate FilteredCustomRulesList. Called on search-text change
    /// + on every CustomRulesList change.</summary>
    private void RebuildFilteredCustomRulesList()
    {
        FilteredCustomRulesList.Clear();
        var query = (CustomRulesSearchText ?? string.Empty).Trim().ToLowerInvariant();
        var actionFilter = RulesActionFilter ?? "all";

        // Per-action counts BEFORE filter — drives the segment-control
        // counters next to each chip label (so the user can see how
        // many rules of each type exist regardless of current filter).
        int total = 0, direct = 0, proxy = 0, block = 0;

        foreach (var vm in CustomRulesList)
        {
            total++;
            switch (vm.Action)
            {
                case "direct": direct++; break;
                case "proxy":  proxy++;  break;
                case "block":  block++;  break;
            }

            // Apply both filters: action AND search.
            if (actionFilter != "all" &&
                !string.Equals(vm.Action, actionFilter, System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (query.Length > 0)
            {
                var haystack = $"{vm.Action} {vm.Type} {vm.Value} {vm.Comment}".ToLowerInvariant();
                if (!haystack.Contains(query)) continue;
            }
            FilteredCustomRulesList.Add(vm);
        }

        RulesFilterCountAll    = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountDirect = direct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountProxy  = proxy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountBlock  = block.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // v2.30.0-r12 — keep Read-mode groups in sync. Cheap (O(N)
        // single pass) and only meaningful when user is in Read view,
        // but rebuilding always avoids stale data when they flip into it.
        RebuildReadModeGroups();

        OnPropertyChanged(nameof(CustomRulesCountText));
    }

    partial void OnCustomRulesSearchTextChanged(string value) => RebuildFilteredCustomRulesList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleActionHint))]
    private string _newRuleAction = "direct";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleTypeHint))]
    [NotifyPropertyChangedFor(nameof(NewRuleValuePlaceholder))]
    private string _newRuleType = "domain_suffix";

    [ObservableProperty] private string _newRuleValue = string.Empty;
    [ObservableProperty] private string _newRuleComment = string.Empty;
    [ObservableProperty] private string _newRuleValidationError = string.Empty;

    // v2.30.0-r11 — live-validation per type for the Add-form Value field.
    // typeMeta from RulesPage.html: each type has a placeholder, a hint,
    // and a regex (or RegExp ctor for domain_regex). We translate the live
    // regex check to NewRuleValueIsValid + NewRuleValueHint + a colored
    // border. Empty value = neutral (hint shows the per-type guidance).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleValueBorderColor))]
    private bool _newRuleValueIsValid = true;

    [ObservableProperty] private string _newRuleValueHint = string.Empty;
    [ObservableProperty] private string _newRuleValuePlaceholder = ".corp.example";

    /// <summary>True when the value is INVALID (Border-color converter
    /// uses bool->Brush param "DangerBorder|SuccessBorder", so true means
    /// danger). When the value is empty, kept false (= success/default).</summary>
    public bool NewRuleValueBorderColor => !NewRuleValueIsValid;

    /// <summary>Live "this action does X" hint shown under the Action
    /// ComboBox in the Add-form. Per design `updateActionColor` JS handler.
    /// v2.30.6-r1 (UX-13): hints now spell out the concrete behavior so
    /// users without sing-box background know what each action does.</summary>
    public string NewRuleActionHint => NewRuleAction switch
    {
        // v2.37.0-r13 — localized text moved to Strings.cs.
        "direct" => Strings.RuleActionHintDirect,
        "proxy"  => Strings.RuleActionHintProxy,
        "block"  => Strings.RuleActionHintBlock,
        _ => string.Empty,
    };

    /// <summary>Per-type guidance text shown under the Type ComboBox + as
    /// the default Value-hint. From RulesPage.html `typeMeta[type].hint`.
    /// v2.30.6-r1 (UX-13): every hint now embeds a concrete example so the
    /// raw sing-box term ("domain_suffix") makes immediate sense.</summary>
    public string NewRuleTypeHint => NewRuleType switch
    {
        // v2.37.0-r13 — localized text moved to Strings.cs.
        "domain"         => Strings.RuleTypeHintDomain,
        "domain_suffix"  => Strings.RuleTypeHintDomainSuffix,
        "domain_keyword" => Strings.RuleTypeHintDomainKeyword,
        "ip_cidr"        => Strings.RuleTypeHintIpCidr,
        "port"           => Strings.RuleTypeHintPort,
        "port_range"     => Strings.RuleTypeHintPortRange,
        "network"        => Strings.RuleTypeHintNetwork,
        "process_name"   => Strings.RuleTypeHintProcessName,
        "process_path"   => Strings.RuleTypeHintProcessPath,
        "geosite"        => Strings.RuleTypeHintGeosite,
        "geoip"          => Strings.RuleTypeHintGeoip,
        _                => string.Empty,
    };

    /// <summary>Compiled regex per type for live-validation of the Value
    /// input. <c>domain_regex</c> uses runtime <c>new Regex(input)</c>
    /// validity check instead of a fixed pattern.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, System.Text.RegularExpressions.Regex> _typeValidatorMap = new()
    {
        ["domain"]         = new(@"^[a-z0-9.-]+\.[a-z]{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["domain_suffix"]  = new(@"^\.?[a-z0-9.-]+\.[a-z]{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["domain_keyword"] = new(@"^[a-z0-9.\-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["ip_cidr"]        = new(@"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$|^[0-9a-f:]+/\d{1,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["port"]           = new(@"^\d{1,5}(\s*,\s*\d{1,5})*$", System.Text.RegularExpressions.RegexOptions.Compiled),
        ["port_range"]     = new(@"^\d{1,5}-\d{1,5}$", System.Text.RegularExpressions.RegexOptions.Compiled),
        ["network"]        = new(@"^(tcp|udp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        // r17: process_name accepts both with and without .exe (Mac/Linux
        // process names are bare like "chrome", "discord"; Windows can be
        // "chrome.exe" or "chrome"). sing-box matches case-sensitively
        // against the executable file basename.
        ["process_name"]   = new(@"^[\w.\-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        // r17: process_path accepts Windows (C:\), Mac/Linux (/), and
        // arbitrary segment characters (.app bundles need spaces).
        ["process_path"]   = new(@"^([A-Z]:\\|/).+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["geosite"]        = new(@"^[a-z][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["geoip"]          = new(@"^[a-z][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
    };

    /// <summary>Per-type Value-input placeholder. Updates when user
    /// changes Type. From RulesPage.html `typeMeta[type].ph`.</summary>
    /// <summary>v2.30.0-r17 — OS-aware placeholders. process_name and
    /// process_path differ between Windows and Mac/Linux (no .exe
    /// extension on Unix; different path conventions). User report:
    /// «пункт process_name предлагает .exe даже на Mac в примере».</summary>
    private string ResolveValuePlaceholder(string type) => type switch
    {
        "domain"         => "mail.example.com",
        "domain_suffix"  => ".corp.example",
        "domain_keyword" => "doubleclick",
        "ip_cidr"        => "10.0.0.0/8",
        "port"           => "443  or  80, 443",
        "port_range"     => "1000-2000",
        "network"        => "tcp",
        "process_name"   => System.OperatingSystem.IsWindows() ? "chrome.exe" : "chrome",
        "process_path"   => System.OperatingSystem.IsWindows()
            ? "C:\\Program Files\\app\\app.exe"
            : System.OperatingSystem.IsMacOS()
                ? "/Applications/App.app/Contents/MacOS/App"
                : "/usr/bin/app",
        "geosite"        => "cn",
        "geoip"          => "cn",
        _                => string.Empty,
    };

    partial void OnNewRuleTypeChanged(string value)
    {
        NewRuleValuePlaceholder = ResolveValuePlaceholder(value);
        // Re-validate the existing value against the new type rules.
        ValidateNewRuleValue(NewRuleValue);
    }

    partial void OnNewRuleValueChanged(string value) => ValidateNewRuleValue(value);

    partial void OnConnectionIntentIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        _settings.App.ConnectionIntent = IntentFromIndex(value);
        SaveSettings();
    }

    private static string IntentFromIndex(int value) => value switch
    {
        1 => VPNRouter.Core.Models.ConnectionIntent.Gaming,
        2 => VPNRouter.Core.Models.ConnectionIntent.Privacy,
        3 => VPNRouter.Core.Models.ConnectionIntent.Compatibility,
        _ => VPNRouter.Core.Models.ConnectionIntent.General
    };

    private static int IntentToIndex(string? value) => VPNRouter.Core.Models.ConnectionIntent.Normalize(value) switch
    {
        VPNRouter.Core.Models.ConnectionIntent.Gaming => 1,
        VPNRouter.Core.Models.ConnectionIntent.Privacy => 2,
        VPNRouter.Core.Models.ConnectionIntent.Compatibility => 3,
        _ => 0
    };

    private void ValidateNewRuleValue(string val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            // Empty = neutral state: show the type's default guidance,
            // border stays default (not danger).
            NewRuleValueIsValid = true;
            NewRuleValueHint = NewRuleTypeHint;
            return;
        }

        bool ok;
        if (NewRuleType == "domain_regex")
        {
            try { _ = new System.Text.RegularExpressions.Regex(val); ok = true; }
            catch { ok = false; }
        }
        else if (_typeValidatorMap.TryGetValue(NewRuleType, out var regex))
        {
            ok = regex.IsMatch(val.Trim());
        }
        else
        {
            ok = true; // Unknown type — don't block.
        }

        NewRuleValueIsValid = ok;
        NewRuleValueHint = ok
            ? (IsRussian ? "✓ корректно" : "✓ valid")
            : (IsRussian ? $"✗ не подходит формату {NewRuleType}" : $"✗ wrong format for {NewRuleType}");
    }

    // v2.30.0-r11 — Action filter chips state.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterDirect))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterProxy))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterBlock))]
    private string _rulesActionFilter = "all";

    public bool IsRulesFilterAll    => RulesActionFilter == "all";
    public bool IsRulesFilterDirect => RulesActionFilter == "direct";
    public bool IsRulesFilterProxy  => RulesActionFilter == "proxy";
    public bool IsRulesFilterBlock  => RulesActionFilter == "block";

    /// <summary>Per-action counts shown in the filter chip secondary text.
    /// Refreshed by <see cref="RebuildFilteredCustomRulesList"/>.</summary>
    [ObservableProperty] private string _rulesFilterCountAll    = string.Empty;
    [ObservableProperty] private string _rulesFilterCountDirect = string.Empty;
    [ObservableProperty] private string _rulesFilterCountProxy  = string.Empty;
    [ObservableProperty] private string _rulesFilterCountBlock  = string.Empty;

    [RelayCommand]
    private void SetRulesActionFilter(string filter)
    {
        if (string.IsNullOrEmpty(filter)) filter = "all";
        RulesActionFilter = filter;
        RebuildFilteredCustomRulesList();
    }

    /// <summary>Static list of action options for the Add-rule ComboBox.</summary>
    public IReadOnlyList<string> AvailableRuleActions { get; }
        = new[] { "direct", "proxy", "block" };

    /// <summary>Static list of type options for the Add-rule ComboBox.
    /// Order matches the textbox grammar documentation for UX consistency.
    /// <para>v2.31.0-r4 (AU-10): added <c>domain_regex</c> + <c>process_path</c>
    /// so Cards-mode now exposes the same surface that the Edit-mode
    /// validator (line ~951) already accepts. Pre-fix users could author
    /// these rule types only via raw textbox grammar; the Add-form
    /// ComboBox didn't list them, leading to a silent surface mismatch.</para>
    /// </summary>
    public IReadOnlyList<string> AvailableRuleTypes { get; }
        = new[]
        {
            "domain", "domain_suffix", "domain_keyword", "domain_regex",
            "ip_cidr", "port", "port_range", "network",
            "process_name", "process_path", "geosite", "geoip",
        };

    [ObservableProperty] private bool _strictMode = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TunMtuWarning))]
    private int _tunMtu = TunSettings.DefaultMtu;
    public string TunMtuWarning => TunMtu < 1332
        ? Strings.MtuWarningLow
        : TunMtu > TunSettings.DefaultMtu
            ? Strings.MtuWarningHigh
            : string.Empty;
    [ObservableProperty] private bool _isMtuAutoTuneRunning;
    [ObservableProperty] private string _mtuAutoTuneStatus = string.Empty;
    [ObservableProperty] private bool _forceIpv4Only = true;
    [ObservableProperty] private bool _flushDnsOnStart = true;
    [ObservableProperty] private bool _strictDns = false;
    [ObservableProperty] private bool _blockAds = false;
    // Backlog A (2026-06-20): opt-in auto-select fastest reachable subscription
    // server via sing-box urltest. Persisted to Vless.AutoSelectBestServer; takes
    // effect on next connect/Apply (like BlockAds). Toggle on the Subscribe page.
    [ObservableProperty] private bool _autoSelectBestServer = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionIntentStatusText))]
    private int _connectionIntentIndex;
    public IReadOnlyList<string> ConnectionIntentChoices => IsRussian
        ? new[] { "Сайты и мессенджеры", "Игры и звонки", "Максимум приватности", "Максимальная совместимость" }
        : new[] { "Sites and messaging", "Games and calls", "Maximum privacy", "Maximum compatibility" };
    public string ConnectionIntentStatusText => ConnectionIntentIndex switch
    {
        1 => IsRussian ? "Авто: игры и звонки" : "Auto: games and calls",
        2 => IsRussian ? "Авто: приватность" : "Auto: privacy",
        3 => IsRussian ? "Авто: совместимость" : "Auto: compatibility",
        _ => IsRussian ? "Авто: обычный режим" : "Auto: general"
    };
    // Wave 39 (v2.35.0-r5): firewall-level DNS lockdown. When ON, the
    // FirewallManager adds outbound block rules for UDP/53, TCP/53, TCP/853
    // on all non-TUN interfaces while VPN is active. Protects against the
    // Windows DNS Client multi-resolver race that survives our existing
    // SMHNR/ParallelAAAA registry hardening (some Win11 22H2+ paths query
    // every configured resolver in parallel regardless of the registry
    // settings). Default true for the property — Agent A's AppSettings
    // change defaults the underlying setting to true for new installs and
    // false for upgrades via SettingsMigrator. See
    // plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBadComboWarningVisible))]
    private bool _isDnsLeakLockdownEnabled = true;

    /// <summary>
    /// v2.37.0-r36 — surface known-incompatible setting combos as a UI banner.
    /// Currently a single combo: DnsLeakLockdown ∩ BypassRussianTraffic.
    /// LeakProtection.CollectIncompatibleSettings has the canonical text +
    /// rationale; the banner shows a short label + Disable buttons.
    /// </summary>
    public bool IsBadComboWarningVisible =>
        BypassRussianTraffic && IsDnsLeakLockdownEnabled;

    public string LblBadComboWarningTitle =>
        IsRussian
            ? "Несовместимые настройки могут ломать интернет"
            : "Incompatible settings may break the internet";

    public string LblBadComboWarningBody =>
        IsRussian
            ? "«Блокировать DNS вне VPN» + «Российский трафик через реальный IP» — RU-домены не будут резолвиться. Отключите одну."
            : "\"Block DNS outside VPN\" + \"Russian traffic via real IP\" conflict — RU domains won't resolve. Disable one.";

    public string LblBadComboDisableLockdown =>
        IsRussian
            ? "Отключить DNS-lockdown"
            : "Disable DNS lockdown";

    public string LblBadComboDisableRuBypass =>
        IsRussian
            ? "Отключить RU-bypass"
            : "Disable RU bypass";

    [RelayCommand]
    private void DisableBadComboLockdown() => IsDnsLeakLockdownEnabled = false;

    [RelayCommand]
    private void DisableBadComboRuBypass() => BypassRussianTraffic = false;

    // Apply changes (hot-reload) UX state
    [ObservableProperty] private bool _hasPendingAppChanges;
    [ObservableProperty] private bool _isApplying;

    // Autostart
    [ObservableProperty] private bool _autostartVpn = false;
    [ObservableProperty] private bool _autostartZapret = false;
    [ObservableProperty] private bool _autostartTgProxy = false;
    [ObservableProperty] private bool _autostartUi = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblDpiToggle))]
    // v2.36.0-r8 — hero labels swap between Stopped/Running on this flag.
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretMagicButton))]
    // r34 — Hero quick-strategy row visibility.
    [NotifyPropertyChangedFor(nameof(HasZapretStrategiesForQuickStart))]
    private bool _zapretEnabled = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomStrategy))]
    private int _zapretStrategyIndex = 0;
    public bool IsCustomStrategy => ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
        && ZapretStrategies[ZapretStrategyIndex] == "custom";
    [ObservableProperty] private string _zapretCustomArgs = string.Empty;
    // v2.37.0-r7 — uses Strings.Stopped (RU «Остановлен» / EN "Stopped")
    // instead of hardcoded English literal. Pre-r7 the field default leaked
    // English into RU UI on first launch. Re-init on language change handled
    // by ReloadMainWindowForLocalization (window rebuild rebinds the VM).
    [ObservableProperty] private string _zapretStatus = Strings.Stopped;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblDiscordHosts))]
    private bool _discordHostsInstalled = false;
    [ObservableProperty] private string _zapretVersionText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretMagicButtonEnabled))]
    private bool _isZapretDownloading = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasZapretStrategiesForQuickStart))]
    private System.Collections.ObjectModel.ObservableCollection<string> _zapretStrategies = new();
    private List<VPNRouter.Core.Services.ZapretStrategy> _parsedStrategies = new();

    /// <summary>
    /// v2.37.0-r36 — strategy name + verification status badge for the
    /// Hero quick-strategy mini-row. 1:1 indexed with <see cref="ZapretStrategies"/>,
    /// so selection (via <see cref="ZapretStrategyIndex"/>) stays in sync.
    /// Raw names are still used for execution; this is display-only.
    ///
    /// <para>Format:</para>
    /// <list type="bullet">
    ///   <item>Cached winner with score: <c>"general (ALT3)  ✓ 5/5"</c></item>
    ///   <item>Cached winner without score (legacy v1 cache): <c>"general (ALT3)  ✓"</c></item>
    ///   <item>Stale cached winner: <c>"general (ALT3)  ⚠ устарело"</c></item>
    ///   <item>Other strategies: just the name (no probe data per-strategy yet)</item>
    /// </list>
    ///
    /// <para>Future r37+: extend <see cref="ZapretProbeCache"/> to track
    /// per-strategy results (not just the winner) so every entry can carry
    /// a verification badge. For r36 we surface only the cached winner.</para>
    /// </summary>
    // r46 — changed element type from `string` to `ZapretStrategyDisplayItem`
    // so the ComboBox ItemTemplate can color the glyph independently of the
    // strategy name. Pre-r46 a single string carried "✓ general (ALT3)" and
    // there was no way to color just "✓" green without inline RichText parsing.
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<ZapretStrategyDisplayItem> _zapretStrategiesDisplay = new();

    /// <summary>r34 — controls visibility of the Hero quick-strategy
    /// mini-row (ComboBox + ▶). Visible only when:
    ///   - Strategies list is populated (Zapret installed and parsed)
    ///   - Not currently probing (would be redundant during auto-probe)
    ///   - Not currently running (already started)
    /// Hidden when there's no quick-start to offer.</summary>
    public bool HasZapretStrategiesForQuickStart =>
        ZapretStrategies != null
        && ZapretStrategies.Count > 0
        && !IsZapretProbing
        && !ZapretEnabled;
    [ObservableProperty] private bool _receivePrereleases = false;

    // v2.36.0-r8 (cross-platform field) — suppress flag for Bug-r9-G AV toast
    // during ZapretAutoStrategy probe loop. Declared at top-level (NOT inside
    // #if PLATFORM_WINDOWS) because OnZapretImmediateExit is also cross-
    // platform — Mac/Linux compile would fail otherwise (caught by r8 CI run
    // 26371608493).
    private bool _suppressZapretAvToast = false;

    // v2.36.0-r8 — ZapretOneTap design state. Three-axis state drives the
    // hero card title/lede/chip visibility on DpiBypassPage:
    //   _isZapretProbing  — true while ZapretAutoStrategy.ProbeAsync loops
    //   _zapretProbeIndex / _zapretProbeTotal — for hero chip "Тестирую (i/N)"
    //   _zapretProbeStrategy — current attempt name
    //   _zapretWinningStrategy — set on Tier1 success; surfaces in air-pill
    //   _isZapretFallback — set when all attempts fail; hero shows manual hint
    // All flip together; NotifyPropertyChangedFor on the hero label
    // computed properties picks up state transitions.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(IsZapretMagicButtonEnabled))]
    // r34 — Hero quick-strategy row visibility.
    [NotifyPropertyChangedFor(nameof(HasZapretStrategiesForQuickStart))]
    private bool _isZapretProbing = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    private int _zapretProbeIndex = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    private int _zapretProbeTotal = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    private string _zapretProbeStrategy = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretAirPill))]
    private string _zapretWinningStrategy = string.Empty;

    // v2.37.0-r1 — multi-target probe score. Set by per-attempt progress
    // reporter. Surfaces in hero lede ("Тестирую (1/3): general — 7/8 ok") +
    // air-pill ("В эфире · general (ALT3) · 7/8") so the user can see
    // confidence in the picked strategy.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretAirPill))]
    private int _zapretProbePassCount = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretAirPill))]
    private int _zapretProbeTotalCount = 0;

    // r39 — last probe's log file path, surfaced in DpiBypassPage as a
    // clickable "Open probe log" link. Lets users attach the log to bug
    // reports without needing to know %ProgramData% / dig for the file.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastProbeLog))]
    private string? _lastProbeLogPath = null;

    /// <summary>r39 — drives visibility of the "Open probe log" link.</summary>
    public bool HasLastProbeLog => !string.IsNullOrEmpty(LastProbeLogPath);

    public string LblOpenProbeLog =>
        IsRussian ? "Открыть лог проверки" : "Open probe log";

    /// <summary>
    /// r45 — legend for the strategy-badge glyphs in the Hero ComboBox.
    /// Static localized string. Lives below the dropdown so users can
    /// decode ✓/⚠/✗/◌/⏱ without hovering each item.
    /// Kept for compatibility — r46+ uses per-label LblLegend* below
    /// with colored mini-blocks instead of single line.
    /// </summary>
    public string LblStrategyBadgeLegend =>
        IsRussian
            ? "✓ работает   ⚠ частично   ✗ не работает   ◌ не проверена   ⏱ устарело"
            : "✓ working   ⚠ partial   ✗ failed   ◌ untested   ⏱ stale";

    // r46 — per-label legend strings for the colored WrapPanel legend.
    // (r50: legend now lives in ComboBox tooltip — see LblStrategyBadgeLegend
    // above — but per-label strings kept for any future inline use.)
    public string LblLegendWorking  => IsRussian ? "работает"     : "working";
    public string LblLegendPartial  => IsRussian ? "частично"     : "partial";
    public string LblLegendFailed   => IsRussian ? "не работает"  : "failed";
    public string LblLegendUntested => IsRussian ? "не проверена" : "untested";
    public string LblLegendStale    => IsRussian ? "устарело"     : "stale";

    // r50 — label for the Hero quick-strategy ▶ button. Pre-r50 was just
    // a bare ▶ glyph; user feedback flagged it as an unlabeled fourth
    // action without context.
    public string LblZapretRunSelected =>
        IsRussian ? "Запустить" : "Run";

    [RelayCommand]
    private void OpenProbeLog()
    {
        var path = LastProbeLogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger?.Information("[VM] OpenProbeLog: no log path or file missing ({Path})", path);
            return;
        }
        try
        {
            // Open in default text editor (notepad) — `explorer "<path>"`
            // would open the folder; `start "" "<path>"` via cmd uses the
            // default text handler.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
            });
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] OpenProbeLog failed for {Path}", path);
        }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    private bool _isZapretFallback = false;

    // Bug-r9-G (2026-05-11) — Zapret AV-block toast. Set when
    // ZapretManager.ImmediateExitDetected fires (winws.exe exited within
    // < 2 s with non-zero code). Auto-clears after 8 s (longer than the
    // 2-3 s rules toast pattern because the user needs time to read the
    // whitelist path and click "Copy path"). Dismissable via X button.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasZapretAvBlockToast))]
    private string _zapretAvBlockToast = string.Empty;

    public bool HasZapretAvBlockToast => !string.IsNullOrWhiteSpace(ZapretAvBlockToast);

    private System.Threading.CancellationTokenSource? _zapretAvBlockToastCts;

    private void OnZapretImmediateExit()
    {
        // v2.36.0-r8: during ZapretOneTap probing, fast-exits are EXPECTED
        // (we deliberately try strategies that may not work) so we suppress
        // the AV-block toast which would otherwise flash up for each
        // failed attempt. ZapretAutoStrategy.ProbeAsync routes the immediate
        // exit through its own per-attempt TaskCompletionSource and uses it
        // to short-circuit the doomed strategy fast.
        if (_suppressZapretAvToast) return;

        // Marshal to UI thread — Process.Exited fires on a threadpool.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ZapretAvBlockToast = Strings.ZapretAvBlockToast;
            // Reset auto-hide timer.
            var oldCts = _zapretAvBlockToastCts;
            _zapretAvBlockToastCts = new System.Threading.CancellationTokenSource();
            var token = _zapretAvBlockToastCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                oldCts.Dispose();
            }
            _ = System.Threading.Tasks.Task.Delay(8000, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested) ZapretAvBlockToast = string.Empty;
                });
            }, System.Threading.Tasks.TaskScheduler.Default);
        });
    }

    /// <summary>
    /// Bug-r9-G — convenience for the toast's "Copy path" button.
    /// Puts the canonical Zapret folder into the clipboard so the user
    /// can paste it directly into their AV's exception list.
    /// </summary>
    [RelayCommand]
    private async Task CopyZapretWhitelistPathAsync()
    {
        var path = @"C:\ProgramData\VPNRouter\zapret\";
        try
        {
            var window = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null;
            if (window?.Clipboard != null)
                await window.Clipboard.SetTextAsync(path);
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VM] Failed to copy Zapret whitelist path");
        }
    }

    [RelayCommand]
    private void DismissZapretAvBlockToast() => ZapretAvBlockToast = string.Empty;

    // Telegram proxy
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblTgProxyToggle))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyMainAction))]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    // v2.36.0-r7: hero re-narrates between stopped/running states.
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroLede))]
    private bool _tgProxyEnabled = false;
    // v2.37.0-r7 — uses Strings.Stopped. Same CLAUDE.md D1 fix as
    // ZapretStatus above. Window rebuild on language change re-instantiates
    // the VM so this picks up the new Lang.
    [ObservableProperty] private string _tgProxyStatus = Strings.Stopped;
    [ObservableProperty]
    // v2.36.0-r7: lede + air-pill template substitute live port.
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyAirPill))]
    private int _tgProxyPort = 1443;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private string _tgProxySecret = "";
    [ObservableProperty] private string _tgProxyLink = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private string _tgProxyVersionText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private bool _isTgProxyDownloading = false;
    // v2.37.0-r15 — TgProxyStats now surfaced in TelegramPage air-pill.
    // HasTgProxyStats is a computed boolean (non-empty after first parse)
    // that gates the inline TextBlock IsVisible binding. Pre-r15 the field
    // existed but no XAML consumer — pure dead plumbing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTgProxyStats))]
    private string _tgProxyStats = "";

    public bool HasTgProxyStats => !string.IsNullOrEmpty(TgProxyStats);

    /// <summary>
    /// v2.31.6-r4 (BUG #3 fix): transient toast banner shown above
    /// the persistent <see cref="TgProxyStatus"/>. Used by
    /// <see cref="ShowTgProxyToast"/> to surface "Copied!", "Telegram
    /// not installed", "New secret — restart proxy" and similar
    /// confirmations without overwriting the runtime status field.
    /// Auto-clears after 2.5 s; latest-write wins via a token guard.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTgProxyToast))]
    private string _tgProxyToast = string.Empty;

    public bool HasTgProxyToast => !string.IsNullOrEmpty(TgProxyToast);

    /// <summary>
    /// v2.36 (MVP one-button): non-blocking warning banner state.
    /// True when the <c>tg://</c> URI scheme has no registered
    /// handler at startup-time pre-flight, meaning Telegram Desktop
    /// is missing or not associated with the scheme. The proxy still
    /// starts (user might pair via QR code on another device or
    /// copy the link manually), but the banner offers a fallback
    /// (Copy link + download Telegram hint).
    ///
    /// <para>Pre-fix the check fired only inside the final deep-link
    /// open path (<see cref="OpenTgProxyInTelegram"/>), so a fresh
    /// user clicking the footer button got the OS-error dialog
    /// "We can't open this 'tg' link" instead of a contextual
    /// banner pointing at the cause + fallback.</para>
    /// </summary>
    [ObservableProperty]
    private bool _isTelegramSchemeWarningVisible;

    /// <summary>
    /// v2.36 (MVP one-button): per-step status text shown during a
    /// running download. Drives the existing
    /// <see cref="TgProxyStatus"/> field today; isolated property
    /// so a future UI iteration can split the persistent runtime
    /// status from the transient download progress.
    /// </summary>
    [ObservableProperty]
    private string _tgProxyDownloadStep = string.Empty;

    public bool HasTgProxyDownloadStep => !string.IsNullOrEmpty(TgProxyDownloadStep);

    partial void OnTgProxyDownloadStepChanged(string value)
    {
        OnPropertyChanged(nameof(HasTgProxyDownloadStep));
    }

    /// <summary>
    /// v2.31.6-r1 (TelegramPage UX simplification): true when the
    /// user has already set up the Telegram proxy at least once —
    /// binary is downloaded AND a secret has been generated. Drives
    /// the two-state TelegramPage layout: <c>false</c> shows the
    /// onboarding "Set up Telegram proxy" CTA, <c>true</c> shows the
    /// run/stop status surface. Power-user controls (port / secret /
    /// version / folder / GitHub) live behind the Advanced expander
    /// in both states so the page never overwhelms a first-time user
    /// while keeping every existing knob reachable.
    /// </summary>
    public bool IsTgProxySetUp =>
        !string.IsNullOrWhiteSpace(TgProxySecret)
        && !string.IsNullOrWhiteSpace(TgProxyVersionText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServersTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSubscribeTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsNetworkTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsAppsTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsToolsTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsFreeConfigsTabSelected))]
    private int _selectedTabIndex;

    public bool IsServersTabSelected => SelectedTabIndex == 0;
    public bool IsSubscribeTabSelected => SelectedTabIndex == 1;
    public bool IsNetworkTabSelected => SelectedTabIndex == 2;
    public bool IsAppsTabSelected => SelectedTabIndex == 3;
    public bool IsToolsTabSelected => SelectedTabIndex == 4;
    public bool IsFreeConfigsTabSelected => SelectedTabIndex == 5;

    // Servers sub-tabs (VLESS / Custom Config)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlessMode))]
    private int _selectedServerModeIndex;

    partial void OnSelectedServerModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        // v2.30.2-r1 diag: trace sub-tab clicks so the SaveSettings r2
        // guard activations are auditable from a single VM event.
        _logger?.Information(
            "[VM] OnSelectedServerModeIndexChanged value={V} (was IsVlessMode={IV}, IsSubscribeMode={IS})",
            value, IsVlessMode, IsSubscribeMode);
        // Sync IsVlessMode with sub-tab index (0=VLESS, 1=Custom)
        IsVlessMode = value == 0;
        SaveSettings();
    }

    /// <summary>v2.30.0 — auto-save when user edits the Custom Rules
    /// textbox. Throttled by Avalonia's TextBox change-on-commit
    /// (focus loss / Enter), so we don't spam SaveSettings on every
    /// keystroke. Errors during parse populate the inline diagnostic
    /// boxes but don't block save (valid lines persist).</summary>
    partial void OnCustomRulesTextChanged(string value)
    {
        if (_isLoadingUI) return;
        if (_isSyncingCustomRules) return;
        SaveSettings();
        // SaveSettings writes parse errors + conflict warnings.
        // Notify so the UI re-binds diagnostic blocks.
        OnPropertyChanged(nameof(CustomRulesErrorText));
        OnPropertyChanged(nameof(CustomRulesConflictText));
        // v2.30.0-r2: rebuild CustomRulesList rows from the parsed
        // structured list so the structured view stays in sync with
        // textbox edits.
        RebuildCustomRulesList();

        // v2.30.0-r7: refresh dirty state of the Edit-mode buffer if Edit
        // view is active. Apply commits EditedCustomRulesText → CustomRulesText
        // which lands here, so dirty must clear naturally.
        OnPropertyChanged(nameof(RulesEditorIsDirty));

        // v2.30.0-r17: rules-change-while-running surface (same as
        // FlushCustomRulesListToSettings). Edit-mode Apply lands here.
        if (IsConnected) HasPendingAppChanges = true;
    }

    // ═══════════════════════════════════════════════════════════════
    // v2.30.0-r7 — Cards / Edit view-mode toggle (RulesExplorations.html
    // design handoff). Replaces the old "Advanced (text format)" expander
    // at the bottom of the section. Two modes:
    //   1. Cards (▦) — structured row-table editor, default; same UI as
    //      v2.30.0-r6.
    //   2. Edit (✎) — full textarea editor with line-numbered gutter,
    //      per-line errors, explicit Apply / Revert buttons (no auto-save
    //      while typing — that was the OLD Advanced expander's behavior).
    //
    // Why the buffered Edit mode: power users editing 100+ rules in text
    // form should see live error markers, but each intermediate keystroke
    // shouldn't commit (e.g. typing "doma" → "domain_suffix" parses cleanly
    // only at the final state). The buffer + Apply pattern lets the user
    // make any-state edits, see errors, fix them, then commit atomically.
    // Revert rolls back to the canonical CustomRulesText snapshot.
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesViewCards))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewRead))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewEdit))]
    private string _rulesViewMode = "cards";

    /// <summary>v2.30.0-r13 — true when the Rules pane is rendered in a
    /// narrow viewport (&lt;540 px). Drives responsive template swaps:
    /// Add-form 5-col -> 4-row stack, toolbar 3-col -> 2-row, etc.
    /// Fed by NetworkPage.axaml.cs SizeChanged handler.</summary>
    [ObservableProperty] private bool _isRulesNarrow;

    /// <summary>True when Cards view is active (default).</summary>
    public bool IsRulesViewCards => RulesViewMode == "cards";

    /// <summary>True when Read (read-only grouped monospace) view is active.
    /// v2.30.0-r12 — added per design RulesExplorations.html third
    /// view-mode `▦ Cards · ☰ Read · ✎ Edit`.</summary>
    public bool IsRulesViewRead => RulesViewMode == "read";

    /// <summary>True when Edit (text-mode) view is active.</summary>
    public bool IsRulesViewEdit => RulesViewMode == "edit";

    [RelayCommand]
    private void SetRulesViewCards() => RulesViewMode = "cards";

    [RelayCommand]
    private void SetRulesViewRead()
    {
        RebuildReadModeGroups();
        RulesViewMode = "read";
    }

    [RelayCommand]
    private void SetRulesViewEdit()
    {
        // Snapshot current canonical text into edit buffer + recompute
        // diagnostics + line-number gutter.
        EditedCustomRulesText = CustomRulesText;
        RulesViewMode = "edit";
        RecomputeRulesEditorState();
    }

    // v2.30.0-r12 — Read view-mode grouped collections.
    // Three filtered ObservableCollections drive the read-only view's
    // 3-section layout (direct / proxy / block). Each section shows its
    // header ("— direct (N) —") only when at least one rule of that
    // action exists.
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeDirectRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeProxyRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeBlockRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();

    [ObservableProperty] private string _readModeDirectHeader = string.Empty;
    [ObservableProperty] private string _readModeProxyHeader  = string.Empty;
    [ObservableProperty] private string _readModeBlockHeader  = string.Empty;

    /// <summary>v2.30.0-r12 — rebuild the three Read-mode groups from
    /// CustomRulesList. Called on view-mode flip + on every CustomRulesList
    /// change (via RebuildCustomRulesList → RebuildFilteredCustomRulesList
    /// chain that already runs after add/delete/toggle/import/etc).</summary>
    private void RebuildReadModeGroups()
    {
        ReadModeDirectRules.Clear();
        ReadModeProxyRules.Clear();
        ReadModeBlockRules.Clear();

        foreach (var vm in CustomRulesList)
        {
            switch (vm.Action)
            {
                case "direct": ReadModeDirectRules.Add(vm); break;
                case "proxy":  ReadModeProxyRules.Add(vm);  break;
                case "block":  ReadModeBlockRules.Add(vm);  break;
            }
        }

        ReadModeDirectHeader = $"— direct ({ReadModeDirectRules.Count}) —";
        ReadModeProxyHeader  = $"— proxy ({ReadModeProxyRules.Count}) —";
        ReadModeBlockHeader  = $"— block ({ReadModeBlockRules.Count}) —";
    }

    /// <summary>Working buffer for the Edit-mode textarea. Decoupled from
    /// CustomRulesText so intermediate states don't trigger SaveSettings
    /// or CustomRulesList rebuilds. Apply commits, Revert rolls back.</summary>
    [ObservableProperty]
    private string _editedCustomRulesText = string.Empty;

    partial void OnEditedCustomRulesTextChanged(string value) => RecomputeRulesEditorState();

    /// <summary>Multi-line string of line numbers for the gutter.
    /// Bound to a TextBlock with same font + line-height as the textbox
    /// so 1:1 line correspondence is preserved (text wrapping disabled
    /// in Edit mode for this reason).</summary>
    [ObservableProperty] private string _rulesEditorLineNumbers = "1";

    /// <summary>Status strip text: "N rules active · M errors".</summary>
    [ObservableProperty] private string _rulesEditorStatusText = string.Empty;

    /// <summary>First 4 errors as a multi-line string for the red callout
    /// below the editor: "line N: msg". Empty when there are no errors.</summary>
    [ObservableProperty] private string _rulesEditorErrorListText = string.Empty;

    /// <summary>True when the buffer has at least one parse error. Apply
    /// is disabled while this is true (button greyed in XAML).</summary>
    [ObservableProperty] private bool _rulesEditorHasErrors;

    /// <summary>Active rule count (excludes commented + empty + errored
    /// lines). Drives the Apply button label "Apply (N)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulesEditorApplyText))]
    private int _rulesEditorActiveCount;

    /// <summary>Buffer differs from canonical → user has uncommitted
    /// edits. Drives the "● unsaved changes" indicator.</summary>
    public bool RulesEditorIsDirty =>
        !string.Equals(EditedCustomRulesText ?? string.Empty,
                       CustomRulesText ?? string.Empty,
                       System.StringComparison.Ordinal);

    /// <summary>Apply button label: "Apply (N)" / "Применить (N)".</summary>
    public string RulesEditorApplyText => IsRussian
        ? $"Применить ({RulesEditorActiveCount})"
        : $"Apply ({RulesEditorActiveCount})";

    /// <summary>v2.30.0-r7 — recompute everything the Edit-mode UI binds:
    /// line numbers (one per logical line), status strip, error list,
    /// active count, has-errors flag, dirty flag.
    ///
    /// Validation grammar mirrors <see cref="CustomRulesParser"/> at a
    /// surface level: action ∈ {direct, proxy, block}, type ∈ known set,
    /// value present. Per-line; comments (lines starting with # or !)
    /// are skipped without contributing to active count or errors.
    ///
    /// Note: this is a LIGHT pre-validator for fast UI feedback. The
    /// authoritative parser still runs in <see cref="CustomRulesParser"/>
    /// during Apply / SaveSettings; it can produce additional warnings
    /// (e.g. catch-all rule conflicts) that the editor doesn't preview.</summary>
    private void RecomputeRulesEditorState()
    {
        var text = EditedCustomRulesText ?? string.Empty;
        // Avalonia normalises CRLF to LF in TextBox; split on \n is fine
        // for both \n-only and CRLF inputs.
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var validActions = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "direct", "proxy", "block"
        };
        var validTypes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "domain", "domain_suffix", "domain_keyword", "domain_regex",
            "ip_cidr", "port", "port_range", "network",
            "process_name", "process_path", "geosite", "geoip"
        };

        int active = 0;
        var errors = new System.Collections.Generic.List<(int Line, string Msg)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var ln = raw.Trim();
            if (string.IsNullOrEmpty(ln)) continue;
            // Comment / disabled line — skip without erroring.
            if (ln.StartsWith("#", System.StringComparison.Ordinal) ||
                ln.StartsWith("!", System.StringComparison.Ordinal)) continue;

            // Strip trailing inline comment "# ..."
            var hashIdx = ln.IndexOf('#');
            if (hashIdx >= 0) ln = ln.Substring(0, hashIdx).Trim();
            if (string.IsNullOrWhiteSpace(ln)) continue;

            var tokens = ln.Split(new[] { ' ', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);

            var firstTok = tokens.Length > 0 ? tokens[0] : string.Empty;
            if (!validActions.Contains(firstTok))
            {
                errors.Add((i + 1, IsRussian
                    ? $"неизвестный action «{firstTok}»"
                    : $"unknown action «{firstTok}»"));
                continue;
            }
            var secondTok = tokens.Length > 1 ? tokens[1] : string.Empty;
            if (!validTypes.Contains(secondTok))
            {
                errors.Add((i + 1, Strings.RuleParserUnknownType(secondTok)));
                continue;
            }
            if (tokens.Length < 3)
            {
                errors.Add((i + 1, Strings.RuleParserMissingValue));
                continue;
            }
            active++;
        }

        RulesEditorActiveCount = active;
        RulesEditorHasErrors = errors.Count > 0;

        // Line-number gutter: one number per source line.
        var sbNums = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sbNums.Append('\n');
            sbNums.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        RulesEditorLineNumbers = sbNums.ToString();

        // Status strip — "N rules active · M errors"
        var status = IsRussian
            ? $"{active} {(active == 1 ? "правило" : "правил")} активно"
            : $"{active} rule{(active == 1 ? "" : "s")} active";
        if (errors.Count > 0)
        {
            status += IsRussian
                ? $"  ·  {errors.Count} {(errors.Count == 1 ? "ошибка" : "ошибок")}"
                : $"  ·  {errors.Count} error{(errors.Count == 1 ? "" : "s")}";
        }
        RulesEditorStatusText = status;

        // Error list — first 4 errors with "line N: msg"
        if (errors.Count == 0)
        {
            RulesEditorErrorListText = string.Empty;
        }
        else
        {
            var head = new System.Text.StringBuilder();
            int take = System.Math.Min(4, errors.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0) head.Append('\n');
                var e = errors[i];
                head.Append(IsRussian
                    ? $"строка {e.Line}: {e.Msg}"
                    : $"line {e.Line}: {e.Msg}");
            }
            if (errors.Count > take)
            {
                head.Append('\n');
                head.Append(IsRussian
                    ? $"и ещё {errors.Count - take}…"
                    : $"and {errors.Count - take} more…");
            }
            RulesEditorErrorListText = head.ToString();
        }

        OnPropertyChanged(nameof(RulesEditorIsDirty));
        OnPropertyChanged(nameof(RulesEditorApplyText));
    }

    /// <summary>v2.30.0-r7 — commit the Edit-mode buffer to the canonical
    /// CustomRulesText. The setter triggers OnCustomRulesTextChanged →
    /// SaveSettings + RebuildCustomRulesList. Disabled while there are
    /// parse errors (button greyed in XAML).</summary>
    [RelayCommand]
    private void ApplyEditedRules()
    {
        if (RulesEditorHasErrors) return;
        CustomRulesText = EditedCustomRulesText ?? string.Empty;
        // OnCustomRulesTextChanged fires RulesEditorIsDirty notification —
        // dirty becomes false because both buffers now match.
        RecomputeRulesEditorState();
    }

    /// <summary>v2.30.0-r7 — discard buffer changes, restore to canonical
    /// CustomRulesText snapshot.</summary>
    [RelayCommand]
    private void RevertEditedRules()
    {
        EditedCustomRulesText = CustomRulesText ?? string.Empty;
        RecomputeRulesEditorState();
    }

    /// <summary>v2.30.0-r7 — sticky-dismiss for the Rules help banner.
    /// Bound to the dismiss X button. Persists in-session only (banner
    /// reappears on app restart — settings persistence is overkill for
    /// a one-line dismissable bullet block).</summary>
    [ObservableProperty] private bool _isRulesHelpBannerDismissed;

    [RelayCommand]
    private void DismissRulesHelpBanner() => IsRulesHelpBannerDismissed = true;

    /// <summary>v2.30.0-r2 — build CustomRulesList from
    /// _settings.App.CustomRules. Called on settings load + after
    /// textbox edits + after structured-row edits. The
    /// _isSyncingCustomRules guard prevents feedback when this method
    /// itself triggers OnCustomRulesTextChanged via SaveSettings.
    /// v2.30.0-r4: also rebuilds FilteredCustomRulesList + count text.</summary>
    private void RebuildCustomRulesList()
    {
        if (_isSyncingCustomRules) return;
        _isSyncingCustomRules = true;
        try
        {
            CustomRulesList.Clear();
            foreach (var rule in _settings.App.CustomRules)
            {
                CustomRulesList.Add(new CustomRuleViewModel(
                    rule,
                    onChanged: OnCustomRuleRowChanged,
                    onRemoveRequested: OnCustomRuleRowRemoveRequested));
            }
        }
        finally { _isSyncingCustomRules = false; }
        RebuildFilteredCustomRulesList();
    }

    /// <summary>v2.30.0-r4 → r18: bulk action: request clear-all
    /// confirmation. Sets <see cref="ClearAllConfirmPending"/> = true
    /// which surfaces the inline confirm bar above the list with
    /// explicit Cancel + Delete buttons. The actual destructive action
    /// runs in <see cref="ConfirmClearAllCustomRules"/>.
    ///
    /// <para>r18 user report: «Кнопка очистить все перестала работать,
    /// видимо из-за того что после клика окошко закрывается а там
    /// нужен дабл-клик». The pre-r18 two-click 5-s pattern broke when
    /// the popover closed on first click — user couldn't make the
    /// second click. r18 swaps to a non-popover confirm bar that
    /// stays visible until the user explicitly Confirms or Cancels
    /// (no time-based auto-dismiss).</para></summary>
    [RelayCommand]
    private void ClearAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        ClearAllConfirmPending = true;
        ClearAllConfirmText = IsRussian
            ? $"Удалить все правила ({CustomRulesList.Count})?"
            : $"Delete all rules ({CustomRulesList.Count})?";
    }

    /// <summary>v2.30.0-r18 — actually clear after the user clicks the
    /// confirm bar's Delete button.</summary>
    [RelayCommand]
    private void ConfirmClearAllCustomRules()
    {
        if (CustomRulesList.Count == 0)
        {
            ClearAllConfirmPending = false;
            ClearAllConfirmText = string.Empty;
            return;
        }
        CustomRulesList.Clear();
        FilteredCustomRulesList.Clear();
        FlushCustomRulesListToSettings();
        ClearAllConfirmPending = false;
        ClearAllConfirmText = string.Empty;
        ShowRulesToast(Strings.RulesAllDeleted);
    }

    /// <summary>v2.30.0-r18 — dismiss the confirm bar without deleting.</summary>
    [RelayCommand]
    private void CancelClearAllCustomRules()
    {
        ClearAllConfirmPending = false;
        ClearAllConfirmText = string.Empty;
    }

    /// <summary>True while the inline confirm bar is shown (between
    /// the popover-Click and the Delete/Cancel button click).</summary>
    [ObservableProperty] private bool _clearAllConfirmPending;
    [ObservableProperty] private string _clearAllConfirmText = string.Empty;

    /// <summary>v2.30.0-r4 — bulk enable all rules.</summary>
    [RelayCommand]
    private void EnableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = true;
        // FlushCustomRulesListToSettings fires per-row via OnCustomRuleRowChanged;
        // batch by setting _isSyncingCustomRules briefly... actually toggle
        // the property normally — feedback loop is fine because
        // _isSyncingCustomRules covers the row→settings sync.
    }

    /// <summary>v2.30.0-r4 — bulk disable all rules.</summary>
    [RelayCommand]
    private void DisableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = false;
    }

    /// <summary>v2.30.0-r14/r17 — bulk-pop "Sort by type" action.
    /// Stable-sorts CustomRulesList by Type alphabetically.
    /// r17 fix: user report «сортировка непонятно работает». Two changes:
    /// 1. Compare ALL items pre/post; if order is unchanged after sort,
    ///    show a "уже отсортировано" toast instead of silently re-shuffling.
    /// 2. Show a "✓ Sorted: N rules" toast for ~2 s on success so the
    ///    user gets visible feedback that the action ran.</summary>
    [RelayCommand]
    private void SortCustomRulesByType()
    {
        if (CustomRulesList.Count <= 1)
        {
            ShowRulesToast(IsRussian
                ? "Нечего сортировать"
                : "Nothing to sort");
            return;
        }

        var preOrder = CustomRulesList
            .Select(r => $"{r.Type}|{r.Action}|{r.Value}")
            .ToList();

        var sorted = CustomRulesList
            .OrderBy(r => r.Type, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Action, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var postOrder = sorted.Select(r => $"{r.Type}|{r.Action}|{r.Value}").ToList();
        bool changed = !preOrder.SequenceEqual(postOrder);

        _isSyncingCustomRules = true;
        try
        {
            CustomRulesList.Clear();
            foreach (var r in sorted) CustomRulesList.Add(r);
        }
        finally { _isSyncingCustomRules = false; }
        FlushCustomRulesListToSettings();
        RebuildFilteredCustomRulesList();

        ShowRulesToast(changed
            ? (IsRussian ? $"✓ Отсортировано по типу ({sorted.Count})"
                         : $"✓ Sorted by type ({sorted.Count})")
            : Strings.RulesAlreadySorted);
    }

    /// <summary>v2.30.0-r17 — transient toast string shown above the
    /// rule list for ~2 s after a bulk action (sort, etc.). Empty
    /// string = no toast.</summary>
    [ObservableProperty] private string _rulesToastText = string.Empty;

    private System.Threading.CancellationTokenSource? _rulesToastCts;

    private void ShowRulesToast(string text)
    {
        RulesToastText = text;
        // v2.31.0-r3 (VM-10): swap+dispose pattern — cancelling without
        // disposing leaked one CancellationTokenSource per toast. Cumulative
        // when toasts flicker (e.g. user mass-toggles rules on Network page).
        var oldCts = _rulesToastCts;
        _rulesToastCts = new System.Threading.CancellationTokenSource();
        var token = _rulesToastCts.Token;
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
        _ = System.Threading.Tasks.Task.Delay(RulesToastDurationMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested) RulesToastText = string.Empty;
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>v2.30.0-r2 — re-emit settings + textbox sync after
    /// a structured-row property change (Action / Type / Value / Comment
    /// / Enabled). Avoids RebuildCustomRulesList loop because the row
    /// VM was already mutated in place; we just flush to settings +
    /// regenerate the textbox view.</summary>
    private void OnCustomRuleRowChanged(CustomRuleViewModel _)
    {
        if (_isSyncingCustomRules || _isLoadingUI) return;
        FlushCustomRulesListToSettings();
    }

    /// <summary>v2.30.0-r2 — handle row's Remove button. r4: also drop
    /// from FilteredCustomRulesList so the visible list stays in sync.</summary>
    private void OnCustomRuleRowRemoveRequested(CustomRuleViewModel row)
    {
        if (_isLoadingUI) return;
        CustomRulesList.Remove(row);
        FilteredCustomRulesList.Remove(row);
        OnPropertyChanged(nameof(CustomRulesCountText));
        FlushCustomRulesListToSettings();
    }

    /// <summary>v2.30.0-r2 — flush the in-memory CustomRulesList rows
    /// to _settings.App.CustomRules + regenerate the CustomRulesText
    /// textbox content so both views stay in sync. Triggered by
    /// add / remove / property change on rows.
    /// v2.30.0-r4: also rebuilds FilteredCustomRulesList + count text
    /// (reapplies search filter to whatever's now in CustomRulesList).</summary>
    private void FlushCustomRulesListToSettings()
    {
        if (_isSyncingCustomRules) return;
        _isSyncingCustomRules = true;
        try
        {
            _settings.App.CustomRules = CustomRulesList.Select(vm => vm.ToModel()).ToList();
            CustomRulesText = VPNRouter.Core.Services.CustomRulesParser
                .SerializeToText(_settings.App.CustomRules);
            // Conflict detection re-runs on the serialized text via
            // the next OnCustomRulesTextChanged path — but we suppressed
            // that, so explicitly recompute here.
            var conflicts = VPNRouter.Core.Services.CustomRulesParser
                .DetectConflicts(_settings.App.CustomRules);
            CustomRulesConflictText = conflicts.Count == 0
                ? string.Empty
                : string.Join("\n", conflicts);
            CustomRulesErrorText = string.Empty;
        }
        finally { _isSyncingCustomRules = false; }
        RebuildFilteredCustomRulesList();
        SaveSettings();
        // v2.30.0-r17: rules-change-while-running surface. User report
        // «мне нужно делать полный перезапуск VPN чтоб правило сработало,
        // тут не очень понятно». While the VPN is running, mark the
        // change as pending so the Apply button + indicator surface
        // (existing pattern from other settings).
        if (IsConnected) HasPendingAppChanges = true;
    }

    /// <summary>v2.30.0-r3 — import rules from a CSV / JSON / sing-box-
    /// native file. Auto-detects format by content sniff. Appends to
    /// the existing list (preserves user's current rules). Surfaces
    /// import warnings in NewRuleValidationError.</summary>
    [RelayCommand]
    private async Task ImportCustomRulesAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                NewRuleValidationError = Strings.RulesFilePickerOpenFailed;
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Strings.RulesImportDialogTitle,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Rule files (CSV, JSON)")
                    {
                        Patterns = new[] { "*.csv", "*.json", "*.txt" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files")
                    {
                        Patterns = new[] { "*.*" },
                    },
                }
            });
            if (files.Count == 0) return;

            var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var text = await File.ReadAllTextAsync(path);
            var result = VPNRouter.Core.Services.CustomRulesImportExport.ImportFromText(text);

            if (result.Rules.Count == 0)
            {
                NewRuleValidationError = result.Warnings.Count > 0
                    ? Strings.RulesImportFailed(result.Warnings[0])
                    : Strings.RulesImportNoRules;
                return;
            }

            // Append imported rules to the live list (preserve existing).
            foreach (var rule in result.Rules)
            {
                CustomRulesList.Add(new CustomRuleViewModel(
                    rule,
                    onChanged: OnCustomRuleRowChanged,
                    onRemoveRequested: OnCustomRuleRowRemoveRequested));
            }
            FlushCustomRulesListToSettings();

            // Show success summary in the validation slot.
            var msg = Strings.RulesImported(result.Rules.Count, result.DetectedFormat.ToString());
            if (result.Warnings.Count > 0)
                msg += Strings.RulesImportWithWarnings(result.Warnings.Count);
            NewRuleValidationError = msg;
            foreach (var w in result.Warnings)
                _logger.Information("[CustomRules import] {Warning}", w);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ImportCustomRules failed");
            NewRuleValidationError = Strings.RulesImportError(ex.Message);
        }
    }

    /// <summary>v2.30.0-r3 — export current rules to a file. User picks
    /// destination path; format determined by file extension (.csv = CSV,
    /// .singbox.json = sing-box-native, anything else = our native JSON).
    /// Disabled rules are still exported (with enabled=false) so the
    /// user can round-trip a backup.</summary>
    [RelayCommand]
    private async Task ExportCustomRulesAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                NewRuleValidationError = Strings.RulesFilePickerOpenFailed;
                return;
            }

            if (CustomRulesList.Count == 0)
            {
                NewRuleValidationError = Strings.RulesExportNothing;
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = Strings.RulesExportDialogTitle,
                SuggestedFileName = $"vpnrouter-rules-{DateTime.Now:yyyyMMdd}",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("VPNRouter JSON (native)")
                    {
                        Patterns = new[] { "*.json" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("CSV (spreadsheet-friendly)")
                    {
                        Patterns = new[] { "*.csv" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("sing-box JSON (NekoBox / Hiddify compat)")
                    {
                        Patterns = new[] { "*.singbox.json" },
                    },
                }
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            // Decide format from extension.
            var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson;
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv;
            else if (path.EndsWith(".singbox.json", StringComparison.OrdinalIgnoreCase))
                fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson;

            var rules = CustomRulesList.Select(vm => vm.ToModel()).ToList();
            var content = VPNRouter.Core.Services.CustomRulesImportExport.ExportToText(rules, fmt);
            await File.WriteAllTextAsync(path, content);

            NewRuleValidationError = Strings.RulesExported(rules.Count, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ExportCustomRules failed");
            NewRuleValidationError = Strings.RulesExportError(ex.Message);
        }
    }

    /// <summary>v2.30.0-r2 — Add-form submit. Validates the new rule
    /// via the parser (one-line text), prepends to the list, clears
    /// the form. Validation errors surface in NewRuleValidationError.</summary>
    [RelayCommand]
    private void AddCustomRuleFromForm()
    {
        if (string.IsNullOrWhiteSpace(NewRuleValue))
        {
            NewRuleValidationError = Strings.RulesEmptyValue;
            return;
        }
        // v2.30.7 — also gate on the live type-regex validator that
        // colours the Value border red. Pre-r1 the parser was more
        // permissive than the live regex (e.g. "53" with type
        // "domain_suffix" passed parser but failed live regex), so a
        // user could submit with a red border and an invalid rule
        // would land in the YAML. Now we honor IsValid first.
        if (!NewRuleValueIsValid)
        {
            NewRuleValidationError = IsRussian
                ? $"Значение не подходит к типу «{NewRuleType}»"
                : $"Value doesn't match type \"{NewRuleType}\"";
            return;
        }
        // Assemble a single-line rule and run it through the parser
        // so all the type-specific validation we already wrote (CIDR,
        // port range, geosite name format) gets re-used here.
        var commentSuffix = string.IsNullOrWhiteSpace(NewRuleComment)
            ? string.Empty
            : $"  # {NewRuleComment.Trim()}";
        var line = $"{NewRuleAction} {NewRuleType} {NewRuleValue.Trim()}{commentSuffix}";
        var parsed = VPNRouter.Core.Services.CustomRulesParser.ParseFromText(line);
        if (parsed.Errors.Count > 0)
        {
            NewRuleValidationError = parsed.Errors[0].Reason;
            return;
        }
        if (parsed.Rules.Count == 0)
        {
            NewRuleValidationError = "Failed to parse";
            return;
        }
        // Append to list. New rules go to the END (lowest priority by
        // default — user can reorder later via move-up/down in v2.31).
        CustomRulesList.Add(new CustomRuleViewModel(
            parsed.Rules[0],
            onChanged: OnCustomRuleRowChanged,
            onRemoveRequested: OnCustomRuleRowRemoveRequested));
        // Clear form.
        NewRuleValue = string.Empty;
        NewRuleComment = string.Empty;
        NewRuleValidationError = string.Empty;
        FlushCustomRulesListToSettings();
    }

}
