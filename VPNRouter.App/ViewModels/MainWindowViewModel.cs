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

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int RulesToastDurationMs = 2000;
    private const int TgProxySettleDelayMs = 2000;
    private const int ServiceReleaseRetryDelayMs = 2000;

    private readonly VpnEngine _engine;
    private bool _disposed;
#if PLATFORM_WINDOWS
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;
    private readonly object _tgProxyStateGate = new();
    private readonly SemaphoreSlim _tgProxyTransitionGate = new(1, 1);
    private readonly CancellationTokenSource _tgProxyLifetimeCts = new();
    private Task? _tgProxyPostStartRecheckTask;
#endif
    private readonly ILogger _logger;
    private readonly ISettingsStore _settingsStore;
    private AppSettings _settings;
    private bool _isLoadingUI;
    private bool _appsLoaded;
    private System.Threading.Timer? _subRefreshTimer;
    private CancellationTokenSource? _subRefreshCts;
    private const int SubRefreshIntervalMs = 3600_000;

    private DateTime _lastSuccessfulConnectAt = DateTime.MinValue;

    [ObservableProperty] private string _statusText = Strings.NotConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isTrueSplitActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private string _trueSplitStatusText = Strings.TrueSplitNotApplicable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isTrueSplitProblem;
    public bool IsTrueSplitStatusVisible =>
        IsConnected && IsSplitTunnel && IsRoutingAppsModeExclude && (!IsTrueSplitActive || IsTrueSplitProblem);
    public bool IsTrueSplitRetryVisible => IsTrueSplitStatusVisible && IsTrueSplitProblem && _engine.IsRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonBrush))]
    [NotifyPropertyChangedFor(nameof(SmpActiveServerLine))]
    [NotifyPropertyChangedFor(nameof(SmpHeroTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusDescription))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaText))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnected))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsDisconnected))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundLine))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundIsSuspect))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundNormalVisible))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundSuspectVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    [NotifyPropertyChangedFor(nameof(CanApplyAppChanges))]
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
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundLine))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundIsSuspect))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundNormalVisible))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundSuspectVisible))]
    [NotifyPropertyChangedFor(nameof(CanApplyAppChanges))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    private bool _isConnecting;
    [ObservableProperty] private string _connectButtonText = Strings.StartVPN;
    [ObservableProperty] private bool _isRussian;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateWarning))]
    private string _updateWarningText = string.Empty;

    public bool HasUpdateWarning => !string.IsNullOrWhiteSpace(UpdateWarningText);

    [RelayCommand]
    private void DismissUpdateWarning() => UpdateWarningText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsRecoveryNotice))]
    private string _settingsRecoveryNoticeText = string.Empty;

    public bool HasSettingsRecoveryNotice =>
        !string.IsNullOrWhiteSpace(SettingsRecoveryNoticeText);

    [RelayCommand]
    private void DismissSettingsRecoveryNotice() =>
        SettingsRecoveryNoticeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaceholderPruneNotice))]
    private string _placeholderPruneNoticeText = string.Empty;

    public bool HasPlaceholderPruneNotice =>
        !string.IsNullOrWhiteSpace(PlaceholderPruneNoticeText);

    [RelayCommand]
    private void DismissPlaceholderPruneNotice() =>
        PlaceholderPruneNoticeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflictingVpnWarning))]
    private string _conflictingVpnWarningText = string.Empty;

    public bool HasConflictingVpnWarning =>
        !string.IsNullOrWhiteSpace(ConflictingVpnWarningText);

    [RelayCommand]
    private void DismissConflictingVpnWarning() =>
        ConflictingVpnWarningText = string.Empty;

    private System.Collections.Generic.IReadOnlyList<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>
        _lastConflicts = System.Array.Empty<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>();

    [RelayCommand]
    private void RefreshConflictingVpn()
    {
        var conflicts = VPNRouter.Core.Services.ConflictingVpnDetector
            .DetectConflictingVpnProcesses(_logger);
        _lastConflicts = conflicts;
        if (conflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            return;
        }
        var first = conflicts[0];
        ConflictingVpnWarningText =
            Strings.ConflictOtherVpnDetectedMessage(first.ProcessName, first.Pid);
    }

    private bool _skipVpnConflictThisSession;

    [RelayCommand]
    private async Task IgnoreVpnConflictAndConnectAsync()
    {
        _skipVpnConflictThisSession = true;
        ConflictingVpnWarningText = string.Empty;
        _logger.Information("[VM] User opted to ignore VPN conflict — retrying Connect with bypass");
        if (!IsConnected && !IsConnecting)
        {
            await ToggleConnectionAsync();
        }
    }

    [RelayCommand]
    private async Task KillConflictingVpnAsync()
    {
        if (_lastConflicts.Count == 0)
        {
            RefreshConflictingVpn();
            if (_lastConflicts.Count == 0) return;
        }

        var killed = 0;
        var failed = 0;
        foreach (var info in _lastConflicts)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                proc.Kill();
                try { await proc.WaitForExitAsync(System.Threading.CancellationToken.None); } catch { }
                killed++;
                _logger.Information("[VM] Killed conflicting VPN: {Name} (PID {Pid})",
                    info.ProcessName, info.Pid);
            }
            catch (System.ArgumentException)
            {
                killed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Warning(ex,
                    "[VM] Failed to kill conflicting VPN {Name} (PID {Pid}) — likely needs admin rights / protected process",
                    info.ProcessName, info.Pid);
            }
        }

        RefreshConflictingVpn();

        try { ForceRefreshRuntimeStatus(); } catch { }

        if (_lastConflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            _logger.Information("[VM] Conflict cleared ({Killed} killed, {Failed} failed)",
                killed, failed);
        }
        else if (failed > 0)
        {
            ConflictingVpnWarningText =
                Strings.ConflictKillPartialFailure(killed, failed);
        }
    }

    private void ConsumeSettingsRecoveryNotice()
    {
        var recovery = _settingsStore.ConsumeRecoveryNotice();
        if (string.IsNullOrWhiteSpace(recovery)) return;

        SettingsRecoveryNoticeText =
            Strings.SettingsRecoveredFromBadConfig(string.Empty)
            + " (" + recovery + ")";
    }

    private void ConsumePlaceholderPruneNotice()
    {
        var consumed = _settingsStore.ConsumePlaceholderPruneNotice(_settings);
        if (consumed.Count == 0) return;

        var hasAnyServerLeft =
            (_settings.Vless.GetEffectiveServers().Count > 0) ||
            (_settings.App.Subscriptions?.Any(s => s.Servers?.Count > 0) == true);

        PlaceholderPruneNoticeText = hasAnyServerLeft
            ? string.Format(Strings.PlaceholderPruneBanner, consumed.Count)
            : Strings.PlaceholderPruneBannerAllGone;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiModeToggleText))]
    [NotifyPropertyChangedFor(nameof(UiModeToggleTooltip))]
    private bool _isSimpleMode;

    public string UiModeToggleText   => IsSimpleMode ? Strings.SmpToggleToAdvanced : Strings.SmpToggleToSimple;
    public string UiModeToggleTooltip => Strings.SmpToggleTooltip;

    public bool IsLinuxPlatform   => OperatingSystem.IsLinux();
    public bool IsWindowsPlatform => OperatingSystem.IsWindows();

    public bool IsDnsLeakLockdownAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool IsZapretAvailable => OperatingSystem.IsWindows();
    public bool IsTgProxyAvailable => OperatingSystem.IsWindows();
    public bool IsToolsAvailable => Internals.ToolTabAvailability.ToolsTabVisible(
        IsZapretAvailable, IsTgProxyAvailable);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isVlessMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isSubscribeMode = false;

    public bool IsServerListMode => IsVlessMode || IsSubscribeMode;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_isLoadingUI || _isReconnecting) return;
        _logger?.Information(
            "[VM] OnSelectedTabIndexChanged tab={Tab} (was IsVlessMode={V}, IsSubscribeMode={S}, ServerModeIndex={I})",
            value, IsVlessMode, IsSubscribeMode, SelectedServerModeIndex);
        if (value == 0)
        {
            IsVlessMode = true;
            IsSubscribeMode = false;
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
        else if (value == 1)
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
        }
        else if (value == 5)
        {
            try { FreeConfigsVm?.EnsureCacheLoaded(); }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] FreeConfigs lazy-load failed");
            }
        }
    }
    [ObservableProperty] private string _subscriptionUrl = string.Empty;

    public ObservableCollection<SubscriptionViewModel> Subscriptions { get; } = new();
    [ObservableProperty] private string _newSubName = string.Empty;
    [ObservableProperty] private string _newSubUrl = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    [NotifyPropertyChangedFor(nameof(IsFullTunnel))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isSplitTunnel = true;

    public bool IsFullTunnel
    {
        get => !IsSplitTunnel;
        set
        {
            if (IsSplitTunnel != !value)
                IsSplitTunnel = !value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeInclude))]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeExclude))]
    [NotifyPropertyChangedFor(nameof(L_CurrentAppsModeHint))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private string _routingAppsMode = "include";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorInclude))]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorExclude))]
    [NotifyPropertyChangedFor(nameof(ActiveAppGroups))]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private string _appsListEditorMode = "include";

    public bool IsAppsListEditorInclude
    {
        get => string.Equals(AppsListEditorMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "include"; }
    }

    public bool IsAppsListEditorExclude
    {
        get => string.Equals(AppsListEditorMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "exclude"; }
    }

    public ObservableCollection<AppGroupViewModel> ActiveAppGroups =>
        IsAppsListEditorExclude ? BypassAppGroups : AppGroups;

    public AppGroupViewModel? SelectedActiveAppGroup
    {
        get => IsAppsListEditorExclude ? SelectedBypassAppGroup : SelectedAppGroup;
        set
        {
            if (IsAppsListEditorExclude)
                SelectedBypassAppGroup = value;
            else
                SelectedAppGroup = value;
            OnPropertyChanged();
        }
    }

    partial void OnAppsListEditorModeChanged(string value)
    {
        if (value != "include" && value != "exclude")
        {
            AppsListEditorMode = "include";
            return;
        }
        OnPropertyChanged(nameof(ActiveAppGroups));
        OnPropertyChanged(nameof(SelectedActiveAppGroup));
    }

    public bool IsRoutingAppsModeInclude
    {
        get => string.Equals(RoutingAppsMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "include"; }
    }

    public bool IsRoutingAppsModeExclude
    {
        get => string.Equals(RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "exclude"; }
    }

    partial void OnRoutingAppsModeChanged(string value)
    {
        if (_isLoadingUI) return;
        var canon = (value ?? "include").Trim().ToLowerInvariant();
        if (canon != "include" && canon != "exclude") canon = "include";
        _settings.App.RoutingAppsMode = canon;
        AppsListEditorMode = canon;

        RefreshAppCheckboxes();

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on apps mode change failed");
        }
        MarkRoutingSettingsChanged();
    }

    internal bool IsAppCheckedInCurrentMode(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var list = GetActiveAppList();
        if (list == null) return false;
        return list.Any(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetAppCheckedInCurrentMode(string processName, bool isChecked)
    {
        if (string.IsNullOrEmpty(processName)) return;
        if (!string.Equals(_settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
            _settings.App.RoutingAppsIncludeInitialized = true;
        var list = GetActiveAppList();
        if (list == null) return;

        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI || IsBatchUpdating) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on per-app mode-aware toggle failed");
        }
        MarkRoutingSettingsChanged();
    }

    internal bool IsAppCheckedInIncludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsInclude, processName);

    internal bool IsAppCheckedInExcludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsExclude, processName);

    internal void SetAppCheckedInIncludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsInclude ??= new List<string>(), processName, isChecked, isIncludeList: true);

    internal void SetAppCheckedInExcludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsExclude ??= new List<string>(), processName, isChecked, isIncludeList: false);

    private static bool IsAppCheckedInList(List<string>? list, string processName)
    {
        if (string.IsNullOrEmpty(processName) || list == null) return false;
        return list.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetAppCheckedInList(List<string> list, string processName, bool isChecked, bool isIncludeList)
    {
        if (string.IsNullOrEmpty(processName)) return;
        if (isIncludeList) _settings.App.RoutingAppsIncludeInitialized = true;
        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI || IsBatchUpdating) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on per-app list toggle failed");
        }
        var editsActiveList = isIncludeList == !string.Equals(
            _settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        if (editsActiveList) MarkRoutingSettingsChanged();
    }

    private List<string>? GetActiveAppList()
    {
        if (_settings?.App == null) return null;
        var isExclude = string.Equals(
            _settings.App.RoutingAppsMode, "exclude",
            StringComparison.OrdinalIgnoreCase);

        if (isExclude)
        {
            return _settings.App.RoutingAppsExclude
                ??= new List<string>();
        }
        return _settings.App.RoutingAppsInclude
            ??= new List<string>();
    }

    private int _batchUpdateDepth;
    public bool IsBatchUpdating => _batchUpdateDepth > 0;

    public IDisposable BeginBatchUpdate()
    {
        System.Threading.Interlocked.Increment(ref _batchUpdateDepth);
        return new BatchUpdateScope(this);
    }

    private sealed class BatchUpdateScope : IDisposable
    {
        private MainWindowViewModel? _vm;
        public BatchUpdateScope(MainWindowViewModel vm) => _vm = vm;
        public void Dispose()
        {
            var vm = System.Threading.Interlocked.Exchange(ref _vm, null);
            if (vm == null) return;
            if (System.Threading.Interlocked.Decrement(ref vm._batchUpdateDepth) == 0)
            {
                if (!vm._isLoadingUI)
                {
                    try { vm.SaveSettings(); }
                    catch (Exception ex) { vm._logger?.Warning(ex, "[VM] SaveSettings after batch update failed"); }
                    vm.MarkRoutingSettingsChanged();
                }
            }
        }
    }

    private void RefreshAppCheckboxes()
    {
        foreach (var group in AppGroups.Concat(BypassAppGroups))
        {
            foreach (var app in group.Apps)
                app.RaiseIsCheckedChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBadComboWarningVisible))]
    private bool _bypassRussianTraffic = true;

    [ObservableProperty] private bool _customRulesAboveToggles;

    partial void OnCustomRulesAboveTogglesChanged(bool value)
    {
        if (_isLoadingUI) return;
        _settings.App.CustomRulesPriority = value ? "custom_first" : "toggles_first";
        SaveSettings();
        MarkRoutingSettingsChanged();
    }

    [ObservableProperty] private string _customRulesText = string.Empty;

    [ObservableProperty] private string _customRulesErrorText = string.Empty;

    [ObservableProperty] private string _customRulesConflictText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> CustomRulesList { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    private bool _isSyncingCustomRules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomRulesCountText))]
    private string _customRulesSearchText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> FilteredCustomRulesList { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();

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

    private void RebuildFilteredCustomRulesList()
    {
        FilteredCustomRulesList.Clear();
        var query = (CustomRulesSearchText ?? string.Empty).Trim().ToLowerInvariant();
        var actionFilter = RulesActionFilter ?? "all";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleValueBorderColor))]
    private bool _newRuleValueIsValid = true;

    [ObservableProperty] private string _newRuleValueHint = string.Empty;
    [ObservableProperty] private string _newRuleValuePlaceholder = ".corp.example";

    public bool NewRuleValueBorderColor => !NewRuleValueIsValid;

    public string NewRuleActionHint => NewRuleAction switch
    {
        "direct" => Strings.RuleActionHintDirect,
        "proxy"  => Strings.RuleActionHintProxy,
        "block"  => Strings.RuleActionHintBlock,
        _ => string.Empty,
    };

    public string NewRuleTypeHint => NewRuleType switch
    {
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

    private static readonly System.Collections.Generic.Dictionary<string, System.Text.RegularExpressions.Regex> _typeValidatorMap = new()
    {
        ["domain"]         = new(@"^[a-z0-9.-]+\.[a-z]{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["domain_suffix"]  = new(@"^\.?[a-z0-9.-]+\.[a-z]{2,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["domain_keyword"] = new(@"^[a-z0-9.\-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["ip_cidr"]        = new(@"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$|^[0-9a-f:]+/\d{1,3}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["port"]           = new(@"^\d{1,5}(\s*,\s*\d{1,5})*$", System.Text.RegularExpressions.RegexOptions.Compiled),
        ["port_range"]     = new(@"^\d{1,5}-\d{1,5}$", System.Text.RegularExpressions.RegexOptions.Compiled),
        ["network"]        = new(@"^(tcp|udp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["process_name"]   = new(@"^[\w.\-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["process_path"]   = new(@"^([A-Z]:\\|/).+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["geosite"]        = new(@"^[a-z][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
        ["geoip"]          = new(@"^[a-z][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled),
    };

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
            ok = true;
        }

        NewRuleValueIsValid = ok;
        NewRuleValueHint = ok
            ? (IsRussian ? "✓ корректно" : "✓ valid")
            : (IsRussian ? $"✗ не подходит формату {NewRuleType}" : $"✗ wrong format for {NewRuleType}");
    }

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

    public IReadOnlyList<string> AvailableRuleActions { get; }
        = new[] { "direct", "proxy", "block" };

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBadComboWarningVisible))]
    private bool _isDnsLeakLockdownEnabled = true;

    public bool IsBadComboWarningVisible => false;

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

    [ObservableProperty] private bool _hasPendingAppChanges;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyAppChanges))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    private bool _isApplying;
    private int _routingSettingsRevision;
    public bool CanApplyAppChanges => IsConnected && !IsConnecting && !IsApplying;
    public bool CanToggleConnection => !IsConnecting && !IsApplying;

    private void MarkRoutingSettingsChanged()
    {
        Interlocked.Increment(ref _routingSettingsRevision);
        if (IsConnected) HasPendingAppChanges = true;
    }

    private void ClearPendingIfRevisionUnchanged(int appliedRevision)
    {
        if (Volatile.Read(ref _routingSettingsRevision) == appliedRevision)
            HasPendingAppChanges = false;
    }

    [ObservableProperty] private bool _autostartVpn = false;
    [ObservableProperty] private bool _autostartZapret = false;
    [ObservableProperty] private bool _autostartTgProxy = false;
    [ObservableProperty] private bool _autostartUi = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblDpiToggle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretMagicButton))]
    [NotifyPropertyChangedFor(nameof(HasZapretStrategiesForQuickStart))]
    private bool _zapretEnabled = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomStrategy))]
    private int _zapretStrategyIndex = 0;
    public bool IsCustomStrategy => ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
        && ZapretStrategies[ZapretStrategyIndex] == "custom";
    [ObservableProperty] private string _zapretCustomArgs = string.Empty;
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

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<ZapretStrategyDisplayItem> _zapretStrategiesDisplay = new();

    public bool HasZapretStrategiesForQuickStart =>
        ZapretStrategies != null
        && ZapretStrategies.Count > 0
        && !IsZapretProbing
        && !ZapretEnabled;
    [ObservableProperty] private bool _receivePrereleases = false;

    private bool _suppressZapretAvToast = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(IsZapretMagicButtonEnabled))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretAirPill))]
    private int _zapretProbePassCount = 0;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretAirPill))]
    private int _zapretProbeTotalCount = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastProbeLog))]
    private string? _lastProbeLogPath = null;

    public bool HasLastProbeLog => !string.IsNullOrEmpty(LastProbeLogPath);

    public string LblOpenProbeLog =>
        IsRussian ? "Открыть лог проверки" : "Open probe log";

    public string LblStrategyBadgeLegend =>
        IsRussian
            ? "✓ работает   ⚠ частично   ✗ не работает   ◌ не проверена   ⏱ устарело"
            : "✓ working   ⚠ partial   ✗ failed   ◌ untested   ⏱ stale";

    public string LblLegendWorking  => IsRussian ? "работает"     : "working";
    public string LblLegendPartial  => IsRussian ? "частично"     : "partial";
    public string LblLegendFailed   => IsRussian ? "не работает"  : "failed";
    public string LblLegendUntested => IsRussian ? "не проверена" : "untested";
    public string LblLegendStale    => IsRussian ? "устарело"     : "stale";

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
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
            }
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasZapretAvBlockToast))]
    private string _zapretAvBlockToast = string.Empty;

    public bool HasZapretAvBlockToast => !string.IsNullOrWhiteSpace(ZapretAvBlockToast);

    private System.Threading.CancellationTokenSource? _zapretAvBlockToastCts;

    private void OnZapretImmediateExit()
    {
        if (_suppressZapretAvToast) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ZapretAvBlockToast = Strings.ZapretAvBlockToast;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblTgProxyToggle))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyMainAction))]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroTitle))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroLede))]
    private bool _tgProxyEnabled = false;
    [ObservableProperty] private string _tgProxyStatus = Strings.Stopped;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblTgProxyHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblTgProxyAirPill))]
    private int _tgProxyPort = 1443;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private string _tgProxySecret = "";
    [ObservableProperty] private string _tgProxyLink = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    [NotifyPropertyChangedFor(nameof(LblUpdateTgProxy))]
    private string _tgProxyVersionText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxySetUp))]
    private bool _isTgProxyDownloading = false;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTgProxyStats))]
    private string _tgProxyStats = "";

    public bool HasTgProxyStats => !string.IsNullOrEmpty(TgProxyStats);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTgProxyToast))]
    private string _tgProxyToast = string.Empty;

    public bool HasTgProxyToast => !string.IsNullOrEmpty(TgProxyToast);

    [ObservableProperty]
    private bool _isTelegramSchemeWarningVisible;

    [ObservableProperty]
    private string _tgProxyDownloadStep = string.Empty;

    public bool HasTgProxyDownloadStep => !string.IsNullOrEmpty(TgProxyDownloadStep);

    partial void OnTgProxyDownloadStepChanged(string value)
    {
        OnPropertyChanged(nameof(HasTgProxyDownloadStep));
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVlessMode))]
    private int _selectedServerModeIndex;

    partial void OnSelectedServerModeIndexChanged(int value)
    {
        if (_isLoadingUI) return;
        _logger?.Information(
            "[VM] OnSelectedServerModeIndexChanged value={V} (was IsVlessMode={IV}, IsSubscribeMode={IS})",
            value, IsVlessMode, IsSubscribeMode);
        IsVlessMode = value == 0;
        SaveSettings();
    }

    partial void OnCustomRulesTextChanged(string value)
    {
        if (_isLoadingUI) return;
        if (_isSyncingCustomRules) return;
        SaveSettings();
        OnPropertyChanged(nameof(CustomRulesErrorText));
        OnPropertyChanged(nameof(CustomRulesConflictText));

        RebuildCustomRulesList();

        OnPropertyChanged(nameof(RulesEditorIsDirty));

        MarkRoutingSettingsChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesViewCards))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewRead))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewEdit))]
    private string _rulesViewMode = "cards";

    [ObservableProperty] private bool _isRulesNarrow;

    public bool IsRulesViewCards => RulesViewMode == "cards";

    public bool IsRulesViewRead => RulesViewMode == "read";

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
        EditedCustomRulesText = CustomRulesText;
        RulesViewMode = "edit";
        RecomputeRulesEditorState();
    }

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeDirectRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeProxyRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeBlockRules { get; }
        = new System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel>();

    [ObservableProperty] private string _readModeDirectHeader = string.Empty;
    [ObservableProperty] private string _readModeProxyHeader  = string.Empty;
    [ObservableProperty] private string _readModeBlockHeader  = string.Empty;

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

    [ObservableProperty]
    private string _editedCustomRulesText = string.Empty;

    partial void OnEditedCustomRulesTextChanged(string value) => RecomputeRulesEditorState();

    [ObservableProperty] private string _rulesEditorLineNumbers = "1";

    [ObservableProperty] private string _rulesEditorStatusText = string.Empty;

    [ObservableProperty] private string _rulesEditorErrorListText = string.Empty;

    [ObservableProperty] private bool _rulesEditorHasErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulesEditorApplyText))]
    private int _rulesEditorActiveCount;

    public bool RulesEditorIsDirty =>
        !string.Equals(EditedCustomRulesText ?? string.Empty,
                       CustomRulesText ?? string.Empty,
                       System.StringComparison.Ordinal);

    public string RulesEditorApplyText => IsRussian
        ? $"Применить ({RulesEditorActiveCount})"
        : $"Apply ({RulesEditorActiveCount})";

    private void RecomputeRulesEditorState()
    {
        var text = EditedCustomRulesText ?? string.Empty;
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
            if (ln.StartsWith("#", System.StringComparison.Ordinal) ||
                ln.StartsWith("!", System.StringComparison.Ordinal)) continue;

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

        var sbNums = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sbNums.Append('\n');
            sbNums.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        RulesEditorLineNumbers = sbNums.ToString();

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

    [RelayCommand]
    private void ApplyEditedRules()
    {
        if (RulesEditorHasErrors) return;
        CustomRulesText = EditedCustomRulesText ?? string.Empty;
        RecomputeRulesEditorState();
    }

    [RelayCommand]
    private void RevertEditedRules()
    {
        EditedCustomRulesText = CustomRulesText ?? string.Empty;
        RecomputeRulesEditorState();
    }

    [ObservableProperty] private bool _isRulesHelpBannerDismissed;

    [RelayCommand]
    private void DismissRulesHelpBanner() => IsRulesHelpBannerDismissed = true;

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

    [RelayCommand]
    private void ClearAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        ClearAllConfirmPending = true;
        ClearAllConfirmText = IsRussian
            ? $"Удалить все правила ({CustomRulesList.Count})?"
            : $"Delete all rules ({CustomRulesList.Count})?";
    }

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

    [RelayCommand]
    private void CancelClearAllCustomRules()
    {
        ClearAllConfirmPending = false;
        ClearAllConfirmText = string.Empty;
    }

    [ObservableProperty] private bool _clearAllConfirmPending;
    [ObservableProperty] private string _clearAllConfirmText = string.Empty;

    [RelayCommand]
    private void EnableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = true;
    }

    [RelayCommand]
    private void DisableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = false;
    }

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

    [ObservableProperty] private string _rulesToastText = string.Empty;

    private System.Threading.CancellationTokenSource? _rulesToastCts;

    private void ShowRulesToast(string text)
    {
        RulesToastText = text;
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

    private void OnCustomRuleRowChanged(CustomRuleViewModel _)
    {
        if (_isSyncingCustomRules || _isLoadingUI) return;
        FlushCustomRulesListToSettings();
    }

    private void OnCustomRuleRowRemoveRequested(CustomRuleViewModel row)
    {
        if (_isLoadingUI) return;
        CustomRulesList.Remove(row);
        FilteredCustomRulesList.Remove(row);
        OnPropertyChanged(nameof(CustomRulesCountText));
        FlushCustomRulesListToSettings();
    }

    private void FlushCustomRulesListToSettings()
    {
        if (_isSyncingCustomRules) return;
        _isSyncingCustomRules = true;
        try
        {
            _settings.App.CustomRules = CustomRulesList.Select(vm => vm.ToModel()).ToList();
            CustomRulesText = VPNRouter.Core.Services.CustomRulesParser
                .SerializeToText(_settings.App.CustomRules);
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
        MarkRoutingSettingsChanged();
    }

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

            foreach (var rule in result.Rules)
            {
                CustomRulesList.Add(new CustomRuleViewModel(
                    rule,
                    onChanged: OnCustomRuleRowChanged,
                    onRemoveRequested: OnCustomRuleRowRemoveRequested));
            }
            FlushCustomRulesListToSettings();

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

    [RelayCommand]
    private void AddCustomRuleFromForm()
    {
        if (string.IsNullOrWhiteSpace(NewRuleValue))
        {
            NewRuleValidationError = Strings.RulesEmptyValue;
            return;
        }
        if (!NewRuleValueIsValid)
        {
            NewRuleValidationError = IsRussian
                ? $"Значение не подходит к типу «{NewRuleType}»"
                : $"Value doesn't match type \"{NewRuleType}\"";
            return;
        }
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
        CustomRulesList.Add(new CustomRuleViewModel(
            parsed.Rules[0],
            onChanged: OnCustomRuleRowChanged,
            onRemoveRequested: OnCustomRuleRowRemoveRequested));
        NewRuleValue = string.Empty;
        NewRuleComment = string.Empty;
        NewRuleValidationError = string.Empty;
        FlushCustomRulesListToSettings();
    }

    partial void OnAutostartUiChanged(bool value)
    {
        if (_isLoadingUI) return;
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

    partial void OnAutostartVpnChanged(bool value)
    {
        if (!_isLoadingUI)
        {
            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AutostartVpn change failed"); }
        }
        OnPropertyChanged(nameof(SmpAutostartChecked));
    }
    partial void OnAutostartZapretChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AutostartZapret change failed"); }
    }
    partial void OnAutostartTgProxyChanged(bool value)
    {
        if (_isLoadingUI) return;

        if (value && string.IsNullOrWhiteSpace(TgProxySecret))
        {
            TgProxySecret = Convert.ToHexStringLower(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        }

        SaveSettings();
    }

    partial void OnIsDnsLeakLockdownEnabledChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on IsDnsLeakLockdownEnabled change failed"); }
        MarkRoutingSettingsChanged();
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblFlowsealHosts))]
    private bool _flowsealHostsInstalled;
    [ObservableProperty] private int _gameFilterModeIndex;
    [ObservableProperty] private int _ipSetModeIndex;
    [ObservableProperty] private bool _zapretAutoUpdateCheck;

    public string LblFlowsealHosts => IsRussian
        ? (FlowsealHostsInstalled ? "Убрать Flowseal hosts" : "Добавить Flowseal hosts")
        : (FlowsealHostsInstalled ? "Remove Flowseal hosts" : "Add Flowseal hosts");

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretToolSelected))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyToolSelected))]
    private int _selectedToolIndex = Internals.ToolTabAvailability.DefaultToolIndex(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsWindows());

    public bool IsZapretToolSelected => SelectedToolIndex == 0;
    public bool IsTgProxyToolSelected => SelectedToolIndex == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private AppGroupViewModel? _selectedAppGroup;

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

    [ObservableProperty] private string _vlessUri = string.Empty;

    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<CustomConfigViewModel> CustomConfigs { get; } = new();
    public ObservableCollection<ServerViewModel> SubscriptionServers { get; } = new();
    [ObservableProperty] private ServerViewModel? _selectedSubscriptionServer;
    public ObservableCollection<AppGroupViewModel> AppGroups { get; } = new();
    public ObservableCollection<AppGroupViewModel> BypassAppGroups { get; } = new();

    [ObservableProperty] private ServerViewModel? _selectedServer;
    [ObservableProperty] private CustomConfigViewModel? _selectedCustomConfig;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private AppGroupViewModel? _selectedBypassAppGroup;

    public UpdateNotificationViewModel UpdateVm { get; }
    public ServiceViewModel ServiceVm { get; }
    public FreeConfigsPageViewModel FreeConfigsVm { get; private set; } = null!;

    public MainWindowViewModel() : this(null) { }

    public MainWindowViewModel(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore ?? RealSettingsStore.Instance;

        _logger = new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "vpnrouter.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();

        TcpTlsProbe.Logger = _logger;

        if (!string.IsNullOrWhiteSpace(Program.PendingUpdateWarning))
        {
            UpdateWarningText = Program.PendingUpdateWarning!;
            Program.PendingUpdateWarning = null;
        }

        AppPaths.EnsureDirectories();
        DeployBundledProfiles();

        _engine = PlatformServices.CreateVpnEngine(_logger);
        _engine.StatusChanged += OnEngineStatus;
        _engine.Connected += OnEngineConnected;
        _engine.AutoFailoverTriggered += OnAutoFailoverMessage;
        _engine.TrueSplitEngagedChanged += OnTrueSplitEngagedChanged;
        _engine.TrueSplitStateChanged += OnTrueSplitStateChanged;

        _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

        WireServersOrphanTracking();

        UpdateVm = new UpdateNotificationViewModel(_settings.Update, _logger);
        ServiceVm = new ServiceViewModel(_logger);
        FreeConfigsVm = new FreeConfigsPageViewModel(_logger, ApplyFreeConfigAsync, () => _settings);

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

        WireOsThemeFollow();

        var startBackgroundServices =
            !AppContext.TryGetSwitch(
                "VPNRouter.Tests.DisableBackgroundServices",
                out var backgroundServicesDisabled) ||
            !backgroundServicesDisabled;

        if (startBackgroundServices)
        {
            DetectServiceManagedVpn();

            _ = UpdateVm.CheckOnStartupAsync();

            StartRuntimeStatusPolling();

            _ = BootstrapAutostartAsync();
        }

        ConsumeSettingsRecoveryNotice();
        ConsumePlaceholderPruneNotice();
    }

    public event Action<ServerViewModel?>? ActiveServerChanged;

    private void RefreshActiveIndicator()
    {
        var activeIp = _engine?.ActiveServerAddress;

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

        var autoSelect = isSubscribeMode && AutoSelectBestServer;
        foreach (var s in SubscriptionServers)
        {
            bool isActive;
            if (autoSelect)
                isActive = IsConnected
                    && _autoSelectedServer is not null
                    && ReferenceEquals(s, _autoSelectedServer);
            else
                isActive = isSubscribeMode
                    && IsConnected
                    && !string.IsNullOrEmpty(activeIp)
                    && IsRowActive(s, activeIp, subscriptionActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        ActiveServerChanged?.Invoke(active);
    }

    private static bool IsRowActive(ServerViewModel row, string activeIp, string? activeName)
    {
        if (row.Server != activeIp)
            return false;

        if (string.IsNullOrWhiteSpace(activeName))
            return true;

        return string.Equals(row.Name, activeName, StringComparison.OrdinalIgnoreCase);
    }

    private void DetectServiceManagedVpn()
    {
        try
        {
            var singboxRunning = VPNRouter.Core.Services.RuntimeStatusDetector.IsVpnRunning();
            if (!singboxRunning) return;

            var tunOwned = TunOwnershipLock.IsOwnedByAnyone();
            if (!tunOwned)
            {
                return;
            }

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configuredMode = _settings.App.ConfigMode ?? "generated";
            var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? "subscribe"
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase) ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            MarkTrueSplitServiceManagedIfNeeded();
            StartSubRefreshTimer();
            _logger.Information("[VM] Detected VPN running via service (sing-box alive + TUN owned)");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] DetectServiceManagedVpn failed");
        }
    }

    private void LoadSettingsIntoUI()
    {
        _isLoadingUI = true;
        try
        {
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

        ThemePreference = NormalizeThemePref(_settings.App.Theme);
        ApplyTheme();

        IsSimpleMode = true;

        var firstEnabledSub = _settings.App.Subscriptions?
            .FirstOrDefault(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        if (firstEnabledSub != null)
            SmpInput = firstEnabledSub.Url;

        var configMode = _settings.App.ConfigMode ?? "generated";
        IsSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        IsVlessMode = !configMode.Equals("custom", StringComparison.OrdinalIgnoreCase) && !IsSubscribeMode;
        SubscriptionUrl = _settings.App.SubscriptionUrl ?? "";
        SelectedTabIndex = IsSubscribeMode ? 1 : 0;

        IsSplitTunnel = !(_settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);

        RoutingAppsMode = (_settings.App.RoutingAppsMode ?? "include").Trim().ToLowerInvariant();

        BypassRussianTraffic = _settings.App.BypassRussianTraffic;
        CustomRulesAboveToggles = string.Equals(
            _settings.App.CustomRulesPriority,
            "custom_first",
            System.StringComparison.OrdinalIgnoreCase);

        _isSyncingCustomRules = true;
        try
        {
            CustomRulesText = VPNRouter.Core.Services.CustomRulesParser
                .SerializeToText(_settings.App.CustomRules);
        }
        finally { _isSyncingCustomRules = false; }
        RebuildCustomRulesList();

        StrictMode = _settings.App.StrictMode;
        TunMtu = _settings.Tun.Mtu;

        ForceIpv4Only = _settings.App.ForceIpv4Only;
        FlushDnsOnStart = _settings.App.FlushDnsOnStart;
        StrictDns = _settings.App.StrictDns;
        BlockAds = _settings.App.BlockAds;
        AutoSelectBestServer = _settings.Vless.AutoSelectBestServer;
        ConnectionIntentIndex = IntentToIndex(_settings.App.ConnectionIntent);
        IsDnsLeakLockdownEnabled = _settings.App.DnsLeakLockdown;

        AutostartVpn = _settings.App.AutostartVpn;
        AutostartZapret = _settings.App.AutostartZapret;
        AutostartTgProxy = _settings.App.AutostartTgProxy;
#if PLATFORM_WINDOWS
        AutostartUi = AutostartHelper.IsEnabled();
#endif
        LoadZapretStrategies();
        ZapretCustomArgs = _settings.App.ZapretCustomArgs;
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

        TgProxyPort = _settings.App.TgProxyPort > 0 ? _settings.App.TgProxyPort : 1443;
        TgProxySecret = _settings.App.TgProxySecret;
        TgProxyVersionText = TgProxyUpdater.IsInstalled()
            ? (TgProxyUpdater.GetLocalVersion() ?? "?")
            : (IsRussian ? "Не установлен" : "Not installed");
        if (_tgProxy?.IsRunning == true)
        {
            TgProxyEnabled = true;
            TgProxyStatus = $"{Strings.StatusRunning} (PID {_tgProxy.Pid})";
            if (!string.IsNullOrEmpty(TgProxySecret))
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        }
        else
        {
            TgProxyEnabled = false;
            TgProxyStatus = Strings.Stopped;
        }
#endif

        ReceivePrereleases = _settings.Update.IsExperimental;

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
        ServerViewModel.RefreshUdpSiblingFlags(Servers);
        ServerViewModel.RefreshProviderRiskFlags(Servers);
        SelectedServer = activeServer ?? Servers.FirstOrDefault();

        MarkOrphanServers();

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

        Subscriptions.Clear();
        foreach (var entry in _settings.App.Subscriptions)
            Subscriptions.Add(new SubscriptionViewModel(entry));

        RebuildSubscriptionPool();

        CustomConfigs.Clear();
        CustomConfigViewModel? activeConfig = null;
        foreach (var entry in _settings.App.CustomConfigs ?? new())
        {
            var isActive = entry.Name == _settings.App.ActiveCustomConfig;
            var vm = new CustomConfigViewModel(entry, isActive);
            CustomConfigs.Add(vm);
            if (isActive) activeConfig = vm;
        }
        if (activeConfig == null && CustomConfigs.Count > 0)
        {
            activeConfig = CustomConfigs[0];
            activeConfig.IsActive = true;
            _settings.App.ActiveCustomConfig = activeConfig.Name;
        }
        SelectedCustomConfig = activeConfig;

        var subTabHasManual = Servers.Count > 0;
        var subTabHasCustom = CustomConfigs.Count > 0;
        var subTabIndex = (subTabHasManual || !subTabHasCustom) ? 0 : 1;
        SelectedServerModeIndex = subTabIndex;
        _logger?.Information(
            "[VM] Sub-tab init: ServerModeIndex={Idx} (manual={M}, custom={C}, configMode={CM})",
            subTabIndex, Servers.Count, CustomConfigs.Count, _settings.App.ConfigMode);

        LoadApps();

        RefreshLocalization();
        }
        finally
        {
            _isLoadingUI = false;
        }
    }

    private bool IsServiceManagedVpn => IsConnected && !(_engine?.IsRunning ?? false);

    [RelayCommand]
    private Task ApplyPendingChangesAsync() => ApplyPendingChangesInternalAsync(forceRestart: false);

    [RelayCommand]
    private void SwitchToSplitTunnel()
    {
        if (IsSplitTunnel) return;
        IsSplitTunnel = true;
        MarkRoutingSettingsChanged();
        SaveSettings();
    }

    private async Task ApplyPendingChangesInternalAsync(bool forceRestart)
    {
        if (!CanApplyAppChanges) return;
        IsApplying = true;
        var appliedRevision = Volatile.Read(ref _routingSettingsRevision);
        try
        {
            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            if (IsServiceManagedVpn)
            {
                if (ServiceVm.IsAvailable)
                {
                    StatusText = IsRussian
                        ? "Перезапускаю службу с новыми настройками..."
                        : "Restarting service with new settings...";
                    await ServiceVm.RestartServiceCommand.ExecuteAsync(null);
                    ClearPendingIfRevisionUnchanged(appliedRevision);
                    return;
                }

                ClearPendingIfRevisionUnchanged(appliedRevision);
                StatusText = IsRussian
                    ? "Настройки сохранены. Остановите и запустите VPN, чтобы они применились (служба перечитает config.yaml при старте)."
                    : "Settings saved. Stop and Start VPN to apply — the service re-reads config.yaml on start.";
                return;
            }

            var ok = await Task.Run(() => _engine.ApplyAsync(_settings, CancellationToken.None, forceRestart));
            if (ok)
            {
                ClearPendingIfRevisionUnchanged(appliedRevision);
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
            StatusText = $"{(IsRussian ? "Не удалось применить" : "Apply failed")}: {ex.Message}";
        }
        finally { IsApplying = false; }
    }

    private void RestoreConnectedStatus()
    {
        if (!IsConnected) return;
        var (serverName, serverIp) = DeriveConnectedServerLabel();

        var configuredMode = _settings.App.ConfigMode ?? "generated";
        var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
            ? "subscribe"
            : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase) ? "manual" : "custom";
        var tunnelLabel = IsSplitTunnel ? "split" : "full";
        var modeLabel = $"{configLabel}/{tunnelLabel}";

        StatusText = Strings.Connected(modeLabel, serverName, serverIp);
    }

    private (string? name, string? ip) DeriveConnectedServerLabel()
    {
        var serverIp = _engine.ActiveServerAddress;
        var configuredMode = _settings.App.ConfigMode ?? "generated";
        if (configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
        {
            return AutoSelectStatus.ResolveSubscribeLabel(
                AutoSelectBestServer,
                _autoSelectedServer is not null,
                _autoSelectedServer?.DisplayName,
                _autoSelectedServer?.Server,
                Strings.AutoSelectStatusLabel,
                (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName,
                serverIp);
        }
        if (configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase))
            return ((SelectedServer ?? Servers.FirstOrDefault())?.DisplayName, serverIp);
        var c = CustomConfigs.FirstOrDefault(x => x.IsActive) ?? SelectedCustomConfig ?? CustomConfigs.FirstOrDefault();
        return (c?.Name, serverIp);
    }

    private const string SudoersFormatMarker = "# vpnrouter 2026-09-02 sudoers (sing-box + exact kill + networksetup DNS + pfctl kill-switch)";

    private void EnsureMacSudoAccess()
    {
        const string sudoersPath = "/etc/sudoers.d/vpnrouter";

        var sudoersMarkerPath = Path.Combine(AppPaths.DataDir, "macos-sudoers.marker");
        bool needsRewrite = true;
        try
        {
            if (File.Exists(sudoersMarkerPath) &&
                File.ReadAllText(sudoersMarkerPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
            {
                needsRewrite = false;
            }
            else if (File.Exists(sudoersPath))
            {
                try
                {
                    if (File.ReadAllText(sudoersPath).Contains(SudoersFormatMarker, StringComparison.Ordinal))
                        needsRewrite = false;
                }
                catch { }
            }
        }
        catch { needsRewrite = true; }
        if (!needsRewrite) return;

        StatusText = IsRussian ? "Настройка sudo (один раз)..." : "Setting up sudo (one-time)...";

        var user = Environment.UserName;
        var singbox = AppPaths.SingBoxExePath;
        var singboxEscaped = singbox.Replace(" ", "\\ ");
        var tmpFile = Path.Combine(Path.GetTempPath(), "vpnrouter-sudoers");
        File.WriteAllText(tmpFile,
            $"{SudoersFormatMarker}\n" +
            $"{user} ALL=(root) NOPASSWD: {singboxEscaped} *\n" +
            $"{user} ALL=(root) NOPASSWD: /bin/kill -KILL -- [0-9]*\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/sbin/networksetup *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/dscacheutil *\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/killall -HUP mDNSResponder\n" +
            $"{user} ALL=(root) NOPASSWD: /sbin/pfctl *\n");

        var helperScript = Path.Combine(Path.GetTempPath(), "vpnrouter-setup.sh");
        File.WriteAllText(helperScript,
            $"#!/bin/bash\ncp \"{tmpFile}\" {sudoersPath}\nchmod 0440 {sudoersPath}\nchown root:wheel {sudoersPath}\nrm -f \"{tmpFile}\" \"{helperScript}\"\n");
        File.SetUnixFileMode(helperScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

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
        var osascriptExit = proc.HasExited ? proc.ExitCode : -1;

        _logger.Information("osascript exit={Exit} stdout={Out} stderr={Err}",
            osascriptExit, stdout, stderr);
        proc.Dispose();

        if (osascriptExit != 0)
        {
            _logger.Warning("sudoers setup: osascript exit {Exit} (cancelled/failed) — NOT writing marker; will re-prompt next time", osascriptExit);
            return;
        }
        if (!File.Exists(sudoersPath))
        {
            _logger.Warning("Failed to configure sudoers (file absent after a successful osascript?)");
            return;
        }
        if (!ProbeSudoGrant())
        {
            _logger.Warning("sudoers setup: pfctl grant probe failed after osascript — NOT writing marker; will re-prompt");
            return;
        }

        _logger.Information("Passwordless sudo configured + probed");
        try { File.WriteAllText(sudoersMarkerPath, SudoersFormatMarker); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to write sudoers marker — may re-prompt next launch"); }
    }

    private static bool ProbeSudoGrant()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sudo")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("/sbin/pfctl");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("info");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void MarkOrphanServers()
    {
        if (_settings == null) return;

        var hasEnabledSubs = _settings.App?.Subscriptions?
            .Any(s => s.Enabled && (s.Servers?.Count ?? 0) > 0) == true;
        if (!hasEnabledSubs)
        {
            foreach (var vm in Servers)
                vm.IsOrphanFromSubscription = false;
            return;
        }

        var subKeys = _settings.App!.Subscriptions!
            .Where(s => s.Enabled)
            .SelectMany(s => s.Servers ?? new System.Collections.Generic.List<VlessServerEntry>())
            .Select(s => $"{s.Server}|{s.Port}|{s.Uuid}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in Servers)
        {
            var key = $"{vm.Server}|{vm.Port}|{vm.Uuid}";
            vm.IsOrphanFromSubscription = !subKeys.Contains(key);
        }
    }

    private void WireServersOrphanTracking()
    {
        Servers.CollectionChanged += (_, _) =>
        {
            if (_isLoadingUI) return;
            try { ServerViewModel.RefreshUdpSiblingFlags(Servers); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto RefreshUdpSiblingFlags on Servers change failed"); }
            try { ServerViewModel.RefreshProviderRiskFlags(Servers); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto RefreshProviderRiskFlags on Servers change failed"); }
            try { MarkOrphanServers(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto MarkOrphanServers on Servers change failed"); }
        };
    }

    private void SaveSettings()
    {
        if (_isLoadingUI) return;

        try
        {
            var configPath = AppPaths.ConfigYamlPath;
            if (File.Exists(configPath))
                File.Copy(configPath, configPath + ".bak", overwrite: true);
        }
        catch (Exception ex) { _logger.Debug(ex, "[Settings] Backup failed"); }

        var wantsCustomMode = !IsSubscribeMode && !IsVlessMode;
        var hasCustomConfig = !string.IsNullOrWhiteSpace(_settings.App.ActiveCustomConfig)
                              || !string.IsNullOrWhiteSpace(_settings.App.CustomConfig)
                              || (_settings.App.CustomConfigs?.Count ?? 0) > 0;
        var hasActiveSubscription = (_settings.App.Subscriptions?.Any(s => s != null && s.Enabled) ?? false)
                                    || !string.IsNullOrWhiteSpace(_settings.App.SubscriptionUrl);

        if (wantsCustomMode && hasActiveSubscription)
        {
            _settings.App.ConfigMode = "subscribe";
            _logger?.Information(
                "[Settings] Subscription is active — keeping ConfigMode=subscribe " +
                "even though Custom sub-tab is selected (user is peeking, not switching)");
        }
        else if (wantsCustomMode && !hasCustomConfig)
        {
            _settings.App.ConfigMode = "generated";
            _logger?.Information(
                "[Settings] User clicked Custom sub-tab but no custom config is configured — keeping ConfigMode=generated instead of 'custom'");
        }
        else
        {
            _settings.App.ConfigMode = IsSubscribeMode ? "subscribe" : IsVlessMode ? "generated" : "custom";
        }

        _settings.App.Subscriptions = Subscriptions.Select(sv => sv.ToEntry()).ToList();

        var activeSub = SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault();
        _settings.App.ActiveSubscriptionServer = activeSub?.Name ?? "";

        _settings.App.SubscriptionUrl = string.Empty;
        _settings.App.SubscriptionServers = new();

        _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

        var appsModeCanon = (RoutingAppsMode ?? "include").Trim().ToLowerInvariant();
        if (appsModeCanon != "include" && appsModeCanon != "exclude") appsModeCanon = "include";
        _settings.App.RoutingAppsMode = appsModeCanon;

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

        _settings.App.BypassRussianTraffic = BypassRussianTraffic;
        _settings.App.CustomRulesPriority = CustomRulesAboveToggles ? "custom_first" : "toggles_first";

        _settings.App.StrictMode = StrictMode;
        var minimumTunMtu = _settings.Tun.Ipv6Enabled
            ? TunSettings.MinimumIpv6Mtu
            : TunSettings.MinimumMtu;
        _settings.Tun.Mtu = Math.Clamp(TunMtu, minimumTunMtu, TunSettings.MaximumMtu);

        _settings.App.ForceIpv4Only = ForceIpv4Only;
        _settings.App.FlushDnsOnStart = FlushDnsOnStart;
        _settings.App.StrictDns = StrictDns;
        _settings.App.BlockAds = BlockAds;
        _settings.Vless.AutoSelectBestServer = AutoSelectBestServer;
        _settings.App.ConnectionIntent = IntentFromIndex(ConnectionIntentIndex);
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

        _settings.Update.Channel = ReceivePrereleases ? "experimental" : "stable";

        _settings.App.Theme = NormalizeThemePref(ThemePreference);
        _settings.App.Language = IsRussian ? "ru" : "en";
        _settings.App.UiMode = IsSimpleMode ? "simple" : "advanced";

        _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
        var activeVless = SelectedServer ?? Servers.FirstOrDefault();
        _settings.Vless.ActiveServer = activeVless?.Name ?? "";
        if (_settings.Vless.Servers.Count > 0)
        {
            var entry = activeVless?.ToEntry() ?? _settings.Vless.Servers[0];
            _settings.Vless.Server = entry.Server;
            _settings.Vless.Port = entry.Port;
            _settings.Vless.Uuid = entry.Uuid;
            _settings.Vless.Flow = entry.Flow;
            _settings.Vless.Security = entry.Security;
            _settings.Vless.Reality = entry.Reality;
        }

        _settings.App.CustomConfigs = CustomConfigs.Select(c => c.ToEntry()).ToList();
        var active = CustomConfigs.FirstOrDefault(c => c.IsActive);
        _settings.App.ActiveCustomConfig = active?.Name ?? "";

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

            var defaultGroupsCount = AppGroups.Count(g => g.Name != "Custom Apps" && !g.IsCustomCategory);
            if (defaultGroupsCount > 0)
            {
                var customGroupApps = new Dictionary<string, List<string>>();
                foreach (var group in AppGroups)
                {
                    if (group.Name == "Custom Apps" || group.IsCustomCategory) continue;
                    var extras = group.Apps.Where(a => a.IsCustom).Select(a => a.ProcessName).ToList();
                    if (extras.Count > 0)
                        customGroupApps[group.Name] = extras;
                }
                _settings.CustomGroupApps = customGroupApps;
            }

            _settings.CustomCategories = AppGroups
                .Where(g => g.IsCustomCategory)
                .Select(g => new CustomCategory
                {
                    Name = g.Name,
                    Enabled = g.IsChecked,
                    Apps = g.Apps.Select(a => a.ProcessName).ToList()
                })
                .ToList();

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

    private void KillAllZapret()
    {
#if PLATFORM_WINDOWS
        try { _zapret?.Stop(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] KillAllZapret: _zapret.Stop failed"); }

        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] KillAllZapret: proc.Kill failed (PID {Pid})", proc.Id);
            }
            finally { proc.Dispose(); }
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("taskkill")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/F");
            psi.ArgumentList.Add("/IM");
            psi.ArgumentList.Add("winws.exe");
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] KillAllZapret: taskkill fallback failed");
        }
#endif
    }

    private bool IsZapretRunning()
    {
#if PLATFORM_WINDOWS
        return VPNRouter.Core.Services.ProcessQuery.AnyAlive("winws");
#else
        return false;
#endif
    }

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
#if PLATFORM_WINDOWS
        var zapretActuallyInstalled = VPNRouter.Core.Services.ZapretUpdater.IsInstalled();
#else
        var zapretActuallyInstalled = false;
#endif
        if (_parsedStrategies.Count == 0 && !zapretActuallyInstalled)
        {
            names.Add("multisplit");
            names.Add("fake+multisplit");
        }
        else if (_parsedStrategies.Count == 0 && zapretActuallyInstalled)
        {
            _logger?.Warning(
                "[VM] LoadZapretStrategies: Zapret install dir exists but ParseStrategies returned 0 — likely install corruption");
        }
        names.Add("custom");

        ZapretStrategies = new System.Collections.ObjectModel.ObservableCollection<string>(names);

        RefreshZapretStrategiesDisplay();

        TryRestoreLastProbeLog();

        var saved = _settings.App.ZapretStrategy;
        var idx = names.IndexOf(saved);
        if (idx < 0
            && _parsedStrategies.Count > 0
            && (string.Equals(saved, "multisplit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(saved, "fake+multisplit", StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.Information(
                "[VM] Migrating saved ZapretStrategy '{Old}' (stub, no longer listed) → '{New}'",
                saved, _parsedStrategies[0].Name);
            idx = names.IndexOf(_parsedStrategies[0].Name);
        }
        ZapretStrategyIndex = idx >= 0 ? idx : 0;
    }

    private void TryRestoreLastProbeLog()
    {
        try
        {
            var logsDir = VPNRouter.Core.AppPaths.LogsDir;
            if (!Directory.Exists(logsDir)) return;
            var newest = Directory.GetFiles(logsDir, "zapret-probe-*.log")
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(newest))
            {
                LastProbeLogPath = newest;
                _logger?.Debug("[VM] TryRestoreLastProbeLog: {Path}", newest);
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VM] TryRestoreLastProbeLog failed (non-fatal)");
        }
    }

    private void RefreshZapretStrategiesDisplay()
    {
        var display = new System.Collections.ObjectModel.ObservableCollection<string>();
        VPNRouter.Core.Services.ZapretProbeCacheEntry? cached = null;
#if PLATFORM_WINDOWS
        try { cached = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger); }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VM] RefreshZapretStrategiesDisplay: probe cache load failed");
        }
#endif

        var winnerName = cached?.Strategy;
        var winnerOk = cached?.IsRecentAndReliable() ?? false;
        var winnerStale = cached?.IsStale() ?? false;
        var hasScore = cached?.HasTargetScore() ?? false;
        var passed = cached?.TargetsPassed ?? 0;
        var total = cached?.TargetsTotal ?? 0;

        var perStrategy = cached?.PerStrategyResults
            ?? new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(StringComparer.Ordinal);

        var newDisplay = new System.Collections.ObjectModel.ObservableCollection<ZapretStrategyDisplayItem>();
        foreach (var name in ZapretStrategies)
        {
            if (!string.IsNullOrEmpty(winnerName)
                && string.Equals(name, winnerName, StringComparison.Ordinal))
            {
                if (winnerOk)
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = hasScore ? $"✓ {passed}/{total}" : "✓",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Success,
                    });
                }
                else if (winnerStale)
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = "⏱",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Stale,
                    });
                }
                else
                {
                    newDisplay.Add(new ZapretStrategyDisplayItem
                    {
                        Glyph = "◌",
                        NameText = name,
                        Kind = ZapretStrategyDisplayKind.Muted,
                    });
                }
                continue;
            }

            if (perStrategy.TryGetValue(name, out var result) && result.Total > 0)
            {
                ZapretStrategyDisplayKind kind;
                string glyph;
                if (result.Passed == result.Total)
                {
                    kind = ZapretStrategyDisplayKind.Success;
                    glyph = $"✓ {result.Passed}/{result.Total}";
                }
                else if (result.Passed == 0)
                {
                    kind = ZapretStrategyDisplayKind.Danger;
                    glyph = $"✗ 0/{result.Total}";
                }
                else
                {
                    kind = ZapretStrategyDisplayKind.Warning;
                    glyph = $"⚠ {result.Passed}/{result.Total}";
                }
                newDisplay.Add(new ZapretStrategyDisplayItem
                {
                    Glyph = glyph,
                    NameText = name,
                    Kind = kind,
                });
            }
            else
            {
                newDisplay.Add(new ZapretStrategyDisplayItem
                {
                    Glyph = "◌",
                    NameText = name,
                    Kind = ZapretStrategyDisplayKind.Muted,
                });
            }
        }
        ZapretStrategiesDisplay = newDisplay;
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
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretStatus = Strings.Stopped;
            SaveSettings();
            return;
        }

        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            await UpdateZapretAsync();
            if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
        }

        try
        {
            if (_zapret == null)
            {
                _zapret = new ZapretManager(_logger);
                _zapret.ImmediateExitDetected += OnZapretImmediateExit;
            }
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
                if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                    _zapret.StartFromBat(parsed.BatPath, parsed.Arguments);
                else
                    _zapret.Start(parsed.Arguments);
            }

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

    public string LblZapretHeroTitle
    {
        get
        {
            if (IsZapretProbing) return Strings.ZapretOneTapTitleProbing;
            if (ZapretEnabled && !string.IsNullOrEmpty(ZapretWinningStrategy))
                return Strings.ZapretOneTapTitleRunning(ZapretWinningStrategy);
            if (IsZapretFallback) return Strings.ZapretOneTapTitleFallback;
            return Strings.ZapretOneTapTitleStopped;
        }
    }

    public string LblZapretHeroLede
    {
        get
        {
            if (IsZapretProbing && ZapretProbeTotal > 0)
            {
                var name = string.IsNullOrEmpty(ZapretProbeStrategy) ? "..." : ZapretProbeStrategy;
                if (ZapretProbeTotalCount > 0)
                    return Strings.ZapretOneTapLedeProbingScored(
                        ZapretProbeIndex + 1, ZapretProbeTotal, name,
                        ZapretProbePassCount, ZapretProbeTotalCount);
                return Strings.ZapretOneTapLedeProbing(
                    ZapretProbeIndex + 1, ZapretProbeTotal, name);
            }
            if (ZapretEnabled) return Strings.ZapretOneTapLedeRunning;
            if (IsZapretFallback) return Strings.ZapretOneTapLedeFallback;
            return Strings.ZapretOneTapLedeStopped;
        }
    }

    public string LblZapretMagicButton => ZapretEnabled
        ? Strings.ZapretOneTapStopButton
        : Strings.ZapretOneTapStartButton;

    public bool IsZapretMagicButtonEnabled => !IsZapretDownloading && !IsZapretProbing;

    public string LblZapretAirPill
    {
        get
        {
            var name = string.IsNullOrEmpty(ZapretWinningStrategy) ? "..." : ZapretWinningStrategy;
            if (ZapretProbeTotalCount > 0)
                return Strings.ZapretOneTapAirPillScored(name, ZapretProbePassCount, ZapretProbeTotalCount);
            var pid = ZapretManager.WinwsPid ?? 0;
            return Strings.ZapretOneTapAirPill(name, pid);
        }
    }

    public string L_ZapretOneTapTune => Strings.ZapretOneTapTune;

    public string L_ZapretOneTapStep1 => Strings.ZapretOneTapStep1;
    public string L_ZapretOneTapStep2 => Strings.ZapretOneTapStep2;
    public string L_ZapretOneTapStep3 => Strings.ZapretOneTapStep3;

    public string L_ZapretForceFreshProbeButton => Strings.ZapretForceFreshProbeButton;
    public string L_ZapretClearCacheButton => Strings.ZapretClearCacheButton;

    public string L_ZapretReverifyButton => Strings.ZapretReverifyButton;
    public string L_ZapretReverifyHint => Strings.ZapretReverifyHint;
    public string L_ZapretSummaryDetailsButton => Strings.ZapretSummaryDetailsButton;
    public string L_ZapretSummaryStaleHint => Strings.ZapretSummaryStaleHint;
    public string L_ZapretCancelProbeButton => Strings.ZapretCancelProbeButton;

    public string L_TgProxyTabSettings => Strings.TgProxyTabSettings;
    public string L_TgProxyTabVersion  => Strings.TgProxyTabVersion;
    public string L_TgProxyTabHelp     => Strings.TgProxyTabHelp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab0))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab1))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyTab2))]
    private int _tgProxyActiveTabIndex;

    public bool IsTgProxyTab0 => TgProxyActiveTabIndex == 0;
    public bool IsTgProxyTab1 => TgProxyActiveTabIndex == 1;
    public bool IsTgProxyTab2 => TgProxyActiveTabIndex == 2;

    [RelayCommand]
    private void SetTgProxyTab(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx) && idx >= 0 && idx <= 2)
            TgProxyActiveTabIndex = idx;
    }

    [RelayCommand]
    private async Task ZapretOneClickAsync()
    {
#if PLATFORM_WINDOWS
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            ZapretProbePassCount = 0;
            ZapretProbeTotalCount = 0;
            IsZapretFallback = false;
            ZapretStatus = Strings.Stopped;
            SaveSettings();
            return;
        }

        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            ZapretStatus = Strings.ZapretOneTapDownloading;
            await UpdateZapretAsync();
            if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
        }
        else
        {
            try
            {
                var remoteTag = await VPNRouter.Core.Services.RemoteVersionChecker.GetLatestTagAsync(
                    VPNRouter.Core.Services.ZapretUpdater.FlowsealRepoPublic,
                    userAgent: $"VPNRouter/{VPNRouter.Core.AppVersion.Version}",
                    _logger,
                    System.Threading.CancellationToken.None);
                var localTag = VPNRouter.Core.Services.ZapretUpdater.GetLocalVersion();
                if (VPNRouter.Core.Services.RemoteVersionChecker.IsNewer(remoteTag, localTag))
                {
                    _logger.Information(
                        "[VM] OneTap: Zapret update available {Local} → {Remote}, auto-applying",
                        localTag, remoteTag);
                    ZapretStatus = IsRussian
                        ? $"Обновление Zapret до {remoteTag}…"
                        : $"Updating Zapret to {remoteTag}…";
                    await UpdateZapretAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] OneTap: Zapret remote version check failed (non-fatal)");
            }
        }

        if (!DiscordHostsInstalled)
        {
            try
            {
                ZapretStatus = Strings.ZapretOneTapInstallingHosts;
                ToggleDiscordHosts();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Discord hosts install failed (non-fatal, continuing to probe)");
            }
        }

        if (!FlowsealHostsInstalled)
        {
            try
            {
                await ToggleFlowsealHostsAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Flowseal hosts install failed (non-fatal, continuing to probe)");
            }
        }

        if (!ZapretActions.IsGameFilterConfigured)
        {
            try
            {
                ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.All);
                GameFilterModeIndex = (int)ZapretActions.GameFilterMode.All;
                _logger.Information("[VM] OneTap: Game filter set to All (first-time default — covers games on UDP)");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] OneTap: Game filter default-set failed (non-fatal)");
            }
        }

        await ProbeAndStartZapretAsync();
