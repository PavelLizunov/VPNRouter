using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
// v2.31.6-r8: removed duplicate `using VPNRouter.Core.Platform;` (was line 21
// and line 18 — compiler tolerates but flagged in the iter#4 audit).
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly VpnEngine _engine;
    // v2.31.6-r12 (Phase H, iter#4 audit): Dispose-state guard.
    // Pre-r12 the VM had no IDisposable surface — _runtimeStatusTimer
    // (DispatcherTimer) and _subRefreshTimer (System.Threading.Timer)
    // only stopped on the explicit Quit() / OnEngineStatus("Stopped")
    // happy paths. On unhandled exit / X-button close / a future
    // ReloadMainWindowForLocalization-style window rebuild, both timers
    // would leak; FreeConfigsVm.Dispose() (which it implements) was
    // never called either. r12 adds Dispose() that unhooks engine
    // events, stops + disposes both timers, and disposes FreeConfigsVm.
    // MainWindow.Closed should call this; until then it's still wired
    // to Quit() so explicit-quit paths benefit immediately.
    private bool _disposed;
#if PLATFORM_WINDOWS
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;
#endif
    private readonly ILogger _logger;
    private AppSettings _settings;
    private bool _isLoadingUI;
    private bool _appsLoaded;
    private System.Threading.Timer? _subRefreshTimer;
    private CancellationTokenSource? _subRefreshCts;
    private const int SubRefreshIntervalMs = 3600_000; // 1 hour

    /// <summary>
    /// Timestamp of the last UI-confirmed successful connect. Used by
    /// <see cref="SyncConnectedWithVpnRuntime"/> to suppress false demotes
    /// immediately after connect — on macOS the process enumeration used
    /// by <see cref="RuntimeStatusDetector.IsVpnRunning"/> occasionally
    /// returns false for the first 1–2 poll ticks after sing-box starts
    /// (sudo launch handoff), which was flipping IsConnected back to false.
    /// DateTime.MinValue = no recent connect.
    /// </summary>
    private DateTime _lastSuccessfulConnectAt = DateTime.MinValue;

    // ── Observable state ──

    [ObservableProperty] private string _statusText = Strings.NotConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonBrush))]
    [NotifyPropertyChangedFor(nameof(SmpActiveServerLine))]
    [NotifyPropertyChangedFor(nameof(SmpHeroTitle))]
    // v2.18.0 compact-design additions — status card / CTA / mini-badge
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusDescription))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaText))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnected))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsDisconnected))]
    private bool _isConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsWarn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusDescription))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaText))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnecting))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnected))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsDisconnected))]
    private bool _isConnecting;
    [ObservableProperty] private string _connectButtonText = Strings.StartVPN;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogoSource))]
    private bool _isDarkTheme;

    // v2.20.3: single transparent-background mascot (penguin_mascot.png,
    // 640×640, black lineart on alpha). Previous b_icon/w_icon pair had
    // SOLID backgrounds (not transparent) and I had them swapped to boot —
    // on light theme we were showing the black-rectangle variant, on dark
    // the white-rectangle one, both as visible rectangles inside the
    // accent-subtle container. User provided the clean transparent
    // version; we use it directly for light theme and RGB-invert it for
    // dark theme so the black lineart becomes white. Alpha channel is
    // preserved through the invert so edges stay anti-aliased.
    private static readonly Bitmap _logoLight = LoadAsset("avares://VPNRouter.App/Assets/penguin_mascot.png");
    private static readonly Bitmap _logoDark  = TryBuildInvertedLogo(_logoLight) ?? _logoLight;
    /// <summary>
    /// Header mascot. Light theme uses the source image as-is (black
    /// lineart on transparent). Dark theme uses an RGB-inverted copy
    /// (white lineart on transparent) so it remains visible against the
    /// dark subheader background.
    /// </summary>
    public Bitmap LogoSource => IsDarkTheme ? _logoDark : _logoLight;
    private static Bitmap LoadAsset(string uri) => new(AssetLoader.Open(new System.Uri(uri)));

    /// <summary>
    /// Produce an RGB-inverted copy that preserves alpha. Uses
    /// WriteableBitmap in Bgra8888/Unpremul so inverting the RGB channels
    /// doesn't interact with premultiplied-alpha edges (no fringing).
    /// Returns null on any failure — caller falls back to the original
    /// bitmap, which just renders invisibly on dark theme but at least
    /// doesn't crash the window.
    /// </summary>
    private static Bitmap? TryBuildInvertedLogo(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            var wb = new Avalonia.Media.Imaging.WriteableBitmap(
                size,
                source.Dpi,
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Unpremul);

            using (var fb = wb.Lock())
            {
                int byteCount = fb.RowBytes * size.Height;
                source.CopyPixels(new Avalonia.PixelRect(size), fb.Address, byteCount, fb.RowBytes);

                var bytes = new byte[byteCount];
                System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, byteCount);

                // BGRA: invert B, G, R; keep A. Source may be indexed-palette
                // PNG — CopyPixels normalises to Bgra8888 regardless.
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    bytes[i]     = (byte)(255 - bytes[i]);
                    bytes[i + 1] = (byte)(255 - bytes[i + 1]);
                    bytes[i + 2] = (byte)(255 - bytes[i + 2]);
                }

                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, fb.Address, byteCount);
            }

            return wb;
        }
        catch
        {
            return null;
        }
    }
    [ObservableProperty] private string _themeToggleText = Strings.ThemeDark;
    [ObservableProperty] private bool _isRussian;

    /// <summary>
    /// v2.29.0+ Layer 7 (UI surface for update receipt warning).
    /// Populated at app startup from
    /// <see cref="VPNRouter.Core.Services.UpdateChecker.CheckInstallReceipt"/>.
    /// Non-empty value surfaces a dismissible banner at the top of
    /// MainWindow. Empty when the previous update landed correctly OR
    /// no update was attempted recently.
    ///
    /// <para>Catches the failure mode where the auto-update flow
    /// completes (download + apply + restart) but the running binary
    /// is not actually newer than before. Pre-r7 was logged via Serilog
    /// only; users who don't tail the log never saw it.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateWarning))]
    private string _updateWarningText = string.Empty;

    public bool HasUpdateWarning => !string.IsNullOrWhiteSpace(UpdateWarningText);

    [RelayCommand]
    private void DismissUpdateWarning() => UpdateWarningText = string.Empty;

    /// <summary>
    /// True when the window should render the one-page SimplePage instead of
    /// the full tabbed Advanced layout. Persisted via AppSettings.App.UiMode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiModeToggleText))]
    [NotifyPropertyChangedFor(nameof(UiModeToggleTooltip))]
    private bool _isSimpleMode;

    public string UiModeToggleText   => IsSimpleMode ? Strings.SmpToggleToAdvanced : Strings.SmpToggleToSimple;
    public string UiModeToggleTooltip => Strings.SmpToggleTooltip;

    // v2.21.0: Linux-specific flags for UI. Zapret (winws.exe) and TgProxy
    // (Python embeddable) are Windows-only; their sub-sections of the Tools
    // tab + related buttons are hidden on Linux. Expose both IsLinux and
    // IsWindows so XAML can bind IsVisible without a converter.
    public bool IsLinuxPlatform   => OperatingSystem.IsLinux();
    public bool IsWindowsPlatform => OperatingSystem.IsWindows();
    /// <summary>True when Zapret DPI bypass is available on the current OS (Windows only).</summary>
    public bool IsZapretAvailable => OperatingSystem.IsWindows();
    /// <summary>True when bundled Telegram proxy is available on the current OS (Windows only).</summary>
    public bool IsTgProxyAvailable => OperatingSystem.IsWindows();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isVlessMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isSubscribeMode = false;

    /// <summary>True when the server ListBox should be visible (Manual or Subscribe mode).</summary>
    public bool IsServerListMode => IsVlessMode || IsSubscribeMode;

    // v2.31.6-r9 — removed `_configModeIndex` ObservableProperty +
    // `ConfigModeItems` getter + `OnConfigModeIndexChanged` no-op partial.
    // The ComboBox they backed was dropped from the UI in v2.5.0 and the
    // empty handler had been parked as a no-op safety since. No XAML
    // bindings left, no callers — iter#4 audit confirmed unused.

    // Sync mode flags when tab changes. Saves on tab switch so Connect
    // always uses the mode matching the visible tab.
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_isLoadingUI || _isReconnecting) return;
        // v2.30.2-r1 diag: log every tab transition so the next repro of
        // "Servers tab opened in wrong sub-state" is unambiguous.
        _logger?.Information(
            "[VM] OnSelectedTabIndexChanged tab={Tab} (was IsVlessMode={V}, IsSubscribeMode={S}, ServerModeIndex={I})",
            value, IsVlessMode, IsSubscribeMode, SelectedServerModeIndex);
        if (value == 0) // Manual tab
        {
            IsVlessMode = true;
            IsSubscribeMode = false;
            // v2.30.2-r1 Bug 1 fix: when navigating into the Servers tab,
            // the sub-tab visual selection must match what the page is
            // actually showing. If the user previously had ConfigMode=
            // "subscribe" → "custom" → "subscribe" (peeking) → switch to
            // Servers tab, the SelectedServerModeIndex could be stuck on
            // 1 (Custom) from the peek, while the page now wants to
            // show VLESS rows. Re-sync to the data-driven default.
            var hasManual = Servers.Count > 0;
            var hasCustom = CustomConfigs.Count > 0;
            var desiredSubTab = (hasManual || !hasCustom) ? 0 : 1;
            if (SelectedServerModeIndex != desiredSubTab)
            {
                _logger?.Information(
                    "[VM] OnSelectedTabIndexChanged: aligning sub-tab {From}->{To} (manual={M}, custom={C})",
                    SelectedServerModeIndex, desiredSubTab, Servers.Count, CustomConfigs.Count);
                SelectedServerModeIndex = desiredSubTab;
            }
        }
        else if (value == 1) // Subscribe tab
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
        }
        else if (value == 5) // FreeConfigs tab
        {
            // v2.20.1: lazy-load the FreeConfigs snapshot on first visit.
            // Users who never open this tab save ~6-7 MB of JSON
            // deserialization + retained list. Subsequent visits are no-ops.
            try { FreeConfigsVm?.EnsureCacheLoaded(); }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] FreeConfigs lazy-load failed");
            }
        }
        // Tab 2 (Network), Tab 3 (Applications), Tab 4 (Tools) — no action
    }
    [ObservableProperty] private string _subscriptionUrl = string.Empty;

    // Multiple subscriptions support (v2.12+)
    public ObservableCollection<SubscriptionViewModel> Subscriptions { get; } = new();
    [ObservableProperty] private string _newSubName = string.Empty;
    [ObservableProperty] private string _newSubUrl = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    [NotifyPropertyChangedFor(nameof(IsFullTunnel))]
    private bool _isSplitTunnel = true;

    /// <summary>
    /// v2.25.10 fix: exposes the inverse of <see cref="IsSplitTunnel"/> as a
    /// two-way bindable bool so the Full Tunnel RadioButton in Settings →
    /// Routing can drive IsSplitTunnel directly (setting IsFullTunnel=true
    /// sets IsSplitTunnel=false). Needed because RadioButton with
    /// <c>{Binding !IsSplitTunnel}</c> is one-way only and cannot flip the
    /// bool back on user click. Previously the Full RadioButton relied on
    /// GroupName exclusivity to uncheck Split — that worked inside one
    /// window but broke after ReloadMainWindowForLocalization briefly kept
    /// both the old and new window's RadioButtons alive with the same
    /// GroupName, letting the group manager cross-wire them. User symptom:
    /// "VPN seemed to flip to Full by itself after language toggle and
    /// Split would no longer apply".
    /// </summary>
    public bool IsFullTunnel
    {
        get => !IsSplitTunnel;
        set
        {
            if (IsSplitTunnel != !value)
                IsSplitTunnel = !value;
        }
    }
    [ObservableProperty] private bool _bypassRussianTraffic = true;

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
    ///
    /// <para>v2.29.0 only had direct rules; v2.30 adds proxy + block.
    /// CustomDirectRulesText kept as alias on first run after upgrade
    /// — read once during cache load, then SaveSettings persists to
    /// CustomRulesText.</para>
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
        "direct" => IsRussian ? "напрямую (мимо VPN)"     : "direct (bypass VPN)",
        "proxy"  => IsRussian ? "через VPN-туннель"       : "through the VPN tunnel",
        "block"  => IsRussian ? "блокировать соединение"  : "block the connection",
        _ => string.Empty,
    };

    /// <summary>Per-type guidance text shown under the Type ComboBox + as
    /// the default Value-hint. From RulesPage.html `typeMeta[type].hint`.
    /// v2.30.6-r1 (UX-13): every hint now embeds a concrete example so the
    /// raw sing-box term ("domain_suffix") makes immediate sense.</summary>
    public string NewRuleTypeHint => NewRuleType switch
    {
        "domain"         => IsRussian ? "точное имя (discord.com)"        : "exact match (discord.com)",
        "domain_suffix"  => IsRussian ? "оканчивается на (.discord.com)"  : "ends with (.discord.com)",
        "domain_keyword" => IsRussian ? "содержит (discord)"              : "contains (discord)",
        "ip_cidr"        => IsRussian ? "IPv4/IPv6 + маска (10.0.0.0/8)"  : "IPv4/IPv6 + mask (10.0.0.0/8)",
        "port"           => IsRussian ? "порт или список (53,853)"        : "port or list (53,853)",
        "port_range"     => IsRussian ? "диапазон портов (1000-2000)"     : "port range (1000-2000)",
        "network"        => IsRussian ? "tcp или udp"                     : "tcp or udp",
        "process_name"   => IsRussian ? "имя процесса (Discord.exe)"      : "process name (Discord.exe)",
        "process_path"   => IsRussian ? "полный путь к .exe"              : "full .exe path",
        "geosite"        => IsRussian ? "тег geosite (cn, ads, …)"        : "geosite tag (cn, ads, …)",
        "geoip"          => IsRussian ? "тег geoip (cn, us, private)"     : "geoip tag (cn, us, private)",
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

    // v2.31.6-r9 — removed CustomDirectRulesText / CustomDirectRulesErrorText
    // aliases (v2.29.0-r4 transitional shim for cached XAML bindings).
    // Iter#4 audit: no XAML reference remains anywhere; the only callers
    // were the VM's own self-OnPropertyChanged announcements at lines
    // 806-807 (also removed).
    [ObservableProperty] private bool _strictMode = false;
    [ObservableProperty] private bool _forceIpv4Only = true;
    [ObservableProperty] private bool _flushDnsOnStart = true;
    [ObservableProperty] private bool _strictDns = false;
    [ObservableProperty] private bool _blockAds = false;

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
    private bool _zapretEnabled = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomStrategy))]
    private int _zapretStrategyIndex = 0;
    public bool IsCustomStrategy => ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
        && ZapretStrategies[ZapretStrategyIndex] == "custom";
    [ObservableProperty] private string _zapretCustomArgs = string.Empty;
    [ObservableProperty] private string _zapretStatus = "Stopped";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblDiscordHosts))]
    private bool _discordHostsInstalled = false;
    [ObservableProperty] private string _zapretVersionText = "";
    [ObservableProperty] private bool _isZapretDownloading = false;

    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<string> _zapretStrategies = new();
    private List<VPNRouter.Core.Services.ZapretStrategy> _parsedStrategies = new();
    [ObservableProperty] private bool _receivePrereleases = false;

    // Telegram proxy
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblTgProxyToggle))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyMainAction))]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private bool _tgProxyEnabled = false;
    [ObservableProperty] private string _tgProxyStatus = "Stopped";
    [ObservableProperty] private int _tgProxyPort = 1443;
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
    [ObservableProperty] private string _tgProxyStats = "";

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

    /// <summary>v2.29.0 — auto-save when the user types in the Custom
    /// Direct Rules textbox. Throttled by Avalonia's TextBox change-on-
    /// commit (focus loss / Enter), so we don't spam SaveSettings on
    /// every keystroke. Errors during parse populate the inline error
    /// box but don't block save (valid lines persist).</summary>
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
        // v2.31.6-r9 — dropped the two legacy alias OnPropertyChanged
        // calls (`CustomDirectRulesText`, `CustomDirectRulesErrorText`)
        // along with the alias getters above. No remaining XAML refs.

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
                errors.Add((i + 1, IsRussian
                    ? $"неизвестный type «{secondTok}»"
                    : $"unknown type «{secondTok}»"));
                continue;
            }
            if (tokens.Length < 3)
            {
                errors.Add((i + 1, IsRussian ? "нет value" : "missing value"));
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
        ShowRulesToast(IsRussian ? "✓ Удалено все правила" : "✓ All rules deleted");
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
            : (IsRussian ? "Уже отсортировано" : "Already sorted"));
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
        _ = System.Threading.Tasks.Task.Delay(2000, token).ContinueWith(t =>
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
                NewRuleValidationError = "Could not open file picker";
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import rules",
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
                    ? "Import failed: " + result.Warnings[0]
                    : "Import: file contained no rules";
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
            var msg = $"Imported {result.Rules.Count} rule(s) [{result.DetectedFormat}]";
            if (result.Warnings.Count > 0)
                msg += $" — {result.Warnings.Count} warning(s) (see app log)";
            NewRuleValidationError = msg;
            foreach (var w in result.Warnings)
                _logger.Information("[CustomRules import] {Warning}", w);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ImportCustomRules failed");
            NewRuleValidationError = "Import error: " + ex.Message;
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
                NewRuleValidationError = "Could not open file picker";
                return;
            }

            if (CustomRulesList.Count == 0)
            {
                NewRuleValidationError = "Nothing to export — rule list is empty";
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export rules",
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

            NewRuleValidationError = $"Exported {rules.Count} rule(s) to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ExportCustomRules failed");
            NewRuleValidationError = "Export error: " + ex.Message;
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
            NewRuleValidationError = IsRussian ? "Пустое значение" : "Empty value";
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

    partial void OnAutostartUiChanged(bool value)
    {
        if (_isLoadingUI) return;
        // v2.29.0: AutostartHelper became cross-platform (Mac LaunchAgent +
        // Linux XDG autostart in addition to the existing Win HKCU\Run).
        // Old `#if PLATFORM_WINDOWS` guard removed — helper handles platform
        // dispatch internally, no-ops on unsupported OS (none currently).
        try
        {
            if (value)
                AutostartHelper.Enable(Environment.ProcessPath!);
            else
                AutostartHelper.Disable();
        }
        catch (Exception ex) { _logger.Error(ex, "[VM] Autostart UI toggle failed"); }
        SaveSettings();
    }

    // v2.27 Bug B: re-fire PropertyChanged for SmpAutostartChecked whenever
    // AutostartVpn flips — the Simple-mode checkbox is now a computed read
    // of (ServiceVm.IsInstalled && ServiceVm.IsRunning && AutostartVpn),
    // so any one of those changing must notify the binding. The matching
    // ServiceVm.IsInstalled/IsRunning listener is wired in the constructor.
    partial void OnAutostartVpnChanged(bool value)
    {
        if (!_isLoadingUI) SaveSettings();
        OnPropertyChanged(nameof(SmpAutostartChecked));
    }
    partial void OnAutostartZapretChanged(bool value) { if (!_isLoadingUI) SaveSettings(); }
    partial void OnAutostartTgProxyChanged(bool value)
    {
        if (_isLoadingUI) return;

        // v2.31.10-r5 — Generate secret on enable so the Service can
        // autostart tgproxy at boot. Without this, toggling the box
        // before ever clicking "Start" once left config.yaml's
        // tg_proxy_secret empty → Service logged "TgProxy secret not
        // configured, skipping" and silently returned → user saw
        // "Auto launch with Windows for tgproxy doesn't work" with no
        // UI feedback. Same RNG + format used by StartTgProxy below.
        if (value && string.IsNullOrWhiteSpace(TgProxySecret))
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
            TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        }

        SaveSettings();
    }

    // Zapret section navigator (master-detail).
    // v2.31.6-r5 (ZAPRET-2): consolidated 7 sections → 5 per user
    // feedback 2026-05-03 night («упростить ZAPRET страницу — где
    // можно»). Audit findings:
    //   • Diagnostics had ONE button («Run diagnostics») + an output
    //     panel → merged into Status (lives below the warning banner;
    //     diagnosing is the natural follow-up to seeing the status).
    //   • Updates had TWO elements («Update IPSet list» + «Auto-check
    //     Zapret updates» checkbox) → merged into Strategy where the
    //     existing «Update Zapret» button already handles version
    //     management, keeping all update-related controls together.
    // Design handoff cell 7 specifies 7 sections, so this is a
    // documented deviation per v2.31.6-r1/r3 lesson (Rule B4): walking
    // each section with mcp__vpnrouter-test__mouse_click revealed
    // 6 of 7 had ≤3 elements — the 7-section spread was over-architected
    // for the actual content. Surface area per section now averages
    // ~5 controls — denser without crowding.
    // The IsZapret*Section flags below are kept as 5 contiguous indices;
    // pre-r5 dead branches («IsZapretUpdatesSection», «IsZapretDiagnosticsSection»)
    // are removed so the XAML can't accidentally bind to unreachable
    // sections.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretStatusSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretStrategySection))]
    [NotifyPropertyChangedFor(nameof(IsZapretHostsSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretFiltersSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretAdvancedSection))]
    private int _selectedZapretSectionIndex;

    public bool IsZapretStatusSection => SelectedZapretSectionIndex == 0;
    public bool IsZapretStrategySection => SelectedZapretSectionIndex == 1;
    public bool IsZapretHostsSection => SelectedZapretSectionIndex == 2;
    public bool IsZapretFiltersSection => SelectedZapretSectionIndex == 3;
    public bool IsZapretAdvancedSection => SelectedZapretSectionIndex == 4;

    // Zapret tool state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblFlowsealHosts))]
    private bool _flowsealHostsInstalled;
    [ObservableProperty] private int _gameFilterModeIndex;
    [ObservableProperty] private int _ipSetModeIndex;
    [ObservableProperty] private bool _zapretAutoUpdateCheck;

    public string LblFlowsealHosts => IsRussian
        ? (FlowsealHostsInstalled ? "Убрать Flowseal hosts" : "Добавить Flowseal hosts")
        : (FlowsealHostsInstalled ? "Remove Flowseal hosts" : "Add Flowseal hosts");

    // Settings section navigator (master-detail)
    // v2.30.0-r2: added Rules as section index 1 (between Routing and
    // Leak Protection — natural sibling to Routing, since both deal
    // with traffic routing). Existing indexes shifted +1.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsRoutingSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsRulesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLeakSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsContentSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsUpdatesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAutostartSelected))]
    private int _selectedSettingsIndex;

    public bool IsSettingsRoutingSelected   => SelectedSettingsIndex == 0;
    public bool IsSettingsRulesSelected     => SelectedSettingsIndex == 1;
    public bool IsSettingsLeakSelected      => SelectedSettingsIndex == 2;
    public bool IsSettingsContentSelected   => SelectedSettingsIndex == 3;
    public bool IsSettingsUpdatesSelected   => SelectedSettingsIndex == 4;
    public bool IsSettingsAutostartSelected => SelectedSettingsIndex == 5;

    // Tools sub-tabs
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretToolSelected))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyToolSelected))]
    private int _selectedToolIndex;

    public bool IsZapretToolSelected => SelectedToolIndex == 0;
    public bool IsTgProxyToolSelected => SelectedToolIndex == 1;

    [ObservableProperty] private AppGroupViewModel? _selectedAppGroup;

    // Detail editor state — independent of SelectedServer (left click sets active, right click opens detail)
    [ObservableProperty] private ServerViewModel? _detailServer;
    [ObservableProperty] private CustomConfigViewModel? _detailCustomConfig;

    [RelayCommand]
    private void CloseServerDetail() => DetailServer = null;

    [RelayCommand]
    private void CloseCustomConfigDetail() => DetailCustomConfig = null;

    [RelayCommand]
    private void OpenServerDetail(ServerViewModel? server) => DetailServer = server;

    [RelayCommand]
    private void OpenCustomConfigDetail(CustomConfigViewModel? cfg) => DetailCustomConfig = cfg;

    // ── Version ──
    public string VersionText => $"by NiniTux  \u00b7  v{AppVersion.Version}  \u00b7  sing-box {GetSingBoxVersion()}";

    // v2.25.2 — short "v2.25.1-r2" string for the redesigned ⋯ menu About
    // row. Rendered as a muted mono pill on the right side of the item.
    // Kept separate from VersionText (which still carries by-line + sing-box
    // for the About dialog) — the menu only has room for the version tag.
    public string AppVersionShortText => $"v{AppVersion.Version}";

    private static string GetSingBoxVersion()
    {
        try
        {
            // v2.21.6: was hardcoded Windows %ProgramData% / macOS
            // ~/Library/Application Support path. Linux fell through to the
            // macOS branch and hit a non-existent path → subtitle showed
            // "sing-box ?" on Linux. AppPaths.SingBoxExePath already
            // resolves to the right location on all three platforms
            // (uses ~/.config/vpnrouter/bin/sing-box on Linux).
            var exePath = AppPaths.SingBoxExePath;
            if (!File.Exists(exePath)) return "?";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(3000);

            // Parse "sing-box version 1.13.7" or "sing-box version unknown"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("sing-box version", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring("sing-box version".Length).Trim();
            }
            return "?";
        }
        catch { return "?"; }
    }

    // ── Localized labels (proxies to Strings.cs, refreshed on language toggle) ──
    public string LblTabServers => Strings.TabServers;
    public string LblTabManual => Strings.TabServers;
    public string LblTabSubscribe => Strings.ModeSubscribe;
    public string LblTabApps => Strings.TabApps;
    public string LblTabNetwork => Strings.TabSettings;
    public string LblVlessServers => Strings.VlessServers;
    public string LblCustomConfigJson => Strings.CustomConfigJson;
    public string LblAddServers => Strings.AddServers;
    public string LblRemove => Strings.Remove;
    public string LblAddConfig => Strings.AddConfig;
    public string LblBtnAdd => Strings.BtnAdd;
    public string LblSplitTunnel => Strings.SplitTunnel;
    public string LblFullTunnel => Strings.FullTunnel;
    public string LblAppsHint => Strings.AppsHint;
    public string LblFieldName => Strings.FieldName;
    public string LblFieldServer => Strings.FieldServer;
    public string LblFieldPort => Strings.FieldPort;
    public string LblFieldUuid => Strings.FieldUuid;
    public string LblFieldPublicKey => Strings.FieldPublicKey;
    public string LblFieldShortId => Strings.FieldShortId;
    public string LblDoubleClickEditServer => Strings.DoubleClickEditServer;
    public string LblDoubleClickActiveConfig => Strings.DoubleClickActiveConfig;
    public string LblClickToActivateConfig => IsRussian ? "Нажмите на конфиг для активации" : "Click a config to activate it";
    public string LblSubscribeMode => Strings.SubscribeMode;
    public string LblSubscriptionUrlHint => Strings.SubscriptionUrlHint;
    public string LblSyncButton => Strings.SyncButton;
    public string LblAddCustomAppHint => Strings.AddCustomAppHint;
    public string LblTcpUdpHint => Strings.TcpUdpHint;
    public string BypassRuLabel => Strings.BypassRussianTrafficLabel;
    public string BypassRuHint => Strings.BypassRussianTrafficHint;
    public string CheckLeaksLabel => Strings.CheckLeaks;
    public string ShowLogsLabel => Strings.ShowLogs;
    public string StrictModeLabel => Strings.StrictModeLabel;
    public string StrictModeHint => Strings.StrictModeHint;
    public string ForceIpv4Label => Strings.ForceIpv4Label;
    public string FlushDnsLabel => Strings.FlushDnsLabel;
    public string StrictDnsLabel => Strings.StrictDnsLabel;
    public string BlockAdsLabel => IsRussian ? "Блокировать рекламу и трекеры" : "Block ads & trackers";
    public string BlockAdsHint => IsRussian
        ? "AdGuard DNS + adblock rule_set (~300K доменов)"
        : "AdGuard DNS + adblock rule_set (~300K domains)";

    // DPI Bypass labels
    public string LblTabTools => IsRussian ? "Инструменты" : "Tools";
    public string LblTabFreeConfigs => Strings.TabFreeConfigs;
    public string LblSettingsRouting => Strings.SectionRouting;
    // LblSettingsRules lives in MainWindowViewModel.Localization.cs (v2.30.0-r2).
    public string LblSettingsLeak => Strings.SectionLeakProtection;
    public string LblSettingsContent => Strings.SectionContent;
    public string LblSettingsUpdates => Strings.SectionUpdates;
    public string LblAutostartSection => Strings.AutostartSection;
    public string LblAutostartVpn => Strings.AutostartVpn;
    public string LblAutostartZapret => Strings.AutostartZapret;
    public string LblAutostartTgProxy => Strings.AutostartTgProxy;
    public string LblAutostartUi => Strings.AutostartUi;

    // v2.31.10 (autostart UX clarity): per-component status badge. Each
    // CheckBox in the Section A "Components to auto-start with the service"
    // block now shows a small label that names the actual delivery channel
    // (Windows Service at boot vs App-side login bootstrap vs nothing) so a
    // user can't tick a toggle that doesn't fire. Status is computed from
    // (ServiceVm.IsInstalled, HasAppBootstrap{Vpn,Zapret,TgProxy}); the
    // ServiceVm.PropertyChanged subscription in the constructor already
    // re-fires PropertyChanged for these labels on every IsInstalled flip.
    //
    // Currently HasAppBootstrap* return false for all three components —
    // the App.axaml.cs OnFrameworkInitializationCompleted path doesn't run
    // any of VpnEngine/ZapretManager/TgProxyManager at user login. The
    // sister DBG-2 task adds App-side bootstrap; flipping the corresponding
    // flag to true at that point switches affected components from the red
    // ⛔ "won't fire" badge to the amber ⚠ "fires after App login" badge.
    internal const bool HasAppBootstrapVpn = false;
    internal const bool HasAppBootstrapZapret = false;
    internal const bool HasAppBootstrapTgProxy = false;

    public string LblAutostartVpnStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapVpn);
    public string LblAutostartZapretStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapZapret);
    public string LblAutostartTgProxyStatus =>
        ComputeAutostartStatus(ServiceVm.IsInstalled, HasAppBootstrapTgProxy);

    public bool IsAutostartVpnStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartVpnStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapVpn;
    public bool IsAutostartVpnStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapVpn;

    public bool IsAutostartZapretStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartZapretStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapZapret;
    public bool IsAutostartZapretStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapZapret;

    public bool IsAutostartTgProxyStatusGood => ServiceVm.IsInstalled;
    public bool IsAutostartTgProxyStatusWarn => !ServiceVm.IsInstalled && HasAppBootstrapTgProxy;
    public bool IsAutostartTgProxyStatusBad => !ServiceVm.IsInstalled && !HasAppBootstrapTgProxy;

    /// <summary>
    /// Pure-function status dispatch — extracted as <c>internal static</c>
    /// so it can be unit-tested without instantiating MainWindowViewModel
    /// (which spins up file I/O, logger, etc.). Three branches matching
    /// the three badge states surfaced in the Autostart sub-tab.
    /// </summary>
    internal static string ComputeAutostartStatus(bool isServiceInstalled, bool hasAppBootstrap)
    {
        if (isServiceInstalled) return Strings.AutostartStatusBoot;
        return hasAppBootstrap
            ? Strings.AutostartStatusLoginFallback
            : Strings.AutostartStatusNoBoot;
    }
    public string LblServerModeVless => Strings.VlessServers;
    public string LblServerModeCustom => Strings.CustomConfigJson;
    public string LblToolZapret => Strings.TabZapret;
    public string LblToolTgProxy => Strings.TabTgWsProxy;
    public string LblDpiBypassTab => Strings.TabZapret;
    // v2.30.7 — UX-44 followup: the v2.30.5 fix dropped the "(zapret от
    // Flowseal)" parenthetical from RU only. EN side kept "(zapret by
    // Flowseal)". Symmetric drop here — Flowseal credit lives in the
    // GitHub link in the Advanced section.
    public string LblDpiDescription => IsRussian
        ? "Обход блокировок провайдера. Работает с Discord, YouTube, и другими заблокированными сервисами. Если стратегия не работает — пробуйте другую."
        : "Bypass ISP blocking. Works with Discord, YouTube, and other blocked services. If a strategy doesn't work — try another.";
    public string LblDpiStrategy => IsRussian ? "Стратегия" : "Strategy";
    public string LblUpdateZapret => IsRussian
        ? (VPNRouter.Core.Services.ZapretUpdater.IsInstalled() ? "Обновить" : "Скачать")
        : (VPNRouter.Core.Services.ZapretUpdater.IsInstalled() ? "Update" : "Download");
    public string LblDpiWarning => IsRussian
        ? "⚠ Только Windows. Можно использовать без VPN и вместе с VPN."
        : "⚠ Windows only. Can be used without VPN and alongside VPN.";
    public string LblDpiToggle => IsRussian
        ? (ZapretEnabled ? "Остановить обход DPI" : "Запустить обход DPI")
        : (ZapretEnabled ? "Stop DPI Bypass" : "Start DPI Bypass");
    public string LblDiscordHosts => IsRussian
        ? (DiscordHostsInstalled ? "Удалить Discord hosts" : "Добавить Discord hosts")
        : (DiscordHostsInstalled ? "Remove Discord hosts" : "Add Discord hosts");
    public string LblDiscordHostsDesc => IsRussian
        ? "Перенаправляет Discord voice серверы (finland*.discord.media) на рабочий Cloudflare IP. Фиксит голосовые каналы."
        : "Redirects Discord voice servers (finland*.discord.media) to working Cloudflare IP. Fixes voice channels.";
    public string ReceivePrereleasesLabel => IsRussian ? "Получать prerelease обновления (experimental канал)" : "Receive prereleases (experimental channel)";
    public string UpdateChannelHeader => IsRussian ? "Канал обновлений" : "Update channel";

    // Telegram proxy labels
    public string LblTabTelegram => Strings.TabTgWsProxy;
    public string LblTgProxyDescription => Strings.TgProxyDescription;
    public string LblTgProxySetupHint => Strings.TgProxySetupHint;
    public string LblTgProxyToggle => TgProxyEnabled ? Strings.TgProxyStop : Strings.TgProxyStart;

    /// <summary>
    /// v2.31.6-r5 (TG-2): label for the unified footer action introduced
    /// per user feedback 2026-05-03 night. When stopped, footer fires the
    /// full SetupTgProxy chain (download → start → open Telegram), so
    /// label reads «Запустить и открыть Telegram» / «Start &amp; open
    /// Telegram». When running, footer reverts to the existing «Stop»
    /// semantics. Bound to <see cref="TgProxyMainActionCommand"/>.
    /// </summary>
    public string LblTgProxyMainAction => TgProxyEnabled
        ? Strings.TgProxyStop
        : Strings.TgProxyStartAndOpen;

    // v2.31.6-r9 — purged 5 unused L_TgProxySetup* / L_TgProxyClientAutoHint
    // / L_TgProxyAdvanced getters added in v2.31.6-r1's two-state cascade
    // but orphaned by r3's design-aligned redo. Iter#4 audit confirmed no
    // XAML bindings. Only L_TgProxyReopenInTelegram is still used (body
    // «Reopen in Telegram» button).
    public string L_TgProxyReopenInTelegram => Strings.TgProxyReopenInTelegram;
    // v2.30.7-r4 — F-17 fix: button label "Обновить" / "Update" alone
    // is ambiguous — the page has multiple things that can be updated
    // (binary version, secret, port). Prefix with "TgProxy" so the
    // action is unambiguous: "Обновить TgProxy" / "Update TgProxy".
    public string LblUpdateTgProxy => IsRussian
        ? (TgProxyUpdater.IsInstalled() ? "Обновить TgProxy" : "Скачать TgProxy")
        : (TgProxyUpdater.IsInstalled() ? "Update TgProxy" : "Download TgProxy");


    [RelayCommand]
    private void OpenLeakTest()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://ipleak.net/",
                UseShellExecute = true
            });
        }
        catch { /* best-effort */ }
    }

    // ── Troubleshooting: health check (v2.24.1) ──
    [RelayCommand]
    private void RunHealthCheck()
    {
        try
        {
            var results = VPNRouter.Core.Services.HealthCheck.RunAll();
            var report  = VPNRouter.Core.Services.HealthCheck.FormatReport(results);

            var reportPath = Path.Combine(AppPaths.DataDir, "last-health-check.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, report);

            // Open in system default text viewer.
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{reportPath}\"",
                    UseShellExecute = true
                };
            }
            else
            {
                // xdg-open on Linux, /usr/bin/open on macOS.
                var opener = OperatingSystem.IsMacOS()
                    ? "/usr/bin/open"
                    : "/usr/bin/xdg-open";
                psi = new ProcessStartInfo
                {
                    FileName = opener,
                    Arguments = $"\"{reportPath}\"",
                    UseShellExecute = false
                };
            }
            System.Diagnostics.Process.Start(psi);
            // v2.31.0-r4 (F-26): inline confirmation toast so the user
            // gets feedback that the report was saved + opened. Pre-fix
            // the menu item dismissed silently and the report only appeared
            // in a separate Notepad window — easy to miss on multi-monitor
            // setups or when Notepad opens behind VPNRouter.
            ShowRulesToast(VPNRouter.App.Localization.Strings.HealthCheckSavedToast);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ViewModel] Health check failed");
        }
    }

    // ── About dialog (v2.25.0) ──
    // Before v2.25.0 the version/build/by-line lived inline in the compact
    // header. The redesign gives the header back to badges + mode-toggle,
    // so the meta block moved into a dedicated About dialog accessible from
    // the ⋯ flyout. Command lives here rather than in code-behind so the
    // menu binding is declarative.
    [RelayCommand]
    private void OpenAbout()
    {
        try
        {
            // v2.25.12: pass `this` as DataContext so the AboutWindow XAML
            // can bind to L_* proxies on this VM ({Binding L_AboutTitle}
            // etc.). Without this the dialog opened with no DataContext
            // and every L_* binding silently resolved to empty string.
            var dlg = new VPNRouter.App.Views.AboutWindow
            {
                DataContext = this
            };

            // Give the dialog the main window as owner so it centres on top
            // and blocks input to the main window until closed (modal feel
            // without actually needing ShowDialog — plain Show() is fine here
            // because About is information-only, no return value).
            var app = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var owner = app?.MainWindow;
            if (owner != null)
                dlg.ShowDialog(owner);
            else
                dlg.Show();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ViewModel] Failed to open About dialog");
        }
    }

    // ── Troubleshooting: safe mode + reset (v2.23.1) ──
    // Menu header flips between "Reset config" and "Click again to
    // reset" so user has to double-click (cheap confirmation without
    // a separate dialog box that we'd need Avalonia.Controls.Dialog
    // for on every platform).
    [ObservableProperty] private bool _resetConfigArmed;
    public string ResetConfigMenuHeader =>
        ResetConfigArmed
            ? VPNRouter.App.Localization.Strings.SmpMenuResetConfirm
            : VPNRouter.App.Localization.Strings.SmpMenuResetConfig;

    partial void OnResetConfigArmedChanged(bool value)
        => OnPropertyChanged(nameof(ResetConfigMenuHeader));

    [RelayCommand]
    private void RestartInSafeMode()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            ProcessStartInfo psi;
            if (OperatingSystem.IsLinux())
            {
                // Use setsid --fork so the new instance survives our exit
                // (same trick the updater uses after applying an update).
                psi = new ProcessStartInfo("/usr/bin/setsid",
                    $"--fork \"{exe}\" --safe")
                { UseShellExecute = false, CreateNoWindow = true };
            }
            else
            {
                psi = new ProcessStartInfo(exe, "--safe")
                { UseShellExecute = false, CreateNoWindow = true };
            }
            System.Diagnostics.Process.Start(psi);
            // Release lock so next run's crash detector doesn't flag us.
            try { VPNRouter.Core.Services.LockFile.Release(); } catch { }
            Environment.Exit(0);
        }
        catch { /* user can still launch with --safe from terminal */ }
    }

    // v2.31.0-r3 (VM-11): track the auto-disarm task so multiple clicks
    // don't stack stale Task.Delay continuations. Pre-fix every "arm" click
    // queued a fresh disarm Task with no cancellation; if the user clicked
    // arm-disarm-arm rapidly each disarm fired blindly later (mostly
    // harmless because it re-set false=>false, but a leak nonetheless).
    private System.Threading.CancellationTokenSource? _resetDisarmCts;

    [RelayCommand]
    private void ResetConfig()
    {
        // First click: arm the confirmation.
        if (!ResetConfigArmed)
        {
            ResetConfigArmed = true;
            // Cancel any prior disarm task before queuing a new one.
            var oldCts = _resetDisarmCts;
            _resetDisarmCts = new System.Threading.CancellationTokenSource();
            var token = _resetDisarmCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                oldCts.Dispose();
            }
            // Auto-disarm after 5 seconds so a stale armed state can't
            // ambush a later click that was meant for something else.
            _ = Task.Delay(5000, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ResetConfigArmed = false);
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }
        ResetConfigArmed = false;
        // Confirmed — cancel pending disarm; we're either resetting now
        // (Environment.Exit below) or aborting via the catch.
        _resetDisarmCts?.Cancel();
        _resetDisarmCts?.Dispose();
        _resetDisarmCts = null;

        try
        {
            var backup = VPNRouter.Core.Services.SettingsLoader.ResetToDefaults();
            _logger?.Warning("[ViewModel] Config reset to defaults; backup at {Backup}", backup ?? "(none)");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ViewModel] Config reset failed");
            return;
        }

        // Restart fresh — no --safe needed, defaults are clean.
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            ProcessStartInfo psi;
            if (OperatingSystem.IsLinux())
            {
                psi = new ProcessStartInfo("/usr/bin/setsid",
                    $"--fork \"{exe}\"")
                { UseShellExecute = false, CreateNoWindow = true };
            }
            else
            {
                psi = new ProcessStartInfo(exe)
                { UseShellExecute = false, CreateNoWindow = true };
            }
            System.Diagnostics.Process.Start(psi);
            try { VPNRouter.Core.Services.LockFile.Release(); } catch { }
            Environment.Exit(0);
        }
        catch { /* reset already happened on disk, user can relaunch manually */ }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            var logsDir = AppPaths.LogsDir;
            Directory.CreateDirectory(logsDir);

            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{logsDir}\"",
                    UseShellExecute = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"\"{logsDir}\"",
                    UseShellExecute = false
                };
            }
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* best-effort */ }
    }

    // ── VLESS fields (for single-server quick edit) ──
    [ObservableProperty] private string _vlessUri = string.Empty;

    // ── Collections ──
    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<CustomConfigViewModel> CustomConfigs { get; } = new();
    public ObservableCollection<ServerViewModel> SubscriptionServers { get; } = new();
    [ObservableProperty] private ServerViewModel? _selectedSubscriptionServer;
    public ObservableCollection<AppGroupViewModel> AppGroups { get; } = new();

    // ── Selected items ──
    [ObservableProperty] private ServerViewModel? _selectedServer;
    [ObservableProperty] private CustomConfigViewModel? _selectedCustomConfig;

    // ── Sub-ViewModels ──
    public UpdateNotificationViewModel UpdateVm { get; }
    public ServiceViewModel ServiceVm { get; }
    public FreeConfigsPageViewModel FreeConfigsVm { get; private set; } = null!;

    public MainWindowViewModel()
    {
        _logger = new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "vpnrouter.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();

        // v2.31.6-r16 (iter#7 / Phase 3): wire the static TcpTlsProbe logger
        // so quick-probe runs (Servers/Subscribe Test all + Free Configs bulk)
        // emit Debug-level entries to vpnrouter*.log. User feedback:
        // «есть ли у проверки логи?» — pre-r16 the answer was no (zero log
        // calls in TcpTlsProbe). r16 logs every probe target + outcome.
        TcpTlsProbe.Logger = _logger;

        // v2.29.0-r7+ Layer 7 — pick up receipt-derived "previous update
        // didn't land" warning that App.axaml.cs OnFrameworkInitialization
        // stored before this VM was constructed. The HasUpdateWarning
        // banner becomes visible immediately on first window paint.
        if (!string.IsNullOrWhiteSpace(Program.PendingUpdateWarning))
        {
            UpdateWarningText = Program.PendingUpdateWarning!;
            Program.PendingUpdateWarning = null; // consume — don't re-set on hot-reload
        }

        AppPaths.EnsureDirectories();
        DeployBundledProfiles();

        _engine = PlatformServices.CreateVpnEngine(_logger);
        _engine.StatusChanged += OnEngineStatus;

        _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

        // Sub-VMs
        UpdateVm = new UpdateNotificationViewModel(_settings.Update, _logger);
        ServiceVm = new ServiceViewModel(_logger);
        FreeConfigsVm = new FreeConfigsPageViewModel(_logger, ApplyFreeConfigAsync, () => _settings);

        // v2.27 Bug B: SmpAutostartChecked is a computed over ServiceVm state,
        // so we need to re-fire PropertyChanged on Simple's checkbox binding
        // every time the service transitions. Without this, an Advanced-mode
        // "Enable background service" toggle that flips IsInstalled/IsRunning
        // silently leaves Simple's UI stale until the user navigates away and
        // back. Scoped to the two properties that actually feed the computed
        // — ignores IsBusy / StatusMessage churn during install.
        //
        // v2.31.10 (autostart UX clarity): IsInstalled also feeds the
        // per-component status badges (LblAutostart{Vpn,Zapret,TgProxy}Status
        // + the IsAutostart*StatusGood/Warn/Bad triplet). When the user toggles
        // the master service, all 12 of those bindings need a fresh read.
        // IsRunning is intentionally not included for the new badges — the
        // boot semantics depend on IsInstalled, not on whether SCM has a live
        // process right now.
        ServiceVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServiceViewModel.IsInstalled)
                               or nameof(ServiceViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(SmpAutostartChecked));
            }
            if (e.PropertyName == nameof(ServiceViewModel.IsInstalled))
            {
                OnPropertyChanged(nameof(LblAutostartVpnStatus));
                OnPropertyChanged(nameof(LblAutostartZapretStatus));
                OnPropertyChanged(nameof(LblAutostartTgProxyStatus));
                OnPropertyChanged(nameof(IsAutostartVpnStatusGood));
                OnPropertyChanged(nameof(IsAutostartVpnStatusWarn));
                OnPropertyChanged(nameof(IsAutostartVpnStatusBad));
                OnPropertyChanged(nameof(IsAutostartZapretStatusGood));
                OnPropertyChanged(nameof(IsAutostartZapretStatusWarn));
                OnPropertyChanged(nameof(IsAutostartZapretStatusBad));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusGood));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusWarn));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusBad));
            }
        };

        LoadSettingsIntoUI();

        // Detect VPN already running (e.g. started by Windows Service on boot)
        DetectServiceManagedVpn();

        // Background update check (fire-and-forget, silent fail)
        _ = UpdateVm.CheckOnStartupAsync();

        // Status dashboard (v2.15.0): poll VPN/Zapret/TgProxy every 2s
        StartRuntimeStatusPolling();

        // v2.31.10 — App-side autostart bootstrap. Closes the gap where
        // autostart_tgproxy / autostart_zapret in config.yaml were read
        // into UI state but never spawned the daemons unless the Windows
        // Service was installed. Defers to the Service when it's running
        // (Service handles boot-spawn). See
        // MainWindowViewModel.AutostartBootstrap.cs for the gating logic.
        _ = BootstrapAutostartAsync();
    }

    /// <summary>
    /// Detect if VPN is already running via Windows Service (sing-box process alive).
    /// Sets IsConnected so the UI reflects reality instead of showing "Not connected".
    /// </summary>
    /// <summary>Raised when the active server (green-dot) changes — views scroll to it.</summary>
    public event Action<ServerViewModel?>? ActiveServerChanged;

    /// <summary>
    /// Update IsActive flag on all ServerViewModels so the UI shows a green dot
    /// next to the currently-active server (both VLESS and Subscription lists).
    /// </summary>
    private void RefreshActiveIndicator()
    {
        var activeIp = _engine?.ActiveServerAddress;

        // v2.30.1-r3 fix: gate the active dot by ConfigMode so a manual
        // VLESS entry that happens to share an IP with a subscription
        // server doesn't light up alongside the subscription one.
        // v2.30.1-r6 fix: also disambiguate WITHIN each list — when two
        // entries share an IP (e.g. port 443 + port 8443 on the same
        // host, or VLESS + Hysteria2 on the same host), the previous
        // IP-only match lit BOTH up. User report 2026-05-01: "у меня
        // 2 конфига на 1 ip и при включения одного из них подсвечиваются
        // оба, будто я включил не 1 а 2".
        //
        // Match priority:
        //   1. Name == settings.Vless.ActiveServer (manual mode) /
        //      App.ActiveSubscriptionServer (subscribe mode) — the
        //      authoritative "which entry was picked" signal.
        //   2. Fallback to IP match when no name is set (legacy entries).
        //
        // The name path picks exactly one row even if many share an IP.
        var configMode = _settings?.App?.ConfigMode ?? "generated";
        var isManualMode = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);
        var isSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        var manualActiveName = _settings?.Vless?.ActiveServer;
        var subscriptionActiveName = _settings?.App?.ActiveSubscriptionServer;

        ServerViewModel? active = null;

        foreach (var s in Servers)
        {
            var isActive = isManualMode
                && IsConnected
                && !string.IsNullOrEmpty(activeIp)
                && IsRowActive(s, activeIp, manualActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        foreach (var s in SubscriptionServers)
        {
            var isActive = isSubscribeMode
                && IsConnected
                && !string.IsNullOrEmpty(activeIp)
                && IsRowActive(s, activeIp, subscriptionActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        ActiveServerChanged?.Invoke(active);
    }

    /// <summary>
    /// v2.30.1-r6: disambiguate "active" rows when two entries share
    /// the same IP. If we have a known active-name (from
    /// <c>Vless.ActiveServer</c> or <c>App.ActiveSubscriptionServer</c>),
    /// only the row whose <c>Name</c> matches lights up. Otherwise we
    /// fall back to the legacy IP-only match so settings.yaml files
    /// from before this change still work.
    /// </summary>
    private static bool IsRowActive(ServerViewModel row, string activeIp, string? activeName)
    {
        if (row.Server != activeIp)
            return false;

        // No active-name available → legacy IP-only behaviour.
        if (string.IsNullOrWhiteSpace(activeName))
            return true;

        // Active-name available → require an exact name match. This
        // prevents two entries with the same IP from both lighting up.
        return string.Equals(row.Name, activeName, StringComparison.OrdinalIgnoreCase);
    }

    private void DetectServiceManagedVpn()
    {
        try
        {
            // v2.26.1 — two-signal detection:
            //   1. sing-box process visible (old single check)
            //   2. TUN ownership semaphore held by SOMEONE
            // Both must be true. Signal #1 alone had a false-positive
            // window on startup: a sing-box that just exited but whose
            // process record Windows hadn't reaped yet would still show
            // up in GetProcessesByName and we'd flip IsConnected=true
            // only to demote it on the next poll. TUN-lock check gates
            // that: once the owner releases (on Stop or death), the
            // kernel releases the semaphore atomically so there's no
            // stale window.
            var singboxRunning = Process.GetProcessesByName("sing-box").Length > 0;
            if (!singboxRunning) return;

            var tunOwned = TunOwnershipLock.IsOwnedByAnyone();
            if (!tunOwned)
            {
                // sing-box.exe present but nobody holds the TUN semaphore
                // — orphan / zombie from a previous run, not a live
                // service-managed tunnel. Let OrphanCleanup reap it on
                // the next cycle; don't adopt.
                return;
            }

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            StartSubRefreshTimer();
            _logger.Information("[VM] Detected VPN running via service (sing-box alive + TUN owned)");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] DetectServiceManagedVpn failed");
        }
    }

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
            try { VPNRouter.Core.Services.SettingsLoader.Save(_settings); } catch { }
        }
        IsRussian = storedLang.Equals("ru", StringComparison.OrdinalIgnoreCase);
        Strings.Lang = IsRussian ? "ru" : "en";

        // Theme
        IsDarkTheme = (_settings.App.Theme ?? "light").Equals("dark", StringComparison.OrdinalIgnoreCase);
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

        // IPv4 + DNS flush + Strict DNS
        ForceIpv4Only = _settings.App.ForceIpv4Only;
        FlushDnsOnStart = _settings.App.FlushDnsOnStart;
        StrictDns = _settings.App.StrictDns;
        BlockAds = _settings.App.BlockAds;

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
            ZapretStatus = IsRussian ? "Остановлен" : "Stopped";
        }

