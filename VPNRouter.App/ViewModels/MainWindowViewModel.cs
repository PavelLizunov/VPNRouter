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
        _logger.Information(
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
                _logger.Information(
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBadComboWarningVisible))]
    private bool _bypassRussianTraffic = true;

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

    public bool CanToggleConnection => !IsConnecting && !IsApplying;

    public string LblLegendWorking  => IsRussian ? "работает"     : "working";
    public string LblLegendPartial  => IsRussian ? "частично"     : "partial";
    public string LblLegendFailed   => IsRussian ? "не работает"  : "failed";
    public string LblLegendUntested => IsRussian ? "не проверена" : "untested";
    public string LblLegendStale    => IsRussian ? "устарело"     : "stale";

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
        _logger.Information(
            "[VM] OnSelectedServerModeIndexChanged value={V} (was IsVlessMode={IV}, IsSubscribeMode={IS})",
            value, IsVlessMode, IsSubscribeMode);
        IsVlessMode = value == 0;
        SaveSettings();
    }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private AppGroupViewModel? _selectedAppGroup;

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