#endif
    }

    private bool _forceFreshProbe;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretProbeElapsed))]
    private int _zapretProbeElapsedSeconds;

    private DateTime _zapretProbeStartTime;
    private System.Threading.Timer? _zapretProbeElapsedTimer;

    public string LblZapretProbeElapsed
    {
        get
        {
            if (!IsZapretProbing || ZapretProbeElapsedSeconds <= 0)
                return string.Empty;
            int? etaSec = null;
            if (ZapretProbeIndex > 0 && ZapretProbeTotal > 0)
            {
                var perConfig = (double)ZapretProbeElapsedSeconds / Math.Max(1, ZapretProbeIndex);
                var remaining = Math.Max(0, ZapretProbeTotal - ZapretProbeIndex);
                etaSec = (int)(perConfig * remaining);
            }
            return Strings.ZapretProbeElapsedAndEta(ZapretProbeElapsedSeconds, etaSec);
        }
    }

    public string L_ZapretStartSelectedStrategyButton => Strings.ZapretStartSelectedStrategyButton;
    public string L_ZapretStartSelectedStrategyHint => Strings.ZapretStartSelectedStrategyHint;

    [RelayCommand]
    private async Task StartZapretWithSelectedStrategyAsync()
    {
#if PLATFORM_WINDOWS
        var idx = ZapretStrategyIndex;
        if (idx < 0 || idx >= ZapretStrategies.Count)
        {
            _logger.Warning("[VM] StartZapretWithSelectedStrategy: invalid index {Idx}", idx);
            return;
        }
        var strategyName = ZapretStrategies[idx];
        if (string.IsNullOrEmpty(strategyName))
        {
            _logger.Warning("[VM] StartZapretWithSelectedStrategy: empty name at {Idx}", idx);
            return;
        }

        if (IsZapretProbing)
        {
            _logger.Information("[VM] StartZapretWithSelectedStrategy: a probe is already running — refusing");
            return;
        }
        KillAllZapret();
        if (ZapretEnabled || IsZapretRunning())
        {
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            await Task.Delay(500);
        }

        if (_zapret == null)
        {
            _zapret = new ZapretManager(_logger);
            _zapret.ImmediateExitDetected += OnZapretImmediateExit;
        }

        ZapretStatus = Strings.ZapretStartingSelected(strategyName);

        try
        {
            var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategyName);
            if (parsed == null)
            {
                if (string.IsNullOrWhiteSpace(ZapretCustomArgs))
                {
                    _logger.Warning(
                        "[VM] Selected strategy {Name} not in parsed list AND ZapretCustomArgs is empty — refusing to spawn winws with no args",
                        strategyName);
                    ZapretStatus = IsRussian
                        ? $"Стратегия «{strategyName}» не настроена (пустые аргументы). Выбери другую."
                        : $"Strategy '{strategyName}' is not configured (empty arguments). Pick another.";
                    return;
                }
                _logger.Warning("[VM] Selected strategy {Name} not in parsed list — using custom args path", strategyName);
                _zapret!.Start(ZapretCustomArgs);
            }
            else if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
            {
                _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
            }
            else
            {
                _zapret!.Start(parsed.Arguments);
            }

            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (_zapret.IsRunning || winwsPid != null)
            {
                ZapretEnabled = true;
                ZapretWinningStrategy = strategyName;
                ZapretProbePassCount = 0;
                ZapretProbeTotalCount = 0;
                IsZapretFallback = false;
                var pid = winwsPid ?? _zapret.Pid ?? 0;
                ZapretStatus = Strings.ZapretRunningSelected(strategyName, pid);
                VPNRouter.Core.Services.ZapretProbeCache.RecordSuccess(strategyName, _logger);
                RefreshZapretStrategiesDisplay();
                SaveSettings();
            }
            else
            {
                ZapretEnabled = false;
                IsZapretFallback = true;
                ZapretStatus = Strings.ZapretSelectedStrategyFailed(strategyName);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] StartZapretWithSelectedStrategy threw");
            ZapretStatus = Strings.ZapretSelectedStrategyFailed(strategyName) + " (" + ex.Message + ")";
        }
        NotifyZapretSummaryChanged();