#if PLATFORM_WINDOWS
        DiscordHostsInstalled = VPNRouter.Core.Services.HostsManager.IsInstalled();
        FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();

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
            TgProxyStatus = IsRussian ? "Остановлен" : "Stopped";
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
        SelectedServer = activeServer ?? Servers.FirstOrDefault();

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

    private void LoadApps()
    {
        // v2.31.0-r3 (VM-8): explicit unwiring before Clear(). ObservableCollection.Clear()
        // raises CollectionChanged with action=Reset where both NewItems and OldItems are
        // null, so the WireAppChangeTracking handler can't unsubscribe old PropertyChanged
        // delegates. Without this, every RU↔EN toggle (which calls LoadApps) leaks one
        // subscription per existing AppGroupViewModel + AppItemViewModel. Cumulative.
        UnwireAllAppGroups();
        AppGroups.Clear();

        var activeProfileStr = _settings.ActiveProfile ?? "";
        var isFirstLaunch = string.IsNullOrWhiteSpace(activeProfileStr);

        var activeProfiles = activeProfileStr
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Load from profiles. Per-platform variants:
        //   macOS → default-macos.json
        //   Linux → default-linux.json (v2.21.6)
        //   Windows + fallback → default.json
        var profileFile = OperatingSystem.IsMacOS() ? "default-macos.json"
                        : OperatingSystem.IsLinux() ? "default-linux.json"
                        : "default.json";
        var profilePath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        // Fallback to default.json if the platform-specific variant is missing.
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, "default.json");

        if (File.Exists(profilePath))
        {
            try
            {
                var json = File.ReadAllText(profilePath);
                var collection = Newtonsoft.Json.JsonConvert.DeserializeObject<ProfileCollection>(json);
                if (collection?.Profiles != null)
                {
                    foreach (var profile in collection.Profiles)
                    {
                        // First launch: select all profiles by default
                        var isActive = isFirstLaunch || activeProfiles.Any(p =>
                            p.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

                        var group = new AppGroupViewModel(profile.Name, profile.Description, isActive);

                        foreach (var proc in profile.Processes)
                        {
                            var name = StripExe(proc.Name);
                            group.Apps.Add(new AppItemViewModel(name, isActive));
                        }

                        // Merge user-added custom apps for this group
                        if (_settings.CustomGroupApps != null
                            && _settings.CustomGroupApps.TryGetValue(profile.Name, out var extras))
                        {
                            foreach (var extra in extras)
                            {
                                if (string.IsNullOrWhiteSpace(extra)) continue;
                                var extraName = StripExe(extra);
                                if (group.Apps.Any(a => a.ProcessName.Equals(extraName, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                group.Apps.Add(new AppItemViewModel(extraName, isActive, isCustom: true));
                            }
                        }

                        AppGroups.Add(group);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load profiles");
            }
        }

        // Custom apps group — ALWAYS create (even empty) so SaveSettings can
        // distinguish "loaded but empty" from "never loaded".
        var customApps = _settings.CustomApps ?? new();
        var customGroup = new AppGroupViewModel("Custom Apps", "Your custom applications", true) { IsCustomGroup = true, IsExpanded = true };
        foreach (var app in customApps)
        {
            if (!string.IsNullOrEmpty(app))
                customGroup.Apps.Add(new AppItemViewModel(StripExe(app), true, isCustom: true));
        }
        AppGroups.Add(customGroup);

        // User-created categories (persisted separately from default groups)
        foreach (var cat in _settings.CustomCategories ?? new())
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            var group = new AppGroupViewModel(cat.Name, "", cat.Enabled) { IsCustomCategory = true };
            foreach (var app in cat.Apps ?? new())
            {
                if (string.IsNullOrWhiteSpace(app)) continue;
                group.Apps.Add(new AppItemViewModel(StripExe(app), cat.Enabled, isCustom: true));
            }
            AppGroups.Add(group);
        }

        _appsLoaded = true;
        WireAppChangeTracking();
    }

    /// <summary>
    /// Hook property-change listeners on all AppGroups + their Apps to set
    /// HasPendingAppChanges when user edits the list while VPN is running.
    /// </summary>
    private bool _appChangeTrackingWired;

    private void WireAppChangeTracking()
    {
        if (!_appChangeTrackingWired)
        {
            AppGroups.CollectionChanged += (s, e) =>
            {
                if (_isLoadingUI) return;
                if (e.NewItems != null)
                    foreach (AppGroupViewModel g in e.NewItems)
                    {
                        g.PropertyChanged -= OnAppGroupPropertyChanged;
                        g.PropertyChanged += OnAppGroupPropertyChanged;
                        g.Apps.CollectionChanged -= OnAppsCollectionChanged;
                        g.Apps.CollectionChanged += OnAppsCollectionChanged;
                        foreach (var a in g.Apps)
                        {
                            a.PropertyChanged -= OnAppItemPropertyChanged;
                            a.PropertyChanged += OnAppItemPropertyChanged;
                        }
                    }
                HasPendingAppChanges = IsConnected;
            };
            _appChangeTrackingWired = true;
        }

        foreach (var group in AppGroups)
        {
            group.PropertyChanged -= OnAppGroupPropertyChanged;
            group.PropertyChanged += OnAppGroupPropertyChanged;
            group.Apps.CollectionChanged -= OnAppsCollectionChanged;
            group.Apps.CollectionChanged += OnAppsCollectionChanged;
            foreach (var app in group.Apps)
            {
                app.PropertyChanged -= OnAppItemPropertyChanged;
                app.PropertyChanged += OnAppItemPropertyChanged;
            }
        }
    }

    private void OnAppGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.PropertyName == nameof(AppGroupViewModel.IsChecked))
            HasPendingAppChanges = IsConnected;
    }

    /// <summary>
    /// v2.31.0-r3 (VM-8): unsubscribe PropertyChanged + CollectionChanged
    /// from every AppGroup + its Apps before LoadApps() rebuilds the list.
    /// Avalonia's ObservableCollection.Clear() emits a Reset CollectionChanged
    /// without OldItems, so the wire-tracking handler can't unsubscribe.
    /// </summary>
    private void UnwireAllAppGroups()
    {
        foreach (var group in AppGroups)
        {
            group.PropertyChanged -= OnAppGroupPropertyChanged;
            group.Apps.CollectionChanged -= OnAppsCollectionChanged;
            foreach (var app in group.Apps)
                app.PropertyChanged -= OnAppItemPropertyChanged;
        }
    }

    private void OnAppsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.NewItems != null)
            foreach (AppItemViewModel a in e.NewItems)
            {
                a.PropertyChanged -= OnAppItemPropertyChanged;
                a.PropertyChanged += OnAppItemPropertyChanged;
            }
        HasPendingAppChanges = IsConnected;
    }

    private void OnAppItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
            HasPendingAppChanges = IsConnected;
    }

    /// <summary>
    /// True when sing-box is running but NOT started by this App instance —
    /// i.e. the Windows Service owns the tunnel. Used by Apply to avoid a
    /// silent-fail call into <see cref="VpnEngine.ApplyAsync"/> (which would
    /// bail immediately because our local engine has no sing-box process).
    /// </summary>
    private bool IsServiceManagedVpn => IsConnected && !(_engine?.IsRunning ?? false);

    [RelayCommand]
    private Task ApplyPendingChangesAsync() => ApplyPendingChangesInternalAsync(forceRestart: false);

    /// <summary>
    /// v2.29.0 — Apps page full-tunnel banner action. When user is in
    /// full-tunnel mode the apps list is irrelevant (all traffic is
    /// routed through VPN regardless of selection); previously the page
    /// silently disabled the entire Grid which read as "broken" to a
    /// Mac tester (2026-04-29 feedback). Now we show a banner with this
    /// command as the action. Flips IsSplitTunnel + persists.
    /// HasPendingAppChanges is set so the user sees the standard Apply
    /// gating without us having to start a tunnel restart unilaterally
    /// — the routing-mode change requires a forceRestart Apply, which
    /// the user kicks off themselves via the Apply bar.
    /// </summary>
    [RelayCommand]
    private void SwitchToSplitTunnel()
    {
        if (IsSplitTunnel) return; // no-op if already split
        IsSplitTunnel = true;
        HasPendingAppChanges = true;
        SaveSettings();
    }

    /// <summary>
    /// v2.20.4: shared Apply pipeline with a <c>forceRestart</c> switch.
    /// Callers changing RoutingMode (split ↔ full) or other structural
    /// sing-box config should pass true — hot-reload doesn't re-do the
    /// TUN routing table, so the user sees no effect if we rely on it.
    /// </summary>
    private async Task ApplyPendingChangesInternalAsync(bool forceRestart)
    {
        if (IsApplying || !IsConnected) return;
        IsApplying = true;
        try
        {
            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            if (IsServiceManagedVpn)
            {
                // v2.18.4: the sing-box process is owned by the Windows
                // Service, so hot-reload via our local engine isn't an
                // option — it has no sing-box to talk to. Pre-v2.18.4 we
                // punted here with a "Stop and Start VPN to apply" hint,
                // which forced the user to click Disconnect + Connect
                // after every Split/Full or server change. Terrible UX.
                //
                // New behaviour: invoke the already-existing
                // ServiceVm.RestartServiceCommand (stop → start cycle).
                // The service re-reads config.yaml via SettingsLoader.Load
                // on boot and spawns sing-box with the freshly-saved
                // RoutingMode / ActiveProfile / subscription picks.
                //
                // Fallback to the old "please restart manually" text only
                // if service isn't available at all (shouldn't happen when
                // IsServiceManagedVpn is true, but belt-and-braces).
                if (ServiceVm.IsAvailable)
                {
                    StatusText = IsRussian
                        ? "Перезапускаю службу с новыми настройками..."
                        : "Restarting service with new settings...";
                    await ServiceVm.RestartServiceCommand.ExecuteAsync(null);
                    HasPendingAppChanges = false;
                    // The 2-second SyncConnectedWithVpnRuntime poll in
                    // RuntimeStatus will pick up the new service state and
                    // refresh StatusText to the "connected via service
                    // [mode]" line. No extra plumbing needed here.
                    return;
                }

                HasPendingAppChanges = false;
                StatusText = IsRussian
                    ? "Настройки сохранены. Остановите и запустите VPN, чтобы они применились (служба перечитает config.yaml при старте)."
                    : "Settings saved. Stop and Start VPN to apply — the service re-reads config.yaml on start.";
                return;
            }

            var ok = await Task.Run(() => _engine.ApplyAsync(_settings, CancellationToken.None, forceRestart));
            if (ok)
            {
                HasPendingAppChanges = false;
                RestoreConnectedStatus();
            }
            else
            {
                StatusText = IsRussian ? "Не удалось применить" : "Apply failed";
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ApplyPendingChanges failed");
            StatusText = $"Apply failed: {ex.Message}";
        }
        finally { IsApplying = false; }
    }

    /// <summary>Rebuild the "Connected [mode · tunnel] → server (ip)" status line after Apply.</summary>
    private void RestoreConnectedStatus()
    {
        if (!IsConnected) return;
        var serverIp = _engine.ActiveServerAddress;
        string? serverName = null;
        if (IsSubscribeMode)
            serverName = (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName;
        else
            serverName = (SelectedServer ?? Servers.FirstOrDefault())?.DisplayName;

        var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
        var tunnelLabel = IsSplitTunnel ? "split" : "full";
        var modeLabel = $"{configLabel}/{tunnelLabel}";

        StatusText = Strings.Connected(modeLabel, serverName, serverIp);
    }

    /// <summary>
    /// One-time: create /etc/sudoers.d/vpnrouter via osascript on UI thread
    /// so the admin password dialog appears properly.
    ///
    /// <para>v2.28.6-r6: two bug fixes for the "sudo: a password is required"
    /// failure on macOS that left users unable to start the VPN:</para>
    /// <list type="number">
    /// <item><b>Escape spaces in path</b>. The default install path is
    /// <c>/Users/$USER/Library/Application Support/VPNRouter/bin/sing-box</c>
    /// — sudoers' <c>Cmnd_Spec</c> grammar requires spaces to be escaped
    /// with a backslash, otherwise the rule is malformed and sudo silently
    /// falls back to password prompt → fails because no terminal.</item>
    /// <item><b>Add <c>*</c> wildcard for arguments</b>. Without it, the rule
    /// only matches a bare <c>sudo sing-box</c> call with NO arguments —
    /// but we always invoke <c>sudo sing-box run -c &lt;path&gt;</c>. With
    /// the wildcard, any argument list is allowed.</item>
    /// </list>
    /// <para>For users who already have a broken sudoers file from
    /// v2.28.6-r1..r5 or older, the marker comment <c>SudoersFormatMarker</c>
    /// flags whether the current rewrite has been applied; if absent, we
    /// rewrite (which means the user gets a one-time osascript prompt
    /// after upgrading).</para>
    /// </summary>
    private const string SudoersFormatMarker = "# vpnrouter v2.28.6-r6 sudoers (escaped spaces + args wildcard)";

    private void EnsureMacSudoAccess()
    {
        const string sudoersPath = "/etc/sudoers.d/vpnrouter";

        // v2.28.6-r6: check the file's CONTENT, not just existence — older
        // releases wrote a malformed file (spaces unescaped, no args
        // wildcard) that exists on disk but doesn't grant NOPASSWD for
        // our actual sudo invocation.
        bool needsRewrite = true;
        try
        {
            if (File.Exists(sudoersPath))
            {
                var existing = File.ReadAllText(sudoersPath);
                if (existing.Contains(SudoersFormatMarker, StringComparison.Ordinal))
                    needsRewrite = false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // /etc/sudoers.d/vpnrouter is mode 0440 root:wheel — non-root
            // user can't read it. If the file exists but we can't see its
            // content, assume the previous (possibly malformed) write
            // already happened and we need a rewrite to be safe.
            // (osascript prompt is a one-time annoyance — much better
            // than a permanently broken VPN.)
            needsRewrite = true;
        }
        catch { needsRewrite = true; }
        if (!needsRewrite) return;

        StatusText = IsRussian ? "Настройка sudo (один раз)..." : "Setting up sudo (one-time)...";

        // v2.28.6-r6: escape spaces in the binary path for sudoers
        // Cmnd_Spec syntax. Add ` *` wildcard so any arguments
        // (`run -c <path>`) are allowed under NOPASSWD.
        var user = Environment.UserName;
        var singbox = AppPaths.SingBoxExePath;
        var singboxEscaped = singbox.Replace(" ", "\\ ");
        var tmpFile = Path.Combine(Path.GetTempPath(), "vpnrouter-sudoers");
        File.WriteAllText(tmpFile,
            $"{SudoersFormatMarker}\n" +
            $"{user} ALL=(root) NOPASSWD: {singboxEscaped} *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/pkill *\n");

        // Write a helper script
        var helperScript = Path.Combine(Path.GetTempPath(), "vpnrouter-setup.sh");
        File.WriteAllText(helperScript,
            $"#!/bin/bash\ncp \"{tmpFile}\" {sudoersPath}\nchmod 0440 {sudoersPath}\nchown root:wheel {sudoersPath}\nrm -f \"{tmpFile}\" \"{helperScript}\"\n");
        File.SetUnixFileMode(helperScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // Exact same osascript format that works for sing-box launch
        var cmd = $"\\\"{helperScript}\\\"";
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"do shell script \"{cmd}\" with administrator privileges");

        _logger.Information("Running osascript for sudo setup...");
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
        {
            _logger.Error("Failed to start osascript");
            return;
        }

        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);

        _logger.Information("osascript exit={Exit} stdout={Out} stderr={Err}",
            proc.ExitCode, stdout, stderr);
        proc.Dispose();

        if (File.Exists(sudoersPath))
            _logger.Information("Passwordless sudo configured");
        else
            _logger.Warning("Failed to configure sudoers");
    }

    /// <summary>
    /// Strip .exe suffix on Unix platforms (macOS, Linux). sing-box matches
    /// by exact process name on Windows (Discord.exe) while on Unix the
    /// process name is bare (Discord, chrome, firefox). The profile JSON
    /// ships with Windows-style .exe names, and MacProcessScanner
    /// normalises at scan time, but the UI would still surface those .exe
    /// names to the user. Stripping in the UI + settings path keeps the
    /// Applications tab readable on Linux.
    /// v2.21.1: Linux added to the strip set (was macOS-only).
    /// </summary>
    private static string StripExe(string name)
    {
        name = name.Trim();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
        }
        return name;
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

        // IPv4 + DNS flush + Strict DNS
        _settings.App.ForceIpv4Only = ForceIpv4Only;
        _settings.App.FlushDnsOnStart = FlushDnsOnStart;
        _settings.App.StrictDns = StrictDns;
        _settings.App.BlockAds = BlockAds;
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

        // Theme & language
        _settings.App.Theme = IsDarkTheme ? "dark" : "light";
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
                .Where(a => a.IsChecked)
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
        }

        SettingsLoader.Save(_settings, AppPaths.ConfigYamlPath);
    }

    partial void OnReceivePrereleasesChanged(bool value)
    {
        if (_isLoadingUI) return;
        _settings.Update.Channel = value ? "experimental" : "stable";
        SettingsLoader.Save(_settings, AppPaths.ConfigYamlPath);
    }

    // ── Engine events ──

    private void OnEngineStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = status;

            if (status.StartsWith("Connected") || status.StartsWith("VPN Router is running"))
            {
                IsConnected = true;
                IsConnecting = false;
                ConnectButtonText = Strings.StopVPN;
                StartSubRefreshTimer();
                RefreshActiveIndicator();
                // Use engine's actual runtime state — not stale ViewModel cache.
                // This prevents "status says 104 but actually running 194" mismatch.
                var serverIp = _engine.ActiveServerAddress;
                string? serverName;
                if (IsSubscribeMode)
                {
                    var s = SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault();
                    serverName = s?.DisplayName;
                }
                else if (IsVlessMode)
                {
                    var s = SelectedServer ?? Servers.FirstOrDefault();
                    serverName = s?.DisplayName;
                }
                else
                {
                    var c = CustomConfigs.FirstOrDefault(x => x.IsActive)
                        ?? SelectedCustomConfig
                        ?? CustomConfigs.FirstOrDefault();
                    serverName = c?.Name;
                }
                var modeLabel = IsSplitTunnel ? "split" : "full";
                StatusText = Strings.Connected(modeLabel, serverName, serverIp);
            }
            else if (status == "Stopped")
            {
                IsConnected = false;
                IsConnecting = false;
                ConnectButtonText = Strings.StartVPN;
                StatusText = Strings.NotConnected;
                StopSubRefreshTimer();
                RefreshActiveIndicator();
                HasPendingAppChanges = false;
            }
        });
    }

    // ── Commands ──

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected || _engine.IsRunning)
        {
            IsConnecting = true;
            StatusText = Strings.Stopping;
            try
            {
                // v2.31.6-r20 — symmetric Stop. The pre-r20 path was a single
                // _engine.Stop() call that only affected the GUI's own engine.
                // If the Windows Service was the actual owner of sing-box (or
                // an older crashed GUI left orphans), _engine._singBox was
                // null and Stop became a no-op while the real sing-box kept
                // running. RuntimeStatusDetector then re-flipped IsConnected
                // back to true within 1-2 seconds — user reports
                // "press disconnect, it turns back on after a second".
                //
                // Mirror the cleanup the Connect-branch already does (kill
                // orphan sing-box + stop Windows Service) so Stop guarantees
                // the tunnel actually goes down regardless of who started it.
                await Task.Run(() =>
                {
                    try { _engine.Stop(); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] _engine.Stop"); }

                    // v2.31.10-r2: pass respectTunLock:false — user clicked
                    // Stop, so we explicitly INTEND to take down whoever
                    // is running sing-box (even Service-spawned). Default
                    // TunLock-aware path is for App startup; here it would
                    // turn the Stop button into a no-op when Service held
                    // the lock.
                    try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] OrphanCleanup on stop"); }

#if PLATFORM_WINDOWS
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "stop VPNRouter")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi);
                        proc?.WaitForExit(5000);
                    }
                    catch (Exception ex) { _logger.Debug(ex, "[VM] sc stop on disconnect"); }