#else
        await Task.CompletedTask;
#endif
    }

#if PLATFORM_WINDOWS
    private void OnTgProxyStats(string stats)
        => Dispatcher.UIThread.Post(() => TgProxyStats = ParseStatsShort(stats));
#endif

    private void StartZapretProbeElapsedTimer()
    {
        _zapretProbeStartTime = DateTime.UtcNow;
        ZapretProbeElapsedSeconds = 0;
        _zapretProbeElapsedTimer?.Dispose();
        _zapretProbeElapsedTimer = new System.Threading.Timer(_ =>
        {
            if (_disposed) return;
            try
            {
                var elapsed = (int)(DateTime.UtcNow - _zapretProbeStartTime).TotalSeconds;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ZapretProbeElapsedSeconds = elapsed;
                });
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[VM] Probe elapsed tick failed");
            }
        }, null, dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
    }

    private void StopZapretProbeElapsedTimer()
    {
        _zapretProbeElapsedTimer?.Dispose();
        _zapretProbeElapsedTimer = null;
        ZapretProbeElapsedSeconds = 0;
    }

    public string LblZapretCacheStatus
    {
        get
        {
            var entry = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (entry == null || string.IsNullOrEmpty(entry.Strategy))
                return Strings.ZapretCacheEmpty;
            return Strings.ZapretCacheInfo(entry.Strategy, entry.SuccessRunCount);
        }
    }

    public bool IsZapretSummaryVisible
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            return e != null && !string.IsNullOrEmpty(e.Strategy);
        }
    }

    public bool IsZapretCacheStale
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            return e != null && e.IsStale();
        }
    }

    public string LblZapretSummaryHeader
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (e == null || string.IsNullOrEmpty(e.Strategy)) return string.Empty;
            return e.IsStale()
                ? Strings.ZapretSummaryHeaderStale(e.Strategy)
                : Strings.ZapretSummaryHeaderFresh(e.Strategy);
        }
    }

    public string LblZapretSummarySubtext
    {
        get
        {
            var e = VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (e == null) return string.Empty;
            var rel = FormatRelativeTime(e.LastSweepAt);
            return e.HasTargetScore()
                ? Strings.ZapretSummarySubtextWithScore(e.TargetsPassed, e.TargetsTotal, rel)
                : Strings.ZapretSummarySubtextNoScore(rel);
        }
    }

    private string FormatRelativeTime(DateTime utcWhen)
    {
        var delta = DateTime.UtcNow - utcWhen;
        if (delta < TimeSpan.FromMinutes(1)) return Strings.RelativeTimeJustNow;
        if (delta < TimeSpan.FromHours(1))
        {
            var m = Math.Max(1, (int)delta.TotalMinutes);
            return Strings.RelativeTimeMinutes(m);
        }
        if (delta < TimeSpan.FromDays(1))
        {
            var h = Math.Max(1, (int)delta.TotalHours);
            return Strings.RelativeTimeHours(h);
        }
        if (delta < TimeSpan.FromDays(30))
        {
            var d = Math.Max(1, (int)delta.TotalDays);
            return Strings.RelativeTimeDays(d);
        }
        return Strings.RelativeTimeLongAgo;
    }

    private void NotifyZapretSummaryChanged()
    {
        OnPropertyChanged(nameof(IsZapretSummaryVisible));
        OnPropertyChanged(nameof(IsZapretCacheStale));
        OnPropertyChanged(nameof(LblZapretSummaryHeader));
        OnPropertyChanged(nameof(LblZapretSummarySubtext));
        OnPropertyChanged(nameof(LblZapretCacheStatus));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretTab0))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab1))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab2))]
    [NotifyPropertyChangedFor(nameof(IsZapretTab3))]
    private int _zapretActiveTabIndex;

    private CancellationTokenSource? _zapretProbeCts;

    [RelayCommand]
    private void CancelZapretProbe()
    {
        try
        {
            _zapretProbeCts?.Cancel();
            _logger.Information("[VM] Zapret probe cancellation requested by user");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] CancelZapretProbe failed");
        }
    }

    public bool IsZapretTab0 => ZapretActiveTabIndex == 0;
    public bool IsZapretTab1 => ZapretActiveTabIndex == 1;
    public bool IsZapretTab2 => ZapretActiveTabIndex == 2;
    public bool IsZapretTab3 => ZapretActiveTabIndex == 3;

    [RelayCommand]
    private void SetZapretTab(string indexStr)
    {
        if (int.TryParse(indexStr, out var idx) && idx >= 0 && idx <= 3)
            ZapretActiveTabIndex = idx;
    }

    [RelayCommand]
    private void ExpandZapretTuneSection()
    {
        ZapretActiveTabIndex = 3;
    }

    [RelayCommand]
    private void ClearZapretCache()
    {
        try
        {
            VPNRouter.Core.Services.ZapretProbeCache.Clear(_logger);
            NotifyZapretSummaryChanged();
            ZapretStatus = Strings.ZapretCacheCleared;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] ClearZapretCache failed");
        }
    }

    [RelayCommand]
    private async Task ForceFreshProbeAsync()
    {
#if PLATFORM_WINDOWS
        if (ZapretEnabled || IsZapretRunning())
        {
            KillAllZapret();
            ZapretEnabled = false;
            ZapretWinningStrategy = string.Empty;
            ZapretProbePassCount = 0;
            ZapretProbeTotalCount = 0;
            await Task.Delay(500);
        }
        _forceFreshProbe = true;
        try
        {
            await ProbeAndStartZapretAsync();
        }
        finally
        {
            _forceFreshProbe = false;
            NotifyZapretSummaryChanged();
        }
#else
        await Task.CompletedTask;
#endif
    }