#endif
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[VM] Error during Stop");
            }
            finally
            {
                IsConnected = false;
                IsConnecting = false;
                ConnectButtonText = Strings.StartVPN;
                StatusText = Strings.NotConnected;
                // v2.20.0: clear the freshly-connected guard so a later poll
                // can faithfully reflect whatever state sing-box ends up in.
                _lastSuccessfulConnectAt = DateTime.MinValue;
            }
            return;
        }

        {
            IsConnecting = true;
            StatusText = Strings.Starting;
            ConnectButtonText = Strings.Starting;

            // Ensure clean state: stop any existing VPN, kill orphans,
            // stop Windows Service. This guarantees the TUN lock is free.
            await Task.Run(() =>
            {
                try
                {
                    // Stop our own engine if it's somehow still running
                    if (_engine.IsRunning)
                        _engine.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[VM] Pre-start engine stop");
                }

                // v2.31.10-r2: pass respectTunLock:false — user clicked
                // Connect, so we explicitly INTEND to free the TUN lock
                // (kill whatever is currently holding it, including
                // Service-spawned sing-box) before our own engine tries
                // to acquire it. Without this, default TunLock-aware
                // skip would leave the Service-spawned sing-box alive
                // and the next sc-stop wouldn't reach it via this VM.
                try { OrphanCleanup.KillOrphans(logger: null, respectTunLock: false); } catch { }

#if PLATFORM_WINDOWS
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "stop VPNRouter")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(5000);
                    if (proc?.ExitCode == 0) Thread.Sleep(2000);
                }
                catch { }
#endif
            });

            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            // Subscribe mode: aggregate enabled subscriptions → feed into VLESS engine path
            var aggregatedServers = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();
            if (IsSubscribeMode && aggregatedServers.Count > 0)
            {
                _settings.Vless.Servers = aggregatedServers;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                // v2.30.2-r3 Bug 2A fix #2: same fix as r2's
                // ReconnectAsync.Subscription branch — do NOT force
                // ConfigMode=generated. The initial-connect path here
                // had the same bug-for-bug indicator gate problem:
                // RefreshActiveIndicator() reads ConfigMode and gates
                // SubscriptionServers list highlighting on
                // ConfigMode=="subscribe". Forcing to "generated"
                // killed the green dot on the Subscriptions list even
                // though the engine connected correctly.
                //
                // Caught during in-app smoke test on r2 — clicking
                // Запустить VPN button on a sub server connected fine
                // ("Подключено [full] → de-01 443 main-brat") but the
                // row indicator stayed dark. Same fix as r2 reconnect.
                //
                // Engine still uses Vless.Servers + Vless.ActiveServer
                // we just wrote. Resolver re-aggregates idempotently
                // when ConfigMode=subscribe — same content, same
                // active. Net: identical engine behaviour, correct UI.
                _logger?.Information(
                    "[VM] ToggleConnectionAsync.Connect.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregatedServers.Count, _settings.Vless.ActiveServer);
            }

            // macOS: ensure sudo access (one-time password prompt)
            if (OperatingSystem.IsMacOS())
                await Task.Run(EnsureMacSudoAccess);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Task.Run(() => _engine.StartAsync(_settings, cts.Token), cts.Token);

                // v2.20.6: poll _engine.IsRunning up to 10 seconds, on a
                // thread-pool thread, WITHOUT the 30 s StartAsync CTS.
                //
                // v2.20.4 ran the poll on the UI thread inside a `while`
                // and used `await Task.Delay(250, cts.Token)`. Two bugs:
                //
                //   1. `_engine.IsRunning` on macOS calls IsClashApiAlive
                //      (synchronous HTTP GET with 3-second timeout). That
                //      was blocking the UI thread up to 3 s per iteration.
                //   2. Passing cts.Token to Task.Delay meant that if
                //      StartAsync had consumed 25+ s on macOS (sudo +
                //      sing-box warmup + healthmonitor setup), the CTS
                //      tripped DURING our poll → OperationCanceledException
                //      → catch block → _engine.Stop() → OnEngineStatus
                //      emits "Stopped" → UI demotes to "not connected"
                //      even though sing-box is actually running.
                //
                // Fix: poll runs on a thread-pool thread (no UI block),
                // uses Thread.Sleep locally (no token, no exception), and
                // gets a fresh 10 s window that starts AFTER StartAsync
                // returns. Windows IsRunning is O(1) (Process.HasExited),
                // so the loop exits on the first iteration there.
                var ready = await Task.Run(() =>
                {
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < until)
                    {
                        if (_engine.IsRunning) return true;
                        System.Threading.Thread.Sleep(250);
                    }
                    return false;
                });

                if (ready)
                {
                    IsConnected = true;
                    IsConnecting = false;
                    _lastSuccessfulConnectAt = DateTime.UtcNow;
                    ConnectButtonText = Strings.StopVPN;
                    StartSubRefreshTimer();
                    RefreshActiveIndicator();
                }
                else
                {
                    // StartAsync returned without exception but the
                    // engine still reports not-running after 10 s. Don't
                    // demote — VpnEngine's own warmup task emits
                    // "Connected" after up to 15 s (see VpnEngine.cs
                    // line ~403/413), and OnEngineStatus will flip
                    // IsConnected=true when that event arrives. Explicit
                    // Stop() call would kill a tunnel that's actually
                    // working.
                    _logger.Warning("[VM] Engine not ready after 10 s — leaving state to OnEngineStatus");
                }
            }
            catch (TunOwnershipException)
            {
                _logger.Warning("[VM] TUN adapter owned by another VPNRouter instance");
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnected = false;
                IsConnecting = false;
                StatusText = IsRussian
                    ? "VPN адаптер занят. Попробуйте ещё раз."
                    : "TUN adapter busy. Try again.";
                ConnectButtonText = Strings.StartVPN;
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.Error("[VM] Start timed out after 30s");
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnecting = false;
                IsConnected = false;
                StatusText = IsRussian
                    ? "Таймаут при запуске (30 сек). Проверьте логи."
                    : "Startup timed out (30s). Check logs.";
                ConnectButtonText = Strings.StartVPN;
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start VPN");
                IsConnecting = false;
                StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
                ConnectButtonText = Strings.StartVPN;
                return;
            }
        }
    }

    /// <summary>Rebuild aggregated server pool from all enabled subscriptions.</summary>
    private void RebuildSubscriptionPool()
    {
        var selectedName = SelectedSubscriptionServer?.Name;
        SubscriptionServers.Clear();

        foreach (var sub in Subscriptions)
        {
            if (!sub.Enabled) continue;
            foreach (var serverEntry in sub.UnderlyingEntry.Servers)
                SubscriptionServers.Add(new ServerViewModel(serverEntry));
        }

        // Restore selection if possible
        SelectedSubscriptionServer = SubscriptionServers
            .FirstOrDefault(s => s.Name == selectedName)
            ?? SubscriptionServers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task AddSubscriptionAsync()
    {
        var url = (NewSubUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        var name = (NewSubName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = $"Sub {Subscriptions.Count + 1}";

        var entry = new SubscriptionEntry { Name = name, Url = url, Enabled = true };
        _settings.App.Subscriptions.Add(entry);
        var svm = new SubscriptionViewModel(entry);
        Subscriptions.Add(svm);

        NewSubName = string.Empty;
        NewSubUrl = string.Empty;

        // Auto-switch to Subscribe tab so user sees the newly added subscription
        // and its fetched servers. Without this, if user adds subscription while
        // on Manual (VLESS) tab, the result happens "behind the scenes" and user
        // has no visual confirmation, leading to the "invisible add" perception.
        if (!IsSubscribeMode)
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
            SelectedTabIndex = 1; // Subscribe tab
        }

        // Immediately refresh this new subscription.
        // RefreshSubscriptionAsync now has fail-safe RebuildSubscriptionPool + SaveSettings
        // in its finally block, so even if fetch fails (bad URL, network), the UI
        // shows the subscription entry (just without servers) instead of appearing
        // to have done nothing.
        await RefreshSubscriptionAsync(svm);

        // Belt-and-suspenders: rebuild one more time after Refresh in case
        // Refresh's finally was short-circuited by some future exception path.
        RebuildSubscriptionPool();
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveSubscription(SubscriptionViewModel? sub)
    {
        if (sub == null) return;
        Subscriptions.Remove(sub);
        _settings.App.Subscriptions.RemoveAll(e => e.Id == sub.Id);
        RebuildSubscriptionPool();
        SaveSettings();
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync(SubscriptionViewModel? sub)
    {
        if (sub == null || string.IsNullOrWhiteSpace(sub.Url)) return;
        if (sub.IsRefreshing) return;

        sub.IsRefreshing = true;
        try
        {
            var count = await SubscriptionFetcher.RefreshEntryAsync(
                sub.UnderlyingEntry, _logger, CancellationToken.None);
            sub.LastServerCount = count;
            sub.LastRefreshedAt = sub.UnderlyingEntry.LastRefreshedAt;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", sub.Url);
        }
        finally
        {
            sub.IsRefreshing = false;
            // Always rebuild + save, even on exception. Previously these only ran
            // on the happy path, so a fetch failure left the UI with zero servers
            // visible for the new subscription — user thought nothing happened.
            // Now: failure means "sub entry exists, no servers" rather than
            // "sub entry exists but UI still shows old state".
            RebuildSubscriptionPool();
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task RefreshAllSubscriptionsAsync()
    {
        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        foreach (var s in enabled) s.IsRefreshing = true;
        try
        {
            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(
                        s.UnderlyingEntry, _logger, CancellationToken.None);
                    s.LastServerCount = count;
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[VM] Refresh of {Url} failed", s.Url);
                }
            }));
        }
        finally
        {
            foreach (var s in enabled) s.IsRefreshing = false;
            // Rebuild + save in finally so even if Task.WhenAll itself throws
            // (shouldn't normally — inner try/catch per entry — but defensive),
            // the UI still reflects any entries that did complete successfully.
            RebuildSubscriptionPool();
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task SyncSubscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(SubscriptionUrl))
        {
            StatusText = IsRussian ? "Введите URL подписки" : "Enter subscription URL";
            return;
        }

        StatusText = Strings.Syncing;
        try
        {
            var entries = await SubscriptionFetcher.FetchAsync(SubscriptionUrl, _logger);

            if (entries.Count == 0)
            {
                StatusText = Strings.SyncEmpty;
                return;
            }

            // Replace subscription servers list
            SubscriptionServers.Clear();
            foreach (var entry in entries)
                SubscriptionServers.Add(new ServerViewModel(entry));

            // Select first server as active
            SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault();
            SaveSettings();
            StatusText = Strings.SyncComplete(entries.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Subscription sync failed");
            StatusText = Strings.SyncFailed(ex.Message);
        }
    }

    // ── Subscription auto-refresh ──

    /// <summary>Start periodic subscription refresh (when VPN connected in subscribe mode).</summary>
    private void StartSubRefreshTimer()
    {
        StopSubRefreshTimer();
        if (!IsSubscribeMode) return;
        // v2.31.0-r3 (VM-1): multi-sub model uses Subscriptions[] — pre-fix
        // condition only checked the legacy single SubscriptionUrl field, so
        // users who had migrated to the multi-sub UI never got auto-refresh
        // even when they had multiple working subs. Accept either source.
        var hasLegacyUrl = !string.IsNullOrWhiteSpace(SubscriptionUrl);
        var hasEnabledMultiSub = Subscriptions.Any(s =>
            s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        if (!hasLegacyUrl && !hasEnabledMultiSub) return;

        _logger.Information("[SubRefresh] Starting timer (interval: {Sec}s)", SubRefreshIntervalMs / 1000);
        _subRefreshTimer = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(async () => await RefreshSubscriptionSilentAsync()),
            null,
            SubRefreshIntervalMs,
            SubRefreshIntervalMs);
    }

    /// <summary>Stop the subscription refresh timer.</summary>
    private void StopSubRefreshTimer()
    {
        _subRefreshTimer?.Dispose();
        _subRefreshTimer = null;
    }

    /// <summary>
    /// Silent subscription refresh — fetches new servers, compares UUIDs,
    /// and reconnects if they changed (e.g. server rotated UUID).
    /// </summary>
    private async Task RefreshSubscriptionSilentAsync()
    {
        if (!IsConnected || !IsSubscribeMode) return;

        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        // Cancel previous refresh if still running (prevents concurrent fetches on slow network)
        _subRefreshCts?.Cancel();
        _subRefreshCts = new CancellationTokenSource();
        var ct = _subRefreshCts.Token;

        try
        {
            _logger.Information("[SubRefresh] Checking {Count} subscription(s)...", enabled.Count);

            // Snapshot current aggregated UUIDs
            var beforeUuids = SubscriptionServers.Select(s => s.Uuid).OrderBy(u => u).ToList();

            // Parallel refresh, ignore per-entry failures
            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(s.UnderlyingEntry, _logger, ct);
                    s.LastServerCount = count;
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SubRefresh] Failed for {Url}", s.Url);
                }
            }));

            if (ct.IsCancellationRequested) return;

            // v2.31.8-r3: compare against UnderlyingEntry.Servers BEFORE
            // RebuildSubscriptionPool — the previous code rebuilt the
            // SubscriptionServers ObservableCollection unconditionally on
            // every hourly refresh. Clear() drops the
            // SelectedSubscriptionServer reference, then the
            // "restore selection" line at the end of
            // RebuildSubscriptionPool re-assigns SelectedSubscriptionServer
            // to a NEW ServerViewModel instance. The setter fires
            // OnSelectedSubscriptionServerChanged, which (when IsConnected
            // && IsSubscribeMode) triggers ReconnectAsync — full sing-box
            // stop+start cycle, ~3-4 s VPN downtime EVERY HOUR even when
            // the server set didn't change.
            //
            // Caught in brat-2026-05-05 logs: every hourly SubRefresh tick
            // disconnected/reconnected the tunnel even though the
            // "No UUID changes, no reconnect needed" log line was emitted
            // right after. Symptom user-reported: long-running TCP
            // connections (e.g. claude.exe → Anthropic API) failed during
            // each window, sometimes returning 403 because traffic
            // briefly fell back to direct route during the gap.
            //
            // Fix: compute afterUuids from UnderlyingEntry.Servers (the
            // model layer that the fetch actually populated), make the
            // change decision FIRST, and only call RebuildSubscriptionPool
            // when something actually changed. SaveSettings still runs in
            // both branches so the refreshed LastRefreshedAt timestamp
            // lands in YAML.
            var afterUuids = enabled
                .SelectMany(s => s.UnderlyingEntry.Servers
                    ?? Enumerable.Empty<VPNRouter.Core.Models.VlessServerEntry>())
                .Select(srv => srv.Uuid ?? string.Empty)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();
            var changed = !beforeUuids.SequenceEqual(afterUuids, StringComparer.Ordinal);

            SaveSettings();

            if (!changed)
            {
                _logger.Information("[SubRefresh] No UUID changes, skipping rebuild and reconnect");
                return;
            }

            _logger.Information("[SubRefresh] Servers changed, rebuilding pool and reconnecting...");
            RebuildSubscriptionPool();
            var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
            await ReconnectAsync(reconnectName);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubRefresh] Auto-refresh failed");
        }
    }

    /// <summary>Kill ALL winws.exe processes system-wide.</summary>
    private void KillAllZapret()
    {
#if PLATFORM_WINDOWS
        // v2.31.6-r12: Debug-log instead of swallowing silently.
        try { _zapret?.Stop(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] KillAllZapret: _zapret.Stop failed"); }

        // Force kill by process name
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] KillAllZapret: proc.Kill failed (PID {Pid})", proc.Id);
            }
            finally { proc.Dispose(); }
        }

        // Fallback: taskkill /F as last resort
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("taskkill", "/F /IM winws.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] KillAllZapret: taskkill fallback failed");
        }
#endif
    }

    /// <summary>Check if winws.exe is running (from previous session or manual start).</summary>
    private bool IsZapretRunning()
    {
#if PLATFORM_WINDOWS
        return System.Diagnostics.Process.GetProcessesByName("winws").Length > 0;
#else
        return false;
#endif
    }

    /// <summary>Load strategies from Flowseal .bat files + legacy built-ins.</summary>
    private void LoadZapretStrategies()
    {
        var names = new List<string>();

#if PLATFORM_WINDOWS
        if (VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            _parsedStrategies = VPNRouter.Core.Services.ZapretUpdater.ParseStrategies();
            names.AddRange(_parsedStrategies.Select(s => s.Name));
            ZapretVersionText = VPNRouter.Core.Services.ZapretUpdater.GetLocalVersion() ?? "?";
        }
        else
        {
            _parsedStrategies = new();
            ZapretVersionText = IsRussian ? "Не установлен" : "Not installed";
        }
#endif
        // Always add legacy + custom
        names.Add("multisplit");
        names.Add("fake+multisplit");
        names.Add("custom");

        ZapretStrategies = new System.Collections.ObjectModel.ObservableCollection<string>(names);

        // Restore saved strategy index
        var saved = _settings.App.ZapretStrategy;
        var idx = names.IndexOf(saved);
        ZapretStrategyIndex = idx >= 0 ? idx : 0;
    }

    [RelayCommand]
    private async Task UpdateZapretAsync()
    {
#if PLATFORM_WINDOWS
        if (IsZapretDownloading) return;
        IsZapretDownloading = true;
        ZapretStatus = IsRussian ? "Загрузка zapret..." : "Downloading zapret...";

        try
        {
            // Stop zapret if running
            if (ZapretEnabled || IsZapretRunning())
            {
                KillAllZapret();
                ZapretEnabled = false;
            }

            var updater = new VPNRouter.Core.Services.ZapretUpdater(_logger);
            updater.StatusChanged += s =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ZapretStatus = s);

            await updater.DownloadAndExtractAsync(System.Threading.CancellationToken.None);

            LoadZapretStrategies();

            ZapretStatus = IsRussian
                ? $"zapret {ZapretVersionText} установлен"
                : $"zapret {ZapretVersionText} installed";
        }
        catch (VPNRouter.Core.Services.ZapretDownloadException zex)
        {
            // Categorized error — use the already-human-readable message directly
            // instead of wrapping with "Download error:" prefix (which adds noise).
            _logger.Warning("[VM] Zapret download failed: {Category} {Msg}", zex.Category, zex.Message);
            ZapretStatus = FormatZapretError(zex);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret download failed (uncategorized)");
            ZapretStatus = IsRussian
                ? $"Ошибка загрузки: {ex.Message}"
                : $"Download error: {ex.Message}";
        }
        finally
        {
            IsZapretDownloading = false;
        }