#if PLATFORM_WINDOWS

    private async Task ProbeAndStartZapretAsync()
    {
        if (_zapret == null)
        {
            _zapret = new ZapretManager(_logger);
            _zapret.ImmediateExitDetected += OnZapretImmediateExit;
        }

        IsZapretProbing = true;
        IsZapretFallback = false;
        ZapretWinningStrategy = string.Empty;
        _suppressZapretAvToast = true;
        StartZapretProbeElapsedTimer();

        try
        {
            var zapretDir = VPNRouter.Core.Services.ZapretUpdater.ZapretDir;

            try
            {
                if (VPNRouter.Core.Services.ZapretAutoStrategy.HasOrphanedIpsetFlag(zapretDir))
                {
                    VPNRouter.Core.Services.ZapretAutoStrategy.RestoreIpsetAfterKill(zapretDir, _logger);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] Pre-probe ipset cleanup failed (continuing anyway)");
            }

            var cached = _forceFreshProbe
                ? null
                : VPNRouter.Core.Services.ZapretProbeCache.TryLoad(_logger);
            if (cached != null && cached.IsRecentAndReliable())
            {
                _logger.Information(
                    "[VM] ZapretOneTap cache hit: trying {Strategy} (success count {N})",
                    cached.Strategy, cached.SuccessRunCount);

                ZapretProbeStrategy = cached.Strategy;
                ZapretProbeIndex = 0;
                ZapretProbeTotal = 1;
                var hit = await TryApplyCachedWinnerAsync(cached.Strategy);
                if (hit)
                {
                    VPNRouter.Core.Services.ZapretProbeCache.RecordSuccess(
                        cached.Strategy, cached.TargetsPassed, cached.TargetsTotal, _logger);
                    return;
                }
                else
                {
                    VPNRouter.Core.Services.ZapretProbeCache.RecordFailure(cached.Strategy, _logger);
                    _logger.Information("[VM] Cache miss path — running full sweep");
                }
            }
            else if (cached != null)
            {
                _logger.Information(
                    "[VM] Cache entry stale or unreliable (last sweep {LastSweep}, fails {Fails}) — running full sweep",
                    cached.LastSweepAt, cached.LastFailureCount);
            }

            var flowsealProgress = new Progress<VPNRouter.Core.Services.ZapretAutoStrategy.FlowsealProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.StrategyName))
                {
                    ZapretProbeIndex = p.CurrentIndex - 1;
                    ZapretProbeTotal = p.TotalCount;
                    ZapretProbeStrategy = p.StrategyName;
                    ZapretProbePassCount = 0;
                    ZapretProbeTotalCount = 0;
                    _logger.Information("[VM] ZapretOneTap Flowseal probe: {Index}/{Total} {Name}",
                        p.CurrentIndex, p.TotalCount, p.StrategyName);
                }
                else if (p.TotalChecks > 0)
                {
                    ZapretProbePassCount = p.OkCount;
                    ZapretProbeTotalCount = p.TotalChecks;
                    if (p.TotalChecks % 6 == 0)
                    {
                        _logger.Information(
                            "[VM] ZapretOneTap Flowseal score: {Ok}/{Total} on {Strategy}",
                            p.OkCount, p.TotalChecks, ZapretProbeStrategy);
                    }
                }
            });

            _zapretProbeCts?.Dispose();
            _zapretProbeCts = new CancellationTokenSource();
            ZapretAutoStrategy.FlowsealSweepResult sweep;
            try
            {
                sweep = await VPNRouter.Core.Services.ZapretAutoStrategy.RunFlowsealProbeAsync(
                    zapretDir, flowsealProgress, _logger, _zapretProbeCts.Token);
            }
            finally
            {
                _zapretProbeCts.Dispose();
                _zapretProbeCts = null;
            }

            LastProbeLogPath = sweep.ProbeLogPath;
            if (sweep.EarlyWinner)
            {
                _logger.Information(
                    "[VM] Probe early-exit: winner {Name} at config {N}/{T} — skipped remaining {M}",
                    sweep.Winner, sweep.TestedCount, sweep.TotalCount,
                    sweep.TotalCount - sweep.TestedCount);
            }

            if (sweep.Winner != null)
            {
                static string NormStrategy(string? s) =>
                    (s ?? string.Empty).Trim().TrimEnd().Replace(".bat", "",
                        StringComparison.OrdinalIgnoreCase).Trim();
                var winnerNorm = NormStrategy(sweep.Winner);
                var parsed = _parsedStrategies.FirstOrDefault(s =>
                        string.Equals(NormStrategy(s.Name), winnerNorm, StringComparison.OrdinalIgnoreCase));
                if (parsed != null)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                            _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
                        else
                            _zapret!.Start(parsed.Arguments);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "[VM] Failed to start winning strategy {Name}", sweep.Winner);
                        IsZapretFallback = true;
                        ZapretEnabled = false;
                        ZapretStatus = $"Error starting {sweep.Winner}: {ex.Message}";
                        return;
                    }

                    await Task.Delay(1500);
                    var winwsPid = ZapretManager.WinwsPid;
                    if (_zapret.IsRunning || winwsPid != null)
                    {
                        ZapretWinningStrategy = sweep.Winner;
                        ZapretEnabled = true;
                        var pid = winwsPid ?? _zapret.Pid;
                        ZapretStatus = IsRussian
                            ? $"Работает [{sweep.Winner}] (PID {pid})"
                            : $"Running [{sweep.Winner}] (PID {pid})";

                        var idx = ZapretStrategies.IndexOf(sweep.Winner);
                        if (idx >= 0) ZapretStrategyIndex = idx;
                        var perStrategy = sweep.PerStrategyResults != null
                            ? new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(
                                sweep.PerStrategyResults, StringComparer.Ordinal)
                            : new System.Collections.Generic.Dictionary<string, VPNRouter.Core.Services.ZapretStrategyTestResult>(StringComparer.Ordinal);
                        VPNRouter.Core.Services.ZapretProbeCache.RecordSweepResults(
                            sweep.Winner,
                            ZapretProbePassCount,
                            ZapretProbeTotalCount,
                            perStrategy,
                            _logger);
                        RefreshZapretStrategiesDisplay();
                        NotifyZapretSummaryChanged();
                        SaveSettings();
                    }
                    else
                    {
                        IsZapretFallback = true;
                        ZapretEnabled = false;
                        ZapretStatus = IsRussian
                            ? $"Стратегия {sweep.Winner} не запустилась"
                            : $"Strategy {sweep.Winner} failed to start";
                    }
                }
                else
                {
                    _logger.Warning("[VM] Flowseal winner {Name} not in parsed list", sweep.Winner);
                    IsZapretFallback = true;
                    ZapretEnabled = false;
                    ZapretStatus = $"Winner {sweep.Winner} not found in strategy list";
                }
            }
            else
            {
                IsZapretFallback = true;
                ZapretEnabled = false;
                ZapretStatus = sweep.Diagnostic switch
                {
                    "not_admin" => IsRussian
                        ? "Нужны права администратора для подбора стратегии. Перезапустите VPNRouter от админа."
                        : "Administrator rights required to probe strategies. Restart VPNRouter as admin.",
                    "sweep_timeout" => IsRussian
                        ? "Подбор стратегии превысил 10 минут. Проверьте интернет и попробуйте ещё раз."
                        : "Strategy probe exceeded 10 min cap. Check network and retry.",
                    "missing_script" => IsRussian
                        ? "Скрипт Flowseal не найден. Обнови Zapret через «Тонкую настройку»."
                        : "Flowseal script missing. Update Zapret via Advanced settings.",
                    "canceled" => IsRussian
                        ? "Подбор отменён."
                        : "Probe canceled.",
                    _ => Strings.ZapretOneTapAllFailedToast,
                };

                if (sweep.ErrorLines is { Count: > 0 })
                {
                    foreach (var errLine in sweep.ErrorLines)
                        _logger.Warning("[VM] Flowseal script: {Line}", errLine);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ZapretOneTap probe orchestrator failed");
            ZapretEnabled = false;
            IsZapretFallback = true;
            ZapretStatus = $"Error: {ex.Message}";
        }
        finally
        {
            try
            {
                var zd = VPNRouter.Core.Services.ZapretUpdater.ZapretDir;
                VPNRouter.Core.Services.ZapretAutoStrategy.RestoreIpsetAfterKill(zd, _logger);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] Post-probe ipset cleanup failed");
            }

            IsZapretProbing = false;
            _suppressZapretAvToast = false;
            ZapretProbeIndex = 0;
            ZapretProbeTotal = 0;
            ZapretProbeStrategy = string.Empty;
            StopZapretProbeElapsedTimer();
        }
    }

    private async Task<bool> TryApplyCachedWinnerAsync(string strategy)
    {
        try
        {
            var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategy);
            if (parsed == null)
            {
                _logger.Warning("[VM] Cached strategy {Name} not in parsed list — bypass cache", strategy);
                return false;
            }

            ZapretProbeStrategy = strategy;
            if (!string.IsNullOrEmpty(parsed.BatPath) && File.Exists(parsed.BatPath))
                _zapret!.StartFromBat(parsed.BatPath, parsed.Arguments);
            else
                _zapret!.Start(parsed.Arguments);

            await Task.Delay(1500);
            var winwsPid = ZapretManager.WinwsPid;
            if (!_zapret.IsRunning && winwsPid == null)
            {
                _logger.Warning("[VM] Cached strategy {Name} failed to spawn winws.exe", strategy);
                return false;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            var targets = VPNRouter.Core.Services.ZapretAutoStrategy.LoadTargets(_logger);
            var report = await VPNRouter.Core.Services.ZapretAutoStrategy.ProbeAllTargetsAsync(
                targets, http, _logger, CancellationToken.None);
            var passPercent = targets.Count == 0 ? 0 : (report.PassCount * 100) / targets.Count;
            _logger.Information(
                "[VM] Cache warm-start probe: {Pass}/{Total} ok ({Pct}%) on {Strategy}",
                report.PassCount, targets.Count, passPercent, strategy);

            if (passPercent >= VPNRouter.Core.Services.ZapretAutoStrategy.Tier2MinPassPercent)
            {
                ZapretWinningStrategy = strategy;
                ZapretEnabled = true;
                ZapretProbePassCount = report.PassCount;
                ZapretProbeTotalCount = targets.Count;
                var pid = winwsPid ?? _zapret.Pid;
                ZapretStatus = IsRussian
                    ? $"Работает [{strategy}] (PID {pid}, warm)"
                    : $"Running [{strategy}] (PID {pid}, warm)";
                var idx = ZapretStrategies.IndexOf(strategy);
                if (idx >= 0) ZapretStrategyIndex = idx;
                SaveSettings();
                return true;
            }

            _logger.Warning("[VM] Cache warm-start probe under threshold — stopping for fresh sweep");
            try { _zapret?.Stop(); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] TryApplyCachedWinnerAsync threw");
            try { _zapret?.Stop(); } catch { }
            return false;
        }
    }

#endif

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

    [RelayCommand]
    private async Task UpdateTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (_disposed) return;
        if (!await _tgProxyTransitionGate.WaitAsync(0)) return;
        try
        {
            if (!_disposed) await UpdateTgProxyCoreAsync();
        }
        finally { _tgProxyTransitionGate.Release(); }
#endif
    }

    private async Task UpdateTgProxyCoreAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;
        IsTgProxyDownloading = true;
        TgProxyStatus = IsRussian ? "Загрузка tg-ws-proxy..." : "Downloading tg-ws-proxy...";
        TgProxyDownloadStep = string.Empty;

        var port = TgProxyPort;
        var secret = TgProxySecret;
        TgProxyManager? manager;
        lock (_tgProxyStateGate) manager = _tgProxy;
        var wasRunning = manager?.IsRunning == true;

        try
        {
            if (wasRunning)
            {
                manager?.Stop();
                if (manager?.IsRunning == true)
                {
                    TgProxyRuntimeStatus = ComponentRuntimeStatus.Failed;
                    TgProxyStatus = VPNRouter.Core.Localization.Strings.TgProxyStopFailed;
                    return;
                }
                TgProxyEnabled = false;
            }

            var updater = new TgProxyUpdater(_logger);
            updater.StatusChanged += s =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed) return;
                    TgProxyStatus = s;
                    TgProxyDownloadStep = s.StartsWith("Step ") ? s : string.Empty;
                });

            await updater.DownloadAsync(_tgProxyLifetimeCts.Token);

            if (_disposed) return;

            TgProxyVersionText = TgProxyUpdater.GetLocalVersion() ?? "?";
            if (wasRunning)
            {
                if (string.IsNullOrWhiteSpace(secret))
                    throw new InvalidOperationException("TgProxy secret is empty after update.");

                lock (_tgProxyStateGate)
                {
                    if (_disposed) return;
                    if (_tgProxy == null)
                    {
                        _tgProxy = new TgProxyManager(_logger);
                        _tgProxy.StatsUpdated += OnTgProxyStats;
                    }
                    manager = _tgProxy;
                }

                manager.Start(port, secret);
                if (_disposed || !ReferenceEquals(_tgProxy, manager))
                {
                    manager.Stop();
                    return;
                }

                TgProxyEnabled = true;
                TgProxyRuntimeStatus = ComponentRuntimeStatus.Running;
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", port, secret);
                TgProxyStatus = IsRussian
                    ? $"Обновлено до {TgProxyVersionText}, работает (PID {manager.Pid})"
                    : $"Updated to {TgProxyVersionText}, running (PID {manager.Pid})";
                _tgProxyPostStartRecheckTask = VerifyTgProxyAfterStartAsync(manager, port);
                try { SaveSettings(); }
                catch (System.IO.IOException ex)
                {
                    _logger.Warning(ex, "[VM] TgProxy Update: SaveSettings failed, keeping runtime state");
                }
            }
            else
            {
                TgProxyStatus = IsRussian
                    ? $"tg-ws-proxy {TgProxyVersionText} установлен"
                    : $"tg-ws-proxy {TgProxyVersionText} installed";
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
            _logger.Debug("[VM] TgProxy download cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy download failed");
            TgProxyStatus = $"Download error: {ex.Message}";
        }
        finally
        {
            IsTgProxyDownloading = false;
            TgProxyDownloadStep = string.Empty;
        }