#endif
    }

#if PLATFORM_WINDOWS
    /// <summary>Translate categorized Zapret errors to localized, actionable user messages.</summary>
    private string FormatZapretError(VPNRouter.Core.Services.ZapretDownloadException zex)
    {
        return zex.Category switch
        {
            VPNRouter.Core.Services.ZapretErrorCategory.Concurrent => IsRussian
                ? "Загрузка уже идёт — дождитесь завершения."
                : "Download already in progress — wait for it to finish.",
            VPNRouter.Core.Services.ZapretErrorCategory.GitHubRateLimit => IsRussian
                ? "GitHub временно ограничил запросы. Попробуйте через ~15 минут."
                : "GitHub rate-limited us. Try again in ~15 minutes.",
            VPNRouter.Core.Services.ZapretErrorCategory.GitHubServerError => IsRussian
                ? "GitHub недоступен. Повторите попытку через минуту."
                : "GitHub is temporarily down. Try again in a minute.",
            VPNRouter.Core.Services.ZapretErrorCategory.Network => IsRussian
                ? $"Сбой сети: {zex.Message}"
                : zex.Message,
            VPNRouter.Core.Services.ZapretErrorCategory.Corrupted => IsRussian
                ? "Скачанный файл повреждён. Нажмите «Скачать» ещё раз."
                : "Downloaded file is corrupted. Click Download to retry.",
            VPNRouter.Core.Services.ZapretErrorCategory.Invalid => IsRussian
                ? $"Формат релиза изменился: {zex.Message}"
                : zex.Message,
            VPNRouter.Core.Services.ZapretErrorCategory.FileSystem => IsRussian
                ? $"Ошибка файловой системы: {zex.Message}"
                : zex.Message,
            _ => IsRussian
                ? $"Ошибка: {zex.Message}"
                : $"Error: {zex.Message}",
        };
    }