#endif
    }

    [RelayCommand]
    private async Task ToggleTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (_disposed) return;
        if (!await _tgProxyTransitionGate.WaitAsync(0)) return;
        try
        {
            if (!_disposed) await ToggleTgProxyCoreAsync();
        }
        finally { _tgProxyTransitionGate.Release(); }
#endif
    }

    private async Task ToggleTgProxyCoreAsync()
    {
#if PLATFORM_WINDOWS
        TgProxyManager? currentManager;
        lock (_tgProxyStateGate) currentManager = _tgProxy;

        var shouldStop = TgProxyEnabled || currentManager?.IsRunning == true;
        if (shouldStop)
        {
            currentManager?.Stop();

            if (currentManager?.IsRunning == true)
            {
                TgProxyRuntimeStatus = ComponentRuntimeStatus.Failed;
                TgProxyStatus = VPNRouter.Core.Localization.Strings.TgProxyStopFailed;
            }
            else
            {
                TgProxyRuntimeStatus = ComponentRuntimeStatus.Idle;
                TgProxyEnabled = false;
                TgProxyStatus = Strings.Stopped;
                TgProxyStats = "";
            }
            try { SaveSettings(); }
            catch (System.IO.IOException ex)
            {
                _logger.Warning(ex, "[VM] TgProxy Stop: SaveSettings failed (file lock?), keeping in-memory state");
            }
            return;
        }

        _logger.Information("[VM] ToggleTgProxyAsync: start path entered");

        if (!TgProxyUpdater.IsInstalled(_logger))
        {
            await UpdateTgProxyCoreAsync();
            if (!TgProxyUpdater.IsInstalled(_logger)) return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(TgProxySecret))
            {
                TgProxySecret = Convert.ToHexStringLower(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            }

            _logger.Information(
                "[VM] ToggleTgProxyAsync: secret configured (len {SecretLen}), port {Port}, calling TgProxyManager.Start",
                TgProxySecret.Length, TgProxyPort);

            TgProxyManager manager;
            lock (_tgProxyStateGate)
            {
                if (_disposed) return;
                if (_tgProxy == null)
                {
                    _tgProxy = new TgProxyManager(_logger);
                    _tgProxy.StatsUpdated += OnTgProxyStats;
                }
                manager = _tgProxy;
            }
            var port = TgProxyPort;
            var secret = TgProxySecret;
            manager.Start(port, secret);

            if (_disposed || !ReferenceEquals(_tgProxy, manager))
            {
                manager.Stop();
                return;
            }

            IsTelegramSchemeWarningVisible = !TgProxyManager.IsTelegramSchemeRegistered();

            if (manager.IsRunning)
            {
                TgProxyEnabled = true;
                TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", port, secret);
                TgProxyStatus = $"{Strings.StatusRunning} (PID {manager.Pid})";

                _tgProxyPostStartRecheckTask = VerifyTgProxyAfterStartAsync(manager, port);
            }
            else
            {
                TgProxyEnabled = false;
                TgProxyStatus = Strings.TgProxyExitedImmediately;
            }
            try { SaveSettings(); }
            catch (System.IO.IOException ex)
            {
                _logger.Warning(ex, "[VM] TgProxy Start: SaveSettings failed (file lock?), keeping in-memory state");
            }
        }
        catch (TgProxyPortConflictException portEx)
        {
            _logger.Warning(portEx,
                "[VM] TgProxy start blocked: port {Port} busy (owner hint: {Owner})",
                portEx.Port, portEx.OwnerProcessHint ?? "<unknown>");
            TgProxyEnabled = false;
            TgProxyStatus = portEx.OwnerProcessHint is null
                ? string.Format(Strings.TgProxyPortBusy, portEx.Port)
                : string.Format(Strings.TgProxyPortBusyWithOwner, portEx.Port, portEx.OwnerProcessHint);
            ShowTgProxyToast(TgProxyStatus);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] TgProxy start failed");
            TgProxyStatus = $"Error: {ex.Message}";
            TgProxyEnabled = false;
        }
#endif
    }

#if PLATFORM_WINDOWS
    private async Task VerifyTgProxyAfterStartAsync(TgProxyManager manager, int port)
    {
        await Task.Delay(TgProxySettleDelayMs);
        if (_disposed || !ReferenceEquals(_tgProxy, manager) || !TgProxyEnabled)
            return;
        if (manager.IsRunning)
            return;

        TgProxyEnabled = false;
        TgProxyRuntimeStatus = ComponentRuntimeStatus.Failed;
        TgProxyStatus = Strings.TgProxyExitedImmediately;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "[VM] TgProxy post-start recheck: SaveSettings failed, keeping in-memory failure state");
        }
    }
#endif

    [RelayCommand]
    private void CopyTgProxyLink()
    {
        if (string.IsNullOrEmpty(TgProxyLink)) return;
        CopyToClipboard(TgProxyLink);
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void OpenTgProxyInTelegram()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;

        if (!TgProxyManager.IsTelegramSchemeRegistered())
        {
            ShowTgProxyToast(Strings.TgProxyTelegramNotInstalled);
            return;
        }

        TgProxyManager.OpenInTelegram("127.0.0.1", TgProxyPort, TgProxySecret);
    }

    [RelayCommand]
    private async Task SetupTgProxyAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        if (!TgProxyEnabled)
        {
            await ToggleTgProxyAsync();
        }

        var startedPort = TgProxyPort;
        var startedSecret = TgProxySecret;
        var postStartRecheck = _tgProxyPostStartRecheckTask;
        if (postStartRecheck != null)
            await postStartRecheck;

        if (TgProxyEnabled && !string.IsNullOrEmpty(TgProxySecret))
        {
            if (!TgProxyManager.IsTelegramSchemeRegistered())
                ShowTgProxyToast(Strings.TgProxyTelegramNotInstalled);
            else
                TgProxyManager.OpenInTelegram("127.0.0.1", startedPort, startedSecret);
        }
#else
        await Task.CompletedTask;