#endif

    [RelayCommand]
    private async Task ToggleZapretAsync()
    {
#if PLATFORM_WINDOWS
        // If any winws process running → stop ALL
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretStatus = IsRussian ? "Остановлен" : "Stopped";
            SaveSettings();
            return;
        }

        // Auto-download if not installed
        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            await UpdateZapretAsync();
            if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
        }

        try
        {
            _zapret ??= new ZapretManager(_logger);
            var strategyName = ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
                ? ZapretStrategies[ZapretStrategyIndex] : "multisplit";

            if (strategyName == "custom")
            {
                _zapret.Start(ZapretCustomArgs);
            }
            else if (strategyName == "multisplit" || strategyName == "fake+multisplit")
            {
                _zapret.Start(ZapretManager.BuildLegacyArgs(strategyName));
            }
            else
            {
                var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategyName);
                if (parsed == null)
                {
                    ZapretStatus = $"Strategy not found: {strategyName}";
                    return;
                }
                // Prefer the original .bat file — it runs Flowseal's prologue
                // (service.bat load_user_lists, etc.) which is required for winws.exe.
                // Silent wrapper: same prologue + winws.exe run directly (no `start`),
                // so it inherits hidden parent window instead of appearing in taskbar.
                if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                    _zapret.StartFromBat(parsed.BatPath, parsed.Arguments);
                else
                    _zapret.Start(parsed.Arguments);
            }

            // Verify winws actually started (bat wrapper exits fast; check winws by name)
            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (_zapret.IsRunning || winwsPid != null)
            {
                ZapretEnabled = true;
                var pid = winwsPid ?? _zapret.Pid;
                ZapretStatus = IsRussian
                    ? $"Работает [{strategyName}] (PID {pid})"
                    : $"Running [{strategyName}] (PID {pid})";
            }
            else
            {
                ZapretEnabled = false;
                ZapretStatus = IsRussian
                    ? "Ошибка: winws.exe завершился сразу. Проверьте стратегию."
                    : "Error: winws.exe exited immediately. Check strategy.";
            }
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret start failed");
            ZapretStatus = $"Error: {ex.Message}";
            ZapretEnabled = false;
        }
#endif
    }

    // ── Zapret tools (diagnostics, Discord cache, hosts, service menu) ──

    [ObservableProperty] private bool _isZapretActionRunning;
    [ObservableProperty] private string _zapretActionTitle = string.Empty;
    public ObservableCollection<string> ZapretActionOutput { get; } = new();

    [RelayCommand]
    private async Task RunZapretDiagnosticsAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.RunDiagnostics,
            ct => ZapretActions.RunDiagnosticsAsync(ct));
#endif
    }

    [RelayCommand]
    private async Task ClearDiscordCacheAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.ClearDiscordCache,
            ct => ZapretActions.ClearDiscordCacheAsync(ct));
#endif
    }

    [RelayCommand]
    private async Task UpdateZapretHostsAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.UpdateHostsFile,
            ct => ZapretActions.UpdateHostsAsync(ct));
#endif
    }

    [RelayCommand]
    private void OpenZapretServiceMenu()
    {
#if PLATFORM_WINDOWS
        try { ZapretActions.OpenServiceMenu(); }
        catch (Exception ex) { _logger.Error(ex, "[VM] OpenServiceMenu failed"); }
#endif
    }

    private async Task RunZapretActionAsync(string title,
        Func<CancellationToken, IAsyncEnumerable<string>> action)
    {
        if (IsZapretActionRunning) return;
        IsZapretActionRunning = true;
        ZapretActionTitle = title;
        ZapretActionOutput.Clear();
        try
        {
            // Stream enumeration on background thread — sub-processes (sc, netsh)
            // should not block UI thread.
            await Task.Run(async () =>
            {
                await foreach (var line in action(CancellationToken.None))
                {
                    var captured = line;
                    await Dispatcher.UIThread.InvokeAsync(() => ZapretActionOutput.Add(captured));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret action failed");
            await Dispatcher.UIThread.InvokeAsync(() => ZapretActionOutput.Add($"ERROR: {ex.Message}"));
        }
        finally { IsZapretActionRunning = false; }
    }

    [RelayCommand]
    private async Task ToggleFlowsealHostsAsync()
    {
#if PLATFORM_WINDOWS
        try
        {
            if (FlowsealHostsInstalled)
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.UninstallFlowseal(_logger);
                FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();
                ZapretStatus = ok ? (IsRussian ? "Flowseal hosts удалены" : "Flowseal hosts removed") : msg;
            }
            else
            {
                var (ok, msg) = await VPNRouter.Core.Services.HostsManager.InstallFlowsealAsync(_logger);
                FlowsealHostsInstalled = VPNRouter.Core.Services.HostsManager.IsFlowsealInstalled();
                ZapretStatus = ok ? msg : msg;
            }
        }
        catch (Exception ex) { ZapretStatus = $"Error: {ex.Message}"; }
#endif
    }

    [RelayCommand]
    private async Task UpdateIpSetListAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(IsRussian ? "Обновить IPSet" : "Update IPSet list",
            ct => ZapretActions.UpdateIpSetListAsync(ct));
        // Refresh IpSetModeIndex after update (list content may have changed)
        IpSetModeIndex = (int)ZapretActions.GetIpSetMode();
#endif
    }

    [RelayCommand]
    private void RunZapretTests()
    {
#if PLATFORM_WINDOWS
        try { ZapretActions.RunTests(); }
        catch (Exception ex) { _logger.Error(ex, "[VM] RunTests"); }
#endif
    }

    [RelayCommand]
    private async Task RemoveZapretServiceAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(IsRussian ? "Удалить службу zapret" : "Remove zapret service",
            ct => ZapretActions.RemoveZapretServiceAsync(ct));
#endif
    }

#if PLATFORM_WINDOWS
    partial void OnGameFilterModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetGameFilterMode((ZapretActions.GameFilterMode)value);
    }

    partial void OnIpSetModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetIpSetMode((ZapretActions.IpSetMode)value);
    }

    partial void OnZapretAutoUpdateCheckChanged(bool value)
    {
        if (_isLoadingUI) return;
        ZapretActions.SetAutoUpdateCheck(value);
    }
#endif

    [RelayCommand]
    private void ToggleDiscordHosts()
    {
#if PLATFORM_WINDOWS
        try
        {
            if (DiscordHostsInstalled)
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.Uninstall(_logger);
                DiscordHostsInstalled = !ok || VPNRouter.Core.Services.HostsManager.IsInstalled();
                ZapretStatus = ok ? (IsRussian ? "Discord hosts удалены" : "Discord hosts removed")
                                  : msg;
            }
            else
            {
                var (ok, msg) = VPNRouter.Core.Services.HostsManager.Install(_logger);
                DiscordHostsInstalled = VPNRouter.Core.Services.HostsManager.IsInstalled();
                ZapretStatus = ok ? (IsRussian ? "Discord hosts добавлены (200 серверов)" : "Discord hosts added (200 servers)")
                                  : msg;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Discord hosts toggle failed");
            ZapretStatus = $"Hosts error: {ex.Message}";
        }
#endif
    }

    // ── Telegram proxy commands ──

    [RelayCommand]
    private async Task UpdateTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;
        IsTgProxyDownloading = true;
        TgProxyStatus = IsRussian ? "Загрузка tg-ws-proxy..." : "Downloading tg-ws-proxy...";

        try
        {
            // Stop if running
            if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                _tgProxy?.Stop();
                TgProxyManager.KillAll(TgProxyPort);
                TgProxyEnabled = false;
            }

            var updater = new TgProxyUpdater(_logger);
            updater.StatusChanged += s =>
                Dispatcher.UIThread.Post(() => TgProxyStatus = s);

            await updater.DownloadAsync(CancellationToken.None);

            TgProxyVersionText = TgProxyUpdater.GetLocalVersion() ?? "?";
            TgProxyStatus = IsRussian
                ? $"tg-ws-proxy {TgProxyVersionText} установлен"
                : $"tg-ws-proxy {TgProxyVersionText} installed";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy download failed");
            TgProxyStatus = $"Download error: {ex.Message}";
        }
        finally
        {
            IsTgProxyDownloading = false;
        }
#endif
    }

    [RelayCommand]
    private async Task ToggleTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        // If running → stop
        if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            _tgProxy?.Stop();
            // v2.20.0: pass the port so KillByPort hits the actual
            // python.exe running the proxy (process-name match never
            // worked — see TgProxyManager.KillAll).
            TgProxyManager.KillAll(TgProxyPort);
            // Re-check a beat later; if the port is still bound
            // something couldn't be killed (permissions? zombie?).
            // We surface the truth instead of lying that we stopped.
            await Task.Delay(300);
            TgProxyRuntimeStatus = TgProxyManager.IsAnyRunning(TgProxyPort)
                ? ComponentRuntimeStatus.Failed
                : ComponentRuntimeStatus.Idle;
            TgProxyEnabled = false;
            TgProxyStatus = TgProxyRuntimeStatus == ComponentRuntimeStatus.Failed
                ? (IsRussian ? "Не удалось остановить (проверьте права)" : "Couldn't stop (check permissions)")
                : (IsRussian ? "Остановлен" : "Stopped");
            TgProxyStats = "";
            SaveSettings();
            return;
        }

        // v2.31.10: Service-side AutostartTgProxyAsync logs entry/decision
        // breadcrumbs with the same shape as below. When the App-side
        // AutostartTgProxyAsync from the DBG-2 sister task lands, lift this
        // structured log pattern verbatim (entry → IsInstalled(_logger) →
        // secret-len + port → ResilientStarter → outcome) so manual-start
        // logs and autostart logs share grep'able prefixes.
        // TODO(DBG-2 sister): once VPNRouter.App has its own
        // AutostartTgProxyAsync, mirror the [Service] AutostartTgProxyAsync
        // entry/decision logs in VPNRouterService.cs:331+ exactly.
        _logger.Information("[VM] ToggleTgProxyAsync: start path entered");

        // Auto-download if not installed
        if (!TgProxyUpdater.IsInstalled(_logger))
        {
            await UpdateTgProxyAsync();
            if (!TgProxyUpdater.IsInstalled(_logger)) return;
        }

        try
        {
            // Generate secret if empty
            if (string.IsNullOrWhiteSpace(TgProxySecret))
            {
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
            }

            _logger.Information(
                "[VM] ToggleTgProxyAsync: secret configured (len {SecretLen}), port {Port}, calling TgProxyManager.Start",
                TgProxySecret.Length, TgProxyPort);

            _tgProxy ??= new TgProxyManager(_logger);
            _tgProxy.StatsUpdated += stats =>
                Dispatcher.UIThread.Post(() => TgProxyStats = ParseStatsShort(stats));
            _tgProxy.Start(TgProxyPort, TgProxySecret);

            // Verify it actually started
            await Task.Delay(2000);
            if (_tgProxy.IsRunning || TgProxyManager.IsAnyRunning(TgProxyPort))
            {
                TgProxyEnabled = true;
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
                TgProxyStatus = IsRussian
                    ? $"Работает (PID {_tgProxy.Pid})"
                    : $"Running (PID {_tgProxy.Pid})";
            }
            else
            {
                TgProxyEnabled = false;
                TgProxyStatus = IsRussian
                    ? "Ошибка: tg-ws-proxy завершился сразу."
                    : "Error: tg-ws-proxy exited immediately.";
            }
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy start failed");
            TgProxyStatus = $"Error: {ex.Message}";
            TgProxyEnabled = false;
        }
#endif
    }

    [RelayCommand]
    private void CopyTgProxyLink()
    {
        if (string.IsNullOrEmpty(TgProxyLink)) return;
        CopyToClipboard(TgProxyLink);
        // v2.31.6-r4 (BUG #3 fix): don't overwrite TgProxyStatus.
        // Pre-r4 we set it to "Copied!" which persistently shadowed
        // the real status (Stopped / Running / Error) until the next
        // status-mutating event. Computer-use audit on r2/r3 confirmed
        // the field never auto-reverted, so user saw stale "Copied!"
        // 30 minutes after click. The clipboard side-effect is its
        // own feedback channel; we trust users to know the click
        // landed without us hijacking the status banner.
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void OpenTgProxyInTelegram()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;

        // v2.31.6-r4 (BUG #1 fix): if no app is registered for the
        // tg:// URI scheme (Windows shows "We can't open this 'tg' link"
        // dialog), Telegram desktop is missing. Surface the cause
        // directly instead of letting the OS dialog do the talking,
        // and offer the canonical download link.
        if (!TgProxyManager.IsTelegramSchemeRegistered())
        {
            ShowTgProxyToast(IsRussian
                ? "Telegram не установлен — скачай с desktop.telegram.org"
                : "Telegram not installed — download from desktop.telegram.org");
            return;
        }

        TgProxyManager.OpenInTelegram("127.0.0.1", TgProxyPort, TgProxySecret);
    }

    /// <summary>
    /// v2.31.6-r1 (TelegramPage UX simplification): one-click
    /// onboarding for Telegram proxy. Wraps the three things a
    /// first-time user needs into a single CTA:
    ///   1. Download the tg-ws-proxy binary if not already installed.
    ///   2. Start the proxy (which auto-generates a secret if empty).
    ///   3. Open Telegram with the deep-link so the client adds the
    ///      proxy to its Settings → Advanced → Connection type list.
    /// On subsequent visits <see cref="IsTgProxySetUp"/> flips to
    /// true and the page swaps to the simpler Connect/Disconnect
    /// surface — at which point this command is no longer reachable
    /// from the UI but stays callable defensively.
    /// </summary>
    [RelayCommand]
    private async Task SetupTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        // Step 1+2: ToggleTgProxyAsync handles "download → generate
        // secret → start" already. Re-using it keeps the start path
        // single-sourced and avoids drift if the toggle logic
        // evolves later (port retry, secret rotation policy, etc.).
        if (!TgProxyEnabled && !TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            await ToggleTgProxyAsync();
        }

        // Step 3: open Telegram with the deep-link. Skip if the
        // start above failed for some reason (no binary, port
        // collision, etc.) — Status text already explains why.
        // v2.31.6-r5: route through OpenTgProxyInTelegram (the command
        // body, not the relay wrapper) so the BUG #1 toast guard for
        // missing Telegram desktop fires here too. Pre-r5 this branch
        // called TgProxyManager.OpenInTelegram directly and bypassed
        // the registry probe — first-time Linux/macOS-style users
        // without Telegram desktop saw the OS dialog instead of the
        // download-link toast.
        if (TgProxyEnabled && !string.IsNullOrEmpty(TgProxySecret))
        {
            OpenTgProxyInTelegram();
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// v2.31.6-r5 (TG-2): unified main-action command wired to the
    /// TelegramPage footer button. Branches on current state:
    /// <list type="bullet">
    ///   <item>Stopped → fires <see cref="SetupTgProxyAsync"/> (download
    ///     binary if needed, start the proxy, open Telegram with
    ///     deep-link to auto-add the entry — single click).</item>
    ///   <item>Running → fires <see cref="ToggleTgProxyAsync"/> which
    ///     stops the proxy.</item>
    /// </list>
    /// User feedback 2026-05-03 night surfaced that the pre-r5 layout
    /// had two visually distant buttons (body «Open in Telegram» +
    /// footer «Start Telegram proxy») that conceptually belonged
    /// together on first run. Folding the start+open chain into the
    /// footer, demoting the body button to a secondary «re-pair»
    /// fallback, removes the «click body, then click footer» two-step
    /// without competing visually with the global Start VPN footer
    /// (per v2.25.6 design intent — footer keeps its secondary style).
    /// </summary>
    [RelayCommand]
    private async Task TgProxyMainActionAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        if (TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort))
        {
            await ToggleTgProxyAsync();
        }
        else
        {
            await SetupTgProxyAsync();
        }
#else
        await Task.CompletedTask;