#endif
    }

    [RelayCommand]
    private async Task TgProxyMainActionAsync()
    {
#if PLATFORM_WINDOWS
        if (IsTgProxyDownloading) return;

        if (TgProxyEnabled)
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
    private void DismissTelegramSchemeWarning()
    {
        IsTelegramSchemeWarningVisible = false;
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
        try
        {
            if (Directory.Exists(path))
            {
                ProcessStartInfo psi;
                if (OperatingSystem.IsWindows())
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        UseShellExecute = false
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open",
                        UseShellExecute = false
                    };
                }
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
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
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            else
            {
                Serilog.Log.Logger.Warning("[VM] OpenUrl blocked non-http(s) URL: {Url}", CanaryPolicy.RedactUrl(url));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "[VM] OpenUrl failed: {Url}", CanaryPolicy.RedactUrl(url));
        }
    }

    [RelayCommand]
    private void CopyTgProxySecret()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;
        CopyToClipboard(TgProxySecret);
        ShowTgProxyToast(Strings.TgProxyCopied);
    }

    [RelayCommand]
    private void RegenerateTgProxySecret()
    {
#if PLATFORM_WINDOWS
        TgProxyManager? manager;
        lock (_tgProxyStateGate) manager = _tgProxy;
        var wasRunning = TgProxyEnabled || manager?.IsRunning == true;
#else
        var wasRunning = TgProxyEnabled;
#endif

        TgProxySecret = Convert.ToHexStringLower(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        SaveSettings();

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

    private void ShowTgProxyToast(string message)
    {
        TgProxyToast = message;
        var token = ++_tgProxyToastToken;
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
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

    private static string ParseStatsShort(string statsLine)
    {
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
        if (active != null) sb.Append($"{Strings.TgProxyStatsActive}: {active}");
        if (total != null) sb.Append($" | {Strings.TgProxyStatsTotal}: {total}");
        if (up != null) sb.Append($" | \u2191{up}");
        if (down != null) sb.Append($" \u2193{down}");
        return sb.ToString();
    }

    [RelayCommand]
    private void AddServer()
    {
        var rawInput = (VlessUri ?? string.Empty).Trim();
        var addedAny = false;

        if (ServerUriParser.IsWireGuardConf(rawInput))
        {
            try
            {
                var entry = ServerUriParser.Parse(rawInput);
                if (!Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server && s.Port == entry.Port))
                {
                    Servers.Add(new ServerViewModel(entry));
                    addedAny = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse WireGuard / AmneziaWG config: {Error}", ex.Message);
            }
        }
        else
        {
            var lines = rawInput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!ServerUriParser.IsSupportedScheme(line))
                    continue;

                try
                {
                    var entry = ServerUriParser.Parse(line);
                    if (Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server && s.Port == entry.Port))
                        continue;
                    Servers.Add(new ServerViewModel(entry));
                    addedAny = true;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to parse server URI: {Line}", CrashReporter.ScrubSecrets(line));
                }
            }
        }

        if (addedAny)
        {
            if (SelectedServer == null)
                SelectedServer = Servers.FirstOrDefault();
            SaveSettings();
        }

        VlessUri = string.Empty;
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer != null)
            RemoveServerByEntry(SelectedServer);
    }

    [RelayCommand]
    private void RemoveServerByEntry(ServerViewModel? entry)
    {
        if (entry == null) return;
        var wasSelected = ReferenceEquals(SelectedServer, entry);
        Servers.Remove(entry);
        if (wasSelected)
            SelectedServer = Servers.FirstOrDefault();

        SaveSettings();
        _logger?.Information(
            "[VM] RemoveServerByEntry: persisted deletion of '{Name}' ({Server}:{Port}) — {Remaining} servers remain",
            entry.Name, entry.Server, entry.Port, Servers.Count);

        MarkOrphanServers();
        RefreshActiveIndicator();
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

            if (CustomConfigs.Any(c => c.Name.Equals(configName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = Strings.ConfigExists(configName);
                return;
            }

            var json = await File.ReadAllTextAsync(sourcePath);
            var (isValid, errors) = CustomConfigInjector.Validate(json);
            if (!isValid)
            {
                StatusText = $"{Strings.InvalidConfig} {string.Join("; ", errors)}";
                return;
            }

            var destPath = CustomConfigInjector.CopyToProgramData(sourcePath, configName);
            var entry = new CustomConfigEntry { Name = configName, Path = destPath };

            var isFirst = CustomConfigs.Count == 0;
            var vm = new CustomConfigViewModel(entry, isFirst);
            CustomConfigs.Add(vm);

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

    private enum ReconnectIntent
    {
        Follow,
        ManualVless,
        Subscription,
        CustomConfig
    }

    partial void OnSelectedSubscriptionServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;
        if (value.IsActive) return;
        _logger?.Information(
            "[VM] OnSelectedSubscriptionServerChanged name={N} ip={Ip} IsConnected={C} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsSubscribeMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.Subscription);
        }
    }

    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;

        _logger?.Information(
            "[VM] OnSelectedServerChanged name={N} ip={Ip} IsConnected={C} IsVlessMode={V} IsSubscribeMode={S} IsConnecting={IC}",
            value.DisplayName, value.Server, IsConnected, IsVlessMode, IsSubscribeMode, IsConnecting);
        if (IsConnected && IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName, ReconnectIntent.ManualVless);
        }
    }

    partial void OnSelectedCustomConfigChanged(CustomConfigViewModel? value)
    {
        if (_isLoadingUI || value == null) return;
        if (value.IsActive) return;
        if (_isReconnecting) return;

        SetActiveCustomConfig(value);

        if (IsConnected && !IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.Name); return; }
            _ = ReconnectAsync(value.Name, ReconnectIntent.CustomConfig);
        }
    }

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

        _logger?.Information(
            "[VM] ReconnectAsync target={Target} intent={Intent} ConfigMode={CM} IsVlessMode={V} IsSubscribeMode={S}",
            configName, intent,
            _settings.App.ConfigMode, IsVlessMode, IsSubscribeMode);

        try
        {
            var applyInPlace = _engine.IsRunning;
            if (!applyInPlace)
            {
                await Task.Run(() => _engine.Stop());
            }

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

            SaveSettings();
            _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

            _logger?.Information(
                "[VM] ReconnectAsync after Save+Reload: ConfigMode={CM} VlessActive={VA} SubActive={SA} VlessServers={N}",
                _settings.App.ConfigMode,
                _settings.Vless.ActiveServer,
                _settings.App.ActiveSubscriptionServer,
                _settings.Vless.Servers?.Count ?? 0);

            var aggregated = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();

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
                _logger?.Information(
                    "[VM] ReconnectAsync.Subscription: aggregated {N} servers, ActiveServer={A}, ConfigMode preserved=subscribe",
                    aggregated.Count, _settings.Vless.ActiveServer);
            }

            if (applyInPlace)
            {
                _logger?.Information("[VM] ReconnectAsync applying new config via ApplyAsync(forceRestart=true)");
                var applied = await Task.Run(() => _engine.ApplyAsync(
                    _settings,
                    CancellationToken.None,
                    forceRestart: true));
                if (applied)
                {
                    RestoreConnectedStatus();
                    try { RefreshActiveIndicator(); }
                    catch (Exception ex) { _logger?.Debug(ex, "[VM] Reconnect: RefreshActiveIndicator failed"); }
                    return;
                }

                _logger?.Warning("[VM] ReconnectAsync ApplyAsync returned false; falling back to Stop+Start");
                await Task.Run(() => _engine.Stop());
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds +
                        Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds));
                    var startTask = Task.Run(
                        () => _engine.StartAsync(_settings, cts.Token, _skipVpnConflictThisSession),
                        cts.Token);

                    var outcome = await Internals.TwoPhaseStartCoordinator.RunAsync(
                        startTask: startTask,
                        subscribeStarted: handler =>
                        {
                            void Wrapper(int pid) => handler(pid);
                            _engine.SingBoxStarted += Wrapper;
                            return () => _engine.SingBoxStarted -= Wrapper;
                        },
                        subscribeConnected: handler =>
                        {
                            void Wrapper(int pid) => handler(pid);
                            _engine.Connected += Wrapper;
                            return () => _engine.Connected -= Wrapper;
                        },
                        cancellationToken: cts.Token);

                    if (outcome == Internals.TwoPhaseStartOutcome.PhaseATimeout)
                    {
                        _logger.Error("[VM] Reconnect: Phase A (sing-box launch) timed out after {N}s",
                            (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
                        try { await Task.Run(() => _engine.Stop()); } catch { }
                        IsConnected = false;
                        StatusText = Strings.StartTimeoutPhaseA;
                        ConnectButtonText = Strings.StartVPN;
                        return;
                    }
                    if (outcome == Internals.TwoPhaseStartOutcome.PhaseBTimeout)
                    {
                        _logger.Error("[VM] Reconnect: Phase B (TUN warm-up) timed out after {N}s",
                            (int)Internals.TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
                        try { await Task.Run(() => _engine.Stop()); } catch { }
                        IsConnected = false;
                        StatusText = Strings.StartTimeoutPhaseB;
                        ConnectButtonText = Strings.StartVPN;
                        return;
                    }
                    await startTask;
                    break;
                }
                catch (TunOwnershipException) when (attempt < maxRetries)
                {
                    _logger.Warning("[VM] Reconnect: TUN lock stolen by service, retry {A}/{M}", attempt, maxRetries);
                    await Task.Delay(ServiceReleaseRetryDelayMs);
                }
            }

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

    [RelayCommand]
    private void Quit()
    {
        if (_engine.IsRunning)
            _engine.Stop();

        StopSubRefreshTimer();

        KillAllZapret();

#if PLATFORM_WINDOWS
        try { _tgProxy?.Stop(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Quit: _tgProxy.Stop failed"); }
#endif

        SaveSettings();

        Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

#if PLATFORM_WINDOWS
        try { _tgProxyLifetimeCts.Cancel(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: TgProxy cancellation failed"); }
#endif

        try { UnwireOsThemeFollow(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: UnwireOsThemeFollow failed"); }

        try
        {
            _runtimeStatusTimer?.Stop();
            _runtimeStatusTimer = null;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _runtimeStatusTimer stop failed"); }

        try { StopSubRefreshTimer(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: StopSubRefreshTimer failed"); }

        try
        {
            _engine.StatusChanged -= OnEngineStatus;
            _engine.Connected -= OnEngineConnected;
            _engine.AutoFailoverTriggered -= OnAutoFailoverMessage;
            _engine.TrueSplitEngagedChanged -= OnTrueSplitEngagedChanged;
            _engine.TrueSplitStateChanged -= OnTrueSplitStateChanged;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: engine StatusChanged unhook failed"); }

        try
        {
            FreeConfigsVm?.Dispose();
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: FreeConfigsVm.Dispose failed"); }

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

        try
        {
            _statsApi?.Dispose();
            _statsApi = null;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _statsApi cleanup failed"); }

#if PLATFORM_WINDOWS
        try
        {
            TgProxyManager? manager;
            lock (_tgProxyStateGate)
            {
                manager = _tgProxy;
                _tgProxy = null;
            }
            if (manager != null)
            {
                manager.StatsUpdated -= OnTgProxyStats;
                manager.Dispose();
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _tgProxy cleanup failed"); }

        try
        {
            if (_zapret != null)
            {
                _zapret.ImmediateExitDetected -= OnZapretImmediateExit;
                _zapret.Dispose();
                _zapret = null;
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _zapret cleanup failed"); }
#endif

        try { StopZapretProbeElapsedTimer(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: StopZapretProbeElapsedTimer failed"); }

        try
        {
            _zapretAvBlockToastCts?.Cancel();
            _zapretAvBlockToastCts?.Dispose();
            _zapretAvBlockToastCts = null;
            _rulesToastCts?.Cancel();
            _rulesToastCts?.Dispose();
            _rulesToastCts = null;
            _tgProxyToastToken++;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: toast CTS cleanup failed"); }
    }

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            ThemeVariant effective;
            if (IsSystemThemePref)
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Default;
                effective = ReadOsThemeVariant();
            }
            else
            {
                effective = IsDarkThemePref ? ThemeVariant.Dark : ThemeVariant.Light;
                Application.Current.RequestedThemeVariant = effective;
            }
            IsDarkTheme = effective == ThemeVariant.Dark;
        }

        OnPropertyChanged(nameof(VpnBadgeBrush));
        OnPropertyChanged(nameof(ZapretBadgeBrush));
        OnPropertyChanged(nameof(TgProxyBadgeBrush));

        foreach (var s in Servers)             s.NotifyThemeChanged();
        foreach (var s in SubscriptionServers) s.NotifyThemeChanged();
    }

    private static ThemeVariant ReadOsThemeVariant()
    {
        try
        {
            var os = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            return os == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch
        {
            return ThemeVariant.Light;
        }
    }

    internal static string NormalizeThemePref(string? raw)
    {
        if (string.Equals(raw, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
        if (string.Equals(raw, "light", StringComparison.OrdinalIgnoreCase)) return "light";
        return "system";
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!IsSystemThemePref) return;
        Dispatcher.UIThread.Post(ApplyTheme);
    }

    private IPlatformSettings? _wiredPlatformSettings;

    private void WireOsThemeFollow()
    {
        if (_wiredPlatformSettings != null) return;
        try
        {
            var ps = Application.Current?.PlatformSettings;
            if (ps == null) return;
            ps.ColorValuesChanged += OnPlatformColorValuesChanged;
            _wiredPlatformSettings = ps;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] WireOsThemeFollow: could not subscribe to ColorValuesChanged");
        }
    }

    private void UnwireOsThemeFollow()
    {
        try
        {
            if (_wiredPlatformSettings != null)
            {
                _wiredPlatformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
                _wiredPlatformSettings = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] UnwireOsThemeFollow: unsubscribe failed");
        }
    }

    private void RefreshLocalization()
    {
        ThemeToggleText = IsDarkTheme ? Strings.ThemeLight : Strings.ThemeDark;
        ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
        if (!IsConnected && !IsConnecting)
            StatusText = Strings.NotConnected;

        OnPropertyChanged(string.Empty);

        foreach (var group in AppGroups.Concat(BypassAppGroups))
            group.NotifyDisplayNameChanged();
        foreach (var server in Servers)
            server.NotifyLocalizationChanged();
        foreach (var server in SubscriptionServers)
            server.NotifyLocalizationChanged();

        UpdateVm?.NotifyLangChanged();
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

}