#endif
    }

    [RelayCommand]
    private void OpenTgProxyFolder()
    {
        OpenFolderInExplorer(TgProxyUpdater.TgProxyDir);
    }

    [RelayCommand]
    private void OpenTgProxyGitHub()
    {
        OpenUrl("https://github.com/Flowseal/tg-ws-proxy");
    }

    [RelayCommand]
    private void OpenZapretFolder()
    {
        OpenFolderInExplorer(ZapretUpdater.ZapretDir);
    }

    [RelayCommand]
    private void OpenZapretGitHub()
    {
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    }

    private static void OpenFolderInExplorer(string path)
    {
        // v2.31.6-r11: Debug-log instead of swallowing silently. Iter#4
        // audit P2: user-action paths (Open folder / Open URL / Copy to
        // clipboard) shouldn't fail invisibly — add at least a Debug
        // line so postmortem from logs is possible. We don't escalate
        // to Warning because the failure modes are usually benign
        // (folder doesn't exist, no shell associated with the URL).
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "[VM] OpenFolderInExplorer failed: {Path}", path);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "[VM] OpenUrl failed: {Url}", url);
        }
    }

    [RelayCommand]
    private void CopyTgProxySecret()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;
        CopyToClipboard(TgProxySecret);
        // v2.31.6-r4 (BUG #3): toast not status — see CopyTgProxyLink.
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void RegenerateTgProxySecret()
    {
        var wasRunning = TgProxyEnabled || TgProxyManager.IsAnyRunning(TgProxyPort);

        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
        TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        SaveSettings();

        // v2.31.6-r4 (BUG #4 fix): if the proxy was running when the
        // secret got rotated, the existing Telegram client connection
        // is now using a stale secret and will silently keep failing
        // until the user restarts the proxy AND re-pairs Telegram.
        // Make this consequence explicit instead of silent.
        if (wasRunning)
        {
            ShowTgProxyToast(IsRussian
                ? "Новый secret — перезапусти proxy и Telegram client"
                : "New secret — restart proxy and re-pair Telegram client");
        }
        else
        {
            ShowTgProxyToast(IsRussian ? "Новый secret сгенерирован" : "New secret generated");
        }
    }

    /// <summary>
    /// v2.31.6-r4: transient toast surface for TgProxy actions that
    /// pre-r4 hijacked TgProxyStatus (Copied! / Installed v… / similar).
    /// Sets <see cref="TgProxyToast"/>, schedules a 2500 ms revert,
    /// and bails the revert if a newer toast races in. Page binds
    /// the toast separately from the status banner so the runtime
    /// status (Stopped / Running / Error) is never shadowed by
    /// a transient confirmation.
    /// </summary>
    private void ShowTgProxyToast(string message)
    {
        TgProxyToast = message;
        var token = ++_tgProxyToastToken;
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            // Only clear if no newer toast has fired in the meantime.
            if (token == _tgProxyToastToken)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (token == _tgProxyToastToken) TgProxyToast = string.Empty;
                });
            }
        });
    }

    private int _tgProxyToastToken;

    private void CopyToClipboard(string text)
    {
        // v2.31.6-r12: Debug-log instead of swallowing silently. Iter#4
        // audit P2: clipboard failures (no clipboard service available
        // in headless test, app exited mid-copy, etc.) should leave a
        // forensic trace.
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Clipboard?.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] CopyToClipboard failed (text length: {Len})", text?.Length ?? 0);
        }
    }

    /// <summary>Parse stats line into short summary for UI display.</summary>
    private static string ParseStatsShort(string statsLine)
    {
        // Input: "stats: total=10 active=2 ws=8 tcp_fb=1 cf=0 bad=1 ..."
        var parts = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(statsLine, @"(\w+)=(\S+)"))
        {
            parts[m.Groups[1].Value] = m.Groups[2].Value;
        }

        parts.TryGetValue("active", out var active);
        parts.TryGetValue("total", out var total);
        parts.TryGetValue("up", out var up);
        parts.TryGetValue("down", out var down);

        var sb = new System.Text.StringBuilder();
        if (active != null) sb.Append($"Active: {active}");
        if (total != null) sb.Append($" | Total: {total}");
        if (up != null) sb.Append($" | \u2191{up}");
        if (down != null) sb.Append($" \u2193{down}");
        return sb.ToString();
    }

    [RelayCommand]
    private void ClearSubscription()
    {
        SubscriptionServers.Clear();
        SubscriptionUrl = string.Empty;
        SelectedSubscriptionServer = null;
        SaveSettings();
        StatusText = IsRussian ? "Подписка удалена" : "Subscription cleared";
    }

    [RelayCommand]
    private void AddServer()
    {
        var lines = VlessUri?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        foreach (var line in lines)
        {
            // v2.30.1-r3: dispatch by scheme via ServerUriParser instead
            // of hard-coded vless:// prefix check. Pasting Hysteria2 /
            // TUIC / Shadowsocks links lands in the same Servers list.
            if (!ServerUriParser.IsSupportedScheme(line))
                continue;

            try
            {
                var entry = ServerUriParser.Parse(line);
                // Check duplicate by name (same IP+port with different name/uuid is OK)
                if (Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server))
                    continue;
                Servers.Add(new ServerViewModel(entry));
                SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse server URI: {Line}", line);
            }
        }

        VlessUri = string.Empty;
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer != null)
            Servers.Remove(SelectedServer);
    }

    /// <summary>
    /// Per-row delete (the × button on each VLESS server row). Removes
    /// the specific entry passed as the parameter without changing
    /// selection — clicking the × on row N must NOT trigger
    /// OnSelectedServerChanged (which would auto-reconnect to row N
    /// when VPN is running). v2.30.1-r3 fix: user reported "при каждом
    /// клике на другие конфиги для удаления, оно запускались, так как
    /// я на них кликал, только потом я их удалял".
    /// </summary>
    [RelayCommand]
    private void RemoveServerByEntry(ServerViewModel? entry)
    {
        if (entry == null) return;
        // Don't change SelectedServer — the row's × button removes the
        // row directly. If the entry being removed is the active one,
        // clear SelectedServer too so the now-empty radio doesn't
        // dangle on a freed row.
        var wasSelected = ReferenceEquals(SelectedServer, entry);
        Servers.Remove(entry);
        if (wasSelected)
            SelectedServer = Servers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task AddCustomConfigAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                _logger.Warning("[VM] AddCustomConfig: MainWindow not found");
                StatusText = IsRussian ? "Не удалось открыть диалог выбора файла" : "Failed to open file picker";
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.SelectSingBoxConfig,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
                }
            });

            if (files.Count == 0) return;

            var file = files[0];
            var sourcePath = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(sourcePath)) return;

            var configName = Path.GetFileNameWithoutExtension(sourcePath);

            // Check duplicate
            if (CustomConfigs.Any(c => c.Name.Equals(configName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = Strings.ConfigExists(configName);
                return;
            }

            // Validate
            var json = await File.ReadAllTextAsync(sourcePath);
            var (isValid, errors) = CustomConfigInjector.Validate(json);
            if (!isValid)
            {
                StatusText = $"{Strings.InvalidConfig} {string.Join("; ", errors)}";
                return;
            }

            // Copy to app support
            var destPath = CustomConfigInjector.CopyToProgramData(sourcePath, configName);
            var entry = new CustomConfigEntry { Name = configName, Path = destPath };

            var isFirst = CustomConfigs.Count == 0;
            var vm = new CustomConfigViewModel(entry, isFirst);
            CustomConfigs.Add(vm);

            // Auto-select and save
            SelectedCustomConfig = vm;
            SaveSettings();
            StatusText = IsRussian
                ? $"Конфиг \"{configName}\" добавлен" + (isFirst ? " и активирован" : "")
                : $"Config \"{configName}\" added" + (isFirst ? " and activated" : "");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] AddCustomConfig failed");
            StatusText = IsRussian
                ? $"Ошибка добавления конфига: {ex.Message}"
                : $"Failed to add config: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveCustomConfig()
    {
        if (SelectedCustomConfig == null) return;
        var name = SelectedCustomConfig.Name;
        var wasActive = SelectedCustomConfig.IsActive;
        CustomConfigs.Remove(SelectedCustomConfig);

        // If removed the active one, activate the first remaining
        if (wasActive && CustomConfigs.Count > 0)
        {
            CustomConfigs[0].IsActive = true;
            SelectedCustomConfig = CustomConfigs[0];
        }

        SaveSettings();
        StatusText = IsRussian ? $"Конфиг \"{name}\" удалён" : $"Config \"{name}\" removed";
    }

    [RelayCommand]
    private void SetActiveCustomConfig(CustomConfigViewModel? config)
    {
        if (config == null) return;
        foreach (var c in CustomConfigs)
            c.IsActive = false;
        config.IsActive = true;
        SaveSettings();
    }

    private bool _isReconnecting;

    /// <summary>
    /// v2.30.2-r1: tells <see cref="ReconnectAsync"/> which mode the
    /// reconnect is FOR. The legacy single-arg call defaulted to "follow
    /// VM flags", which could leave ConfigMode stuck on "subscribe" if
    /// the user clicked a manual VLESS row after sub-tab peeking. The
    /// explicit hint lets the reconnect path force the correct mode
    /// regardless of stale flag state.
    /// </summary>
    private enum ReconnectIntent
    {
        /// <summary>Follow VM flags (legacy behaviour).</summary>
        Follow,
        /// <summary>User clicked a manual VLESS server in the Servers list.</summary>
        ManualVless,
        /// <summary>User clicked a subscription server in the Subscriptions tab.</summary>
        Subscription,
        /// <summary>User clicked a custom config in the Custom sub-tab.</summary>
        CustomConfig
    }

    // Subscribe: selecting a subscription server = choosing which to route through.
    partial void OnSelectedSubscriptionServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;
        // v2.30.2-r1 diag: trace every subscription-row selection.
        _logger?.Information(
            "[VM] OnSelectedSubscriptionServerChanged name={N} ip={Ip} IsConnected={C} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsSubscribeMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.Subscription);
        }
    }

    // VLESS: selecting a server = choosing which server to route through.
    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;

        // v2.30.2-r1 diag: trace every manual-row selection.
        _logger?.Information(
            "[VM] OnSelectedServerChanged name={N} ip={Ip} IsConnected={C} IsVlessMode={V} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsVlessMode, IsSubscribeMode, IsConnecting);
        // If connected in VLESS mode → reconnect with newly selected server
        if (IsConnected && IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.ManualVless);
        }
    }

    // Auto-activate config when selected in the list (left-click = switch).
    // If VPN is already running, auto-reconnect with the new config.
    partial void OnSelectedCustomConfigChanged(CustomConfigViewModel? value)
    {
        if (_isLoadingUI || value == null) return;
        if (value.IsActive) return; // already active, no-op
        if (_isReconnecting) return; // don't re-enter during reconnect

        SetActiveCustomConfig(value);

        // If connected in custom mode → reconnect with new config
        if (IsConnected && !IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.Name); return; }
            _ = ReconnectAsync(value.Name, ReconnectIntent.CustomConfig);
        }
    }

    /// <summary>
    /// Service-managed VPN can't be reconnected from the app — the local
    /// engine doesn't own the sing-box process, so Stop() is a no-op and
    /// StartAsync() would fight TUN ownership. We still save the new
    /// selection to config.yaml so the next Stop+Start cycle picks it up,
    /// and we surface a clear message so the user isn't confused about
    /// why the connection didn't switch.
    /// </summary>
    private void WarnServiceManagedReconnect(string newServerName)
    {
        try { SaveSettings(); } catch { }
        StatusText = IsRussian
            ? $"Выбран {newServerName}. VPN управляется службой — остановите и запустите VPN, чтобы переключиться."
            : $"Selected {newServerName}. VPN is managed by the service — Stop and Start VPN to switch.";
        _logger.Information("[VM] Service-managed VPN: selection '{Name}' saved; user must Stop+Start to apply", newServerName);
    }

    private async Task ReconnectAsync(string configName, ReconnectIntent intent = ReconnectIntent.Follow)
    {
        if (_isReconnecting) return;
        _isReconnecting = true;
        IsConnecting = true;
        StatusText = IsRussian
            ? $"Переключение на {configName}..."
            : $"Switching to {configName}...";

        // v2.30.2-r1 diag: log every reconnect with full context so the
        // next repro distinguishes a "should-be-manual but is-subscribe"
        // vs other ordering bugs.
        _logger?.Information(
            "[VM] ReconnectAsync target={Target} intent={Intent} ConfigMode={CM} IsVlessMode={V} IsSubscribeMode={S}",
            configName, intent,
            _settings.App.ConfigMode, IsVlessMode, IsSubscribeMode);

        try
        {
            // Stop current VPN
            await Task.Run(() => _engine.Stop());

            // v2.30.2-r1 Bug 2C fix: when the user explicitly clicked a
            // manual VLESS row, force the VM flags to manual mode BEFORE
            // SaveSettings so the on-disk ConfigMode persists as
            // "generated" — even if a subscription is enabled (which the
            // r2 guard would otherwise prefer to keep as "subscribe").
            // The r2 guard's purpose is to defend against accidental
            // sub-tab "peeks"; an explicit server-row click is NOT a peek.
            if (intent == ReconnectIntent.ManualVless)
            {
                IsSubscribeMode = false;
                IsVlessMode = true;
            }
            else if (intent == ReconnectIntent.Subscription)
            {
                IsSubscribeMode = true;
                IsVlessMode = false;
            }
            else if (intent == ReconnectIntent.CustomConfig)
            {
                IsSubscribeMode = false;
                IsVlessMode = false;
            }

            // Save + reload settings with the new active config
            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            // v2.30.2-r1 diag: log effective settings after Save+Reload
            // so the engine-side decision is auditable from the VM log.
            _logger?.Information(
                "[VM] ReconnectAsync after Save+Reload: ConfigMode={CM} VlessActive={VA} SubActive={SA} VlessServers={N}",
                _settings.App.ConfigMode,
                _settings.Vless.ActiveServer,
                _settings.App.ActiveSubscriptionServer,
                _settings.Vless.Servers?.Count ?? 0);

            // Subscribe mode: aggregate enabled subscriptions → feed into engine
            var aggregated = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();

            // v2.30.2-r1 Bug 2C fix: branch on caller intent, not just on
            // VM flag state. ManualVless overrides any subscription
            // pollution that may have leaked into _settings.Vless.Servers
            // from a prior reconnect cycle.
            if (intent == ReconnectIntent.ManualVless)
            {
                _settings.App.ConfigMode = "generated";
                _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
                _settings.Vless.ActiveServer = configName;
                _logger?.Information(
                    "[VM] ReconnectAsync.ManualVless: forced ConfigMode=generated, Vless.Servers={N}, ActiveServer={A}",
                    _settings.Vless.Servers.Count, configName);
            }
            else if ((intent == ReconnectIntent.Subscription || (intent == ReconnectIntent.Follow && IsSubscribeMode))
                     && aggregated.Count > 0)
            {
                _settings.Vless.Servers = aggregated;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                // v2.30.2-r2 Bug 2A fix: do NOT force ConfigMode=generated
                // here. The legacy code did this so VlessServersResolver
                // wouldn't re-aggregate (since we already did). But it
                // also broke RefreshActiveIndicator's ConfigMode gate —
                // with ConfigMode=generated the indicator loop only paints
                // the manual Servers list, leaving the Subscriptions list
                // dot dark even after a successful subscribe-mode connect.
                // User report 2026-05-01:
                // «Зеленый кружочек в подписках не появляеться, хотя к
                //  кликнутому конфигу есть подключение».
                //
                // Keeping ConfigMode="subscribe" is harmless to the engine:
                // VlessServersResolver re-aggregates idempotently (same
                // content as we just wrote into Vless.Servers), and the
                // engine reads Vless.Servers + Vless.ActiveServer the same
                // way regardless of ConfigMode. RefreshActiveIndicator can
                // now correctly identify the active subscription row.
                _logger?.Information(
                    "[VM] ReconnectAsync.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregated.Count, _settings.Vless.ActiveServer);
            }

            // Start with new config. Retry up to 3 times because Windows Service
            // may briefly grab the TUN lock between our Stop and Start.
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await Task.Run(() => _engine.StartAsync(_settings, cts.Token), cts.Token);
                    break; // success
                }
                catch (TunOwnershipException) when (attempt < maxRetries)
                {
                    _logger.Warning("[VM] Reconnect: TUN lock stolen by service, retry {A}/{M}", attempt, maxRetries);
                    await Task.Delay(2000); // wait for service to release
                }
            }

            // v2.30.2-r1 Bug 2A fix: refresh the active-row indicator
            // after the engine has actually settled on a new ActiveServer.
            // The legacy flow relied on RefreshActiveIndicator firing from
            // some other status callback, but the timing was racy after a
            // subscription→subscription click chain — the green dot would
            // stay on the old row (or vanish entirely). Forcing a refresh
            // here, with the just-applied _settings, makes the UI match
            // the engine's view.
            try { RefreshActiveIndicator(); }
            catch (Exception ex) { _logger?.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
        }
        catch (OperationCanceledException)
        {
            _logger.Error("[VM] Reconnect timed out");
            try { await Task.Run(() => _engine.Stop()); } catch { }
            IsConnected = false;
            StatusText = IsRussian
                ? "Таймаут переключения. Попробуйте снова."
                : "Switch timed out. Try again.";
            ConnectButtonText = Strings.StartVPN;
        }
        catch (TunOwnershipException)
        {
            IsConnected = false;
            StatusText = IsRussian
                ? "VPN адаптер занят другим экземпляром"
                : "TUN adapter owned by another instance";
            ConnectButtonText = Strings.StartVPN;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Reconnect failed");
            IsConnected = false;
            StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
            ConnectButtonText = Strings.StartVPN;
        }
        finally
        {
            IsConnecting = false;
            _isReconnecting = false;
        }
    }

    [ObservableProperty] private string _newCategoryName = string.Empty;

    [RelayCommand]
    private void AddCategory()
    {
        var name = NewCategoryName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (AppGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        var group = new AppGroupViewModel(name!, "", isChecked: true) { IsCustomCategory = true };
        AppGroups.Add(group);
        SelectedAppGroup = group;
        NewCategoryName = string.Empty;
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveCategory(AppGroupViewModel? group)
    {
        if (group == null || !group.IsCustomCategory) return;
        AppGroups.Remove(group);
        if (SelectedAppGroup == group)
            SelectedAppGroup = AppGroups.FirstOrDefault();
        SaveSettings();
    }

    [RelayCommand]
    private void AddCustomApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var name = StripExe(processName.Trim());
        var target = SelectedAppGroup;

        // Fallback: if no group selected, use "Custom Apps"
        if (target == null)
        {
            target = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
            if (target == null)
            {
                target = new AppGroupViewModel("Custom Apps", "Your custom applications", true) { IsCustomGroup = true };
                AppGroups.Add(target);
            }
        }

        if (target.Apps.Any(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        target.Apps.Add(new AppItemViewModel(name, true, isCustom: true));
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveCustomApps()
    {
        var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (customGroup == null) return;

        var toRemove = customGroup.Apps.Where(a => a.IsChecked).ToList();
        foreach (var app in toRemove)
            customGroup.Apps.Remove(app);
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveCustomApp(AppItemViewModel? app)
    {
        if (app == null) return;
        // Search ALL groups — user can add custom apps to any group now
        foreach (var group in AppGroups)
        {
            if (group.Apps.Remove(app))
            {
                SaveSettings();
                return;
            }
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        // v2.17.10: log entry so bug reports about the window teleporting
        // can be traced to the exact toggle that fired.
        _logger.Information("[VM] ToggleTheme → {Theme}", IsDarkTheme ? "Light" : "Dark");
        IsDarkTheme = !IsDarkTheme;
        ApplyTheme();
        RefreshLocalization();
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        // v2.25.12 — final fix for the "app freezes on language toggle"
        // report. The underlying cause was that XAML `{x:Static loc:Strings.*}`
        // markup extensions are evaluated exactly ONCE at parse time and
        // never re-read. Every earlier iteration worked around this by
        // rebuilding MainWindow from scratch on every language toggle,
        // which meant re-parsing 7 pages' worth of XAML synchronously on
        // the UI thread — a 200-500 ms hard freeze that the v2.25.11
        // Dispatcher-defer trick merely concealed rather than removed.
        //
        // Real fix (shipped in v2.25.12):
        //   1. Bulk-converted every `{x:Static loc:Strings.X}` → `{Binding L_X}`
        //      across all 10 .axaml files (229 references).
        //   2. Generated `MainWindowViewModel.Localization.cs` with 207
        //      `public string L_X => Strings.X;` proxies.
        //   3. `RefreshL10nProxies()` iterates those properties and fires
        //      PropertyChanged for each so every binding re-reads.
        // Result: toggling language now runs in ~5-10 ms — no rebuild,
        // no freeze, flyout dismisses normally. Old
        // `ReloadMainWindowForLocalization` helper kept around as
        // fallback but is no longer wired into the toggle path.
        _logger.Information("[VM] ToggleLanguage → {Lang}", IsRussian ? "en" : "ru");
        IsRussian = !IsRussian;
        Strings.Lang = IsRussian ? "ru" : "en";
        SaveSettings();
        RefreshLocalization();
        RefreshL10nProxies();   // broadcast PropertyChanged for every L_* + Lbl*
    }

    // v2.25.2 — explicit segment commands for the redesigned ⋯ menu popover
    // (Phase 1). The popover shows Theme as a Light|Dark segmented control
    // and Language as RU|EN — clicking an already-active segment must be a
    // no-op, whereas ToggleTheme/ToggleLanguage always flip. These wrappers
    // let the XAML bind each segment button to its own command without
    // having to compute "should I fire?" in the binding layer.
    [RelayCommand]
    private void SetThemeLight()
    {
        if (!IsDarkTheme) return;
        ToggleTheme();
    }

    [RelayCommand]
    private void SetThemeDark()
    {
        if (IsDarkTheme) return;
        ToggleTheme();
    }

    [RelayCommand]
    private void SetLanguageRussian()
    {
        if (IsRussian) return;
        ToggleLanguage();
    }

    [RelayCommand]
    private void SetLanguageEnglish()
    {
        if (!IsRussian) return;
        ToggleLanguage();
    }

    /// <summary>
    /// Flip the UI between the minimalist SimplePage and the full tabbed
    /// Advanced layout. Both views share the same ViewModel instance; the
    /// window only swaps which pane is visible, so VM state (servers,
    /// connection, Free Configs cache, etc.) survives the toggle.
    /// </summary>
    [RelayCommand]
    private void ToggleUiMode()
    {
        IsSimpleMode = !IsSimpleMode;
        _settings.App.UiMode = IsSimpleMode ? "simple" : "advanced";
        SaveSettings();
    }

    /// <summary>
    /// v2.27.0-r2: jump from Simple mode straight into Advanced →
    /// Network → Autostart. Originally Simple had its own "Start with
    /// Windows" checkbox bound to a computed property over three backing
    /// settings. User feedback: two parallel sources of truth was more
    /// confusing than useful, and getting them in sync needed extra code
    /// paths (Bug B / Bug D). Collapsing Simple down to a link-card keeps
    /// Simple actually simple — one path, one place to look — and the
    /// Advanced master checkbox (bound directly to ServiceVm.Autostart
    /// Checked again) is the only autostart surface. See plans/vpnrouter-
    /// v2.27-service-ux.md §4 and the companion user feedback loop.
    /// </summary>
    [RelayCommand]
    private void OpenAutostartSettings()
    {
        IsSimpleMode = false;
        _settings.App.UiMode = "advanced";
        SelectedTabIndex = 2;           // Network tab
        SelectedSettingsIndex = 5;      // Autostart sub-section (v2.30.0-r2: shifted +1 for Rules)
        SaveSettings();
    }

    /// <summary>
    /// v2.31.1-r1 (F-4 / UX-6): inline CTA used by the Autostart Section A
    /// when the boot-autostart checkboxes are greyed out because the Windows
    /// service isn't installed. Pre-fix the only path to install was scrolling
    /// up to the master toggle, which wasn't obvious. The button binds to this
    /// command which simply flips the master toggle — same code path as
    /// clicking it directly, just discoverable from where the user is looking.
    /// </summary>
    [RelayCommand]
    private void InstallServiceForAutostart()
    {
        if (ServiceVm.IsInstalled || ServiceVm.IsBusy) return;
        ServiceVm.AutostartChecked = true;
    }

    // v2.31.6-r9 — removed ReloadMainWindowForLocalization (~50 LOC).
    // Originally a workaround for Avalonia's {x:Static loc:Strings.*}
    // binding evaluation timing — bindings cached at XAML parse time
    // and didn't refresh on language toggle, so Free Configs page (~100
    // x:Static bindings) needed window rebuild. The newer locale-toggle
    // path (RefreshL10nProxies) reflects PropertyChanged across all L_*
    // proxy getters and made this rebuild redundant; the comment at
    // line 5108 explicitly noted "no longer wired into the toggle path".
    // Iter#4 audit confirmed zero callers (only definition + its own
    // catch-block log line referenced the symbol).

    [RelayCommand]
    private void ApplySettings()
    {
        SaveSettings();
        StatusText = IsRussian ? "Настройки сохранены" : "Settings saved";
    }

    [RelayCommand]
    private void ShowWindow()
    {
        var window = GetMainWindow();
        if (window != null)
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    [RelayCommand]
    private void Quit()
    {
        if (_engine.IsRunning)
            _engine.Stop();

        StopSubRefreshTimer();

        // Kill zapret on app exit
        KillAllZapret();

        // Kill tg-ws-proxy on app exit
#if PLATFORM_WINDOWS
        // v2.31.6-r12: Debug-log instead of swallowing silently.
        try { _tgProxy?.Stop(); TgProxyManager.KillAll(TgProxyPort); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Quit: _tgProxy.Stop / KillAll failed"); }
#endif

        SaveSettings();

        // v2.31.6-r12 (Phase H): release IDisposable resources so a
        // subsequent app reopen / future ReloadMainWindowForLocalization
        // doesn't leak this VM's timers / event subscriptions.
        Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>
    /// v2.31.6-r12 (Phase H, iter#4 audit): IDisposable surface for the
    /// VM. Ensures both timers (`_runtimeStatusTimer`, `_subRefreshTimer`),
    /// the engine event handlers, and the FreeConfigs sub-VM all get
    /// torn down cleanly when the VM is no longer needed. Pre-r12 these
    /// only stopped on the explicit Quit() / OnEngineStatus("Stopped")
    /// paths; an unhandled exit or a future window-rebuild path would
    /// leak them.
    ///
    /// <para>Idempotent — safe to call multiple times. Exceptions during
    /// individual cleanup steps are swallowed-with-debug-log so the
    /// rest of the chain still runs (we'd rather leak ONE thing than
    /// leak everything because the first cleanup threw).</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. Stop polling timers.
        try
        {
            _runtimeStatusTimer?.Stop();
            _runtimeStatusTimer = null;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _runtimeStatusTimer stop failed"); }

        try { StopSubRefreshTimer(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: StopSubRefreshTimer failed"); }

        // 2. Unhook engine events. _engine itself is owned by the host
        // (App / Service) so we don't dispose it here — just unhook
        // our handlers so a stale VM doesn't continue to receive
        // status updates after disposal.
        try
        {
            _engine.StatusChanged -= OnEngineStatus;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: engine StatusChanged unhook failed"); }

        // 3. Dispose the FreeConfigs sub-VM (it owns its own timers +
        // HttpClient + cache write FileStream).
        try
        {
            FreeConfigsVm?.Dispose();
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: FreeConfigsVm.Dispose failed"); }

        // 4. Cancel + dispose the subscription-refresh CTS if active.
        try
        {
            var cts = _subRefreshCts;
            _subRefreshCts = null;
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _subRefreshCts cleanup failed"); }
    }

    // ── Theme ──

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant =
                IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        // DynamicResource bindings in XAML auto-update when the theme variant
        // changes — no manual refresh needed for those. But any brush
        // property that resolves from Application.Resources in C# (our
        // runtime-status badges + ServerViewModel.StatusDotBrush) is cached
        // in a read-only getter; we must re-fire PropertyChanged so the
        // binding re-reads the resolved value.
        OnPropertyChanged(nameof(VpnBadgeBrush));
        OnPropertyChanged(nameof(ZapretBadgeBrush));
        OnPropertyChanged(nameof(TgProxyBadgeBrush));

        foreach (var s in Servers)             s.NotifyThemeChanged();
        foreach (var s in SubscriptionServers) s.NotifyThemeChanged();
    }

    // ── Localization refresh ──

    private void RefreshLocalization()
    {
        ThemeToggleText = IsDarkTheme ? Strings.ThemeLight : Strings.ThemeDark;
        ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
        if (!IsConnected && !IsConnecting)
            StatusText = Strings.NotConnected;

        // Notify all properties — refreshes every Lbl* and other localized binding
        OnPropertyChanged(string.Empty);

        // Propagate to child view models — they have their own property notifiers
        foreach (var group in AppGroups)
            group.NotifyDisplayNameChanged();

        // v2.30.7-r3 — UpdateVm.CheckLinkText is computed from Strings;
        // it doesn't auto-refresh on lang change because OnPropertyChanged("")
        // only fires on the parent VM, not on child VMs.
        UpdateVm?.NotifyLangChanged();
    }

    // ── Helpers ──

    /// <summary>
    /// First-run setup: deploy bundled profiles and sing-box binary.
    /// </summary>
    private void DeployBundledProfiles()
    {
        // Deploy profiles. Ship the platform-specific variant first + the
        // generic default.json as fallback so any code still resolving
        // "default.json" keeps working on first launch.
        string[] profileFiles = OperatingSystem.IsMacOS() ? new[] { "default-macos.json", "default.json" }
            : OperatingSystem.IsLinux() ? new[] { "default-linux.json", "default.json" }
            : new[] { "default.json" };

        foreach (var file in profileFiles)
        {
            var destPath = Path.Combine(AppPaths.ProfilesDir, file);
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "profiles", file);
            if (!File.Exists(destPath) && File.Exists(bundledPath))
            {
                File.Copy(bundledPath, destPath);
                _logger.Information("Deployed {File}", file);
            }
        }

        // Deploy sing-box binary on Unix platforms.
        // macOS: bundled inside the .app (build-mac.sh copies it into
        //        Contents/MacOS/ during packaging).
        // Linux: bundled inside the AppImage / .deb / tar.gz payload by the
        //        build-linux.yml GitHub Actions workflow, which curl-downloads
        //        sing-box-linux-amd64 from SagerNet/sing-box releases and
        //        drops it next to VPNRouter.App. Either way, we copy it
        //        from AppContext.BaseDirectory to ~/.config/vpnrouter/bin/
        //        on first launch so the user doesn't have to do anything.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var destSingBox = AppPaths.SingBoxExePath;
            var bundledSingBox = Path.Combine(AppContext.BaseDirectory, "sing-box");
            if (File.Exists(bundledSingBox) && !File.Exists(destSingBox))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destSingBox)!);
                File.Copy(bundledSingBox, destSingBox);
                File.SetUnixFileMode(destSingBox,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                _logger.Information("Deployed sing-box to {Path}", destSingBox);
            }
        }
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    /// <summary>
    /// Apply a free config (from the Free Configs page) as the active VLESS server and (re)start the VPN.
    /// IMPORTANT: mutates the VM-level <see cref="Servers"/> collection (not _settings directly) because
    /// SaveSettings() rebuilds _settings.Vless.Servers from the VM collection — direct mutations to
    /// _settings.Vless.Servers would be wiped out.
    /// </summary>
    private async Task<bool> ApplyFreeConfigAsync(FreeConfigEntry entry)
    {
        try
        {
            // v2.13.19 — one-time privacy warning before first-ever Free Config Connect.
            // User can dismiss once via the dialog's confirm button; reset via Settings.
            if (!_settings.App.FreeConfigSecurityWarningAcked)
            {
                var proceed = await ShowFreeConfigSecurityWarningAsync();
                if (!proceed) return false;
                _settings.App.FreeConfigSecurityWarningAcked = true;
                SaveSettings();
            }

            var newEntry = entry.ToVlessServerEntry();

            // Does the Free config already exist in the user's Server list? Match by host:port:uuid.
            var existingVm = Servers.FirstOrDefault(s =>
                string.Equals(s.Server, newEntry.Server, StringComparison.OrdinalIgnoreCase) &&
                s.Port == newEntry.Port &&
                string.Equals(s.Uuid, newEntry.Uuid, StringComparison.OrdinalIgnoreCase));

            ServerViewModel target;
            if (existingVm != null)
            {
                target = existingVm;
            }
            else
            {
                // Ensure display name is unique in the VM collection.
                var displayName = newEntry.Name;
                var baseName = string.IsNullOrWhiteSpace(displayName) ? "⚡ free" : displayName;
                displayName = baseName;
                var suffix = 2;
                while (Servers.Any(s => string.Equals(s.Name, displayName, StringComparison.OrdinalIgnoreCase)))
                    displayName = $"{baseName} #{suffix++}";
                newEntry.Name = displayName;

                target = new ServerViewModel(newEntry);
                Servers.Add(target);
            }

            // Make it the active server for the Manual/VLESS mode.
            // SaveSettings() reads SelectedServer + the Servers OC and persists them correctly.
            //
            // v2.28.3-r4 critical fix: also clear IsSubscribeMode. SaveSettings
            // line 1544 picks ConfigMode by checking IsSubscribeMode FIRST, then
            // IsVlessMode. If the user was on the Subscribe tab when they hit
            // Apply on a free config, IsSubscribeMode stayed true and SaveSettings
            // persisted ConfigMode='subscribe' — which made the engine pick
            // subscription servers on Start instead of the freshly-applied free
            // config. User report: "я подключаюсь а на самом деле к моей подписке
            // подключает а не к выбранному конфигу". Explicitly flipping both
            // mode flags so SaveSettings writes ConfigMode='generated' regardless
            // of which tab the user came from.
            //
            // v2.30.7-r2 (VM-3 audit fix): the explicit `_settings.App.ConfigMode
            // = "generated"` line below was dead code — SaveSettings recomputes
            // ConfigMode from VM flags (IsSubscribeMode/IsVlessMode) and overwrites
            // any direct assignment. Only the flag flips below matter. Dropped
            // the redundant assignment.
            SelectedServer = target;
            IsSubscribeMode = false;
            IsVlessMode = true;
            SelectedServerModeIndex = 0;

            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            // Stop current VPN if running.
            if (IsConnected)
            {
                try { await Task.Run(() => _engine.Stop()); } catch { }
                IsConnected = false;
            }

            // Start with the new active server.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.Run(() => _engine.StartAsync(_settings, cts.Token), cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ApplyFreeConfig failed");
            return false;
        }
    }

    /// <summary>
    /// v2.13.19 — one-time privacy warning shown before first Connect from Free Configs.
    /// Modal dialog: explains operator can see metadata (not HTTPS content), lists what
    /// to avoid (banking/email/2FA) and what's safe (YouTube/Wikipedia/Discord).
    /// Returns true if user clicked "Proceed", false if user cancelled.
    /// </summary>
    private async Task<bool> ShowFreeConfigSecurityWarningAsync()
    {
        var owner = GetMainWindow();
        if (owner == null) return true; // edge case: no window — proceed silently

        var tcs = new TaskCompletionSource<bool>();

        // v2.31.6-r8: replaced 6 hardcoded hex colours with semantic design
        // tokens from Tokens.axaml. Pre-r8 the dialog used Avalonia.Media.Brush.
        // Parse("#059669"), "#FEF3C7", "#F59E0B", "#78350F", "#DCFCE7",
        // "#14532D", "#B45309" — Rule B3 violation (no raw hex in code) AND
        // the dialog rendered identically in Light/Dark themes because the
        // hex literals don't follow theme switching. Tokens
        // (SuccessBg/Fg/Solid/Border, WarningBg/Fg/Border) auto-resolve per
        // theme. TryFindResource returns null on test/design-time AppBuilder
        // setups; falling back to a sensible default (Brushes.Transparent
        // for backgrounds, default foreground) keeps the dialog renderable
        // in headless tests.
        IBrush Tok(string key, IBrush fallback)
            => owner.TryFindResource(key, owner.ActualThemeVariant, out var v)
                && v is IBrush b
                ? b
                : fallback;

        var proceedBtn = new Button
        {
            Content = Strings.FcSecWarnProceed,
            Padding = new Thickness(12, 6),
            FontWeight = FontWeight.SemiBold,
            Background = Tok("SuccessSolidBrush", Avalonia.Media.Brushes.SeaGreen),
            Foreground = Tok("AccentOnSolidBrush", Avalonia.Media.Brushes.White),
            CornerRadius = new CornerRadius(4),
        };
        var cancelBtn = new Button
        {
            Content = Strings.FcSecWarnCancel,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(4),
        };

        var dialog = new Window
        {
            Title = Strings.FcSecWarnTitle,
            Width = 520,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "⚠ " + Strings.FcSecWarnHeader,
                            FontSize = 15,
                            FontWeight = FontWeight.Bold,
                            Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.DarkOrange),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = Strings.FcSecWarnBody,
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("WarningBgBrush", Avalonia.Media.Brushes.LightYellow),
                            BorderBrush = Tok("WarningBorderBrush", Avalonia.Media.Brushes.Goldenrod),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnDontUseList,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("WarningFgBrush", Avalonia.Media.Brushes.SaddleBrown),
                            },
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Tok("SuccessBgBrush", Avalonia.Media.Brushes.Honeydew),
                            BorderBrush = Tok("SuccessBorderBrush", Avalonia.Media.Brushes.SeaGreen),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnGoodFor,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Tok("SuccessFgBrush", Avalonia.Media.Brushes.DarkGreen),
                            },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Margin = new Thickness(0, 10, 0, 0),
                            Children = { cancelBtn, proceedBtn },
                        },
                    },
                },
            },
        };

        proceedBtn.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
