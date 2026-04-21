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
using VPNRouter.Core.Platform;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly VpnEngine _engine;
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

    // ComboBox mode selector: 0=Manual, 1=Subscribe, 2=Custom Config
    [ObservableProperty] private int _configModeIndex;
    public string[] ConfigModeItems => new[]
    {
        Strings.ModeManual,
        Strings.ModeSubscribe,
        Strings.ModeCustomConfig
    };

    // ConfigModeIndex is no longer used for mode switching.
    // Mode is determined solely by tab selection (OnSelectedTabIndexChanged).
    // ComboBox removed from UI in v2.5.0; this handler kept as no-op safety.
    partial void OnConfigModeIndexChanged(int value) { }

    // Sync mode flags when tab changes. Saves on tab switch so Connect
    // always uses the mode matching the visible tab.
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_isLoadingUI || _isReconnecting) return;
        if (value == 0) // Manual tab
        {
            IsVlessMode = true;
            IsSubscribeMode = false;
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
    private bool _isSplitTunnel = true;
    [ObservableProperty] private bool _bypassRussianTraffic = true;
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
    private bool _tgProxyEnabled = false;
    [ObservableProperty] private string _tgProxyStatus = "Stopped";
    [ObservableProperty] private int _tgProxyPort = 1443;
    [ObservableProperty] private string _tgProxySecret = "";
    [ObservableProperty] private string _tgProxyLink = "";
    [ObservableProperty] private string _tgProxyVersionText = "";
    [ObservableProperty] private bool _isTgProxyDownloading = false;
    [ObservableProperty] private string _tgProxyStats = "";

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
        // Sync IsVlessMode with sub-tab index (0=VLESS, 1=Custom)
        IsVlessMode = value == 0;
        SaveSettings();
    }

    partial void OnAutostartUiChanged(bool value)
    {
        if (_isLoadingUI) return;
#if PLATFORM_WINDOWS
        try
        {
            if (value)
                AutostartHelper.Enable(Environment.ProcessPath!);
            else
                AutostartHelper.Disable();
        }
        catch (Exception ex) { _logger.Error(ex, "[VM] Autostart UI toggle failed"); }
#endif
        SaveSettings();
    }

    partial void OnAutostartVpnChanged(bool value) { if (!_isLoadingUI) SaveSettings(); }
    partial void OnAutostartZapretChanged(bool value) { if (!_isLoadingUI) SaveSettings(); }
    partial void OnAutostartTgProxyChanged(bool value) { if (!_isLoadingUI) SaveSettings(); }

    // Zapret section navigator (master-detail)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretStatusSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretStrategySection))]
    [NotifyPropertyChangedFor(nameof(IsZapretHostsSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretFiltersSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretUpdatesSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretDiagnosticsSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretAdvancedSection))]
    private int _selectedZapretSectionIndex;

    public bool IsZapretStatusSection => SelectedZapretSectionIndex == 0;
    public bool IsZapretStrategySection => SelectedZapretSectionIndex == 1;
    public bool IsZapretHostsSection => SelectedZapretSectionIndex == 2;
    public bool IsZapretFiltersSection => SelectedZapretSectionIndex == 3;
    public bool IsZapretUpdatesSection => SelectedZapretSectionIndex == 4;
    public bool IsZapretDiagnosticsSection => SelectedZapretSectionIndex == 5;
    public bool IsZapretAdvancedSection => SelectedZapretSectionIndex == 6;

    // Free Configs section navigator (v2.14.8 master-detail restructure, matches NetworkPage/DpiBypassPage)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeOverviewSection))]
    [NotifyPropertyChangedFor(nameof(IsFreeScanSection))]
    [NotifyPropertyChangedFor(nameof(IsFreeDeepSection))]
    [NotifyPropertyChangedFor(nameof(IsFreeFiltersSection))]
    [NotifyPropertyChangedFor(nameof(IsFreeMySourcesSection))]
    [NotifyPropertyChangedFor(nameof(IsFreeCleanupSection))]
    private int _selectedFreeSectionIndex;

    public bool IsFreeOverviewSection   => SelectedFreeSectionIndex == 0;
    public bool IsFreeScanSection       => SelectedFreeSectionIndex == 1;
    public bool IsFreeDeepSection       => SelectedFreeSectionIndex == 2;
    public bool IsFreeFiltersSection    => SelectedFreeSectionIndex == 3;
    public bool IsFreeMySourcesSection  => SelectedFreeSectionIndex == 4;
    public bool IsFreeCleanupSection    => SelectedFreeSectionIndex == 5;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsRoutingSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLeakSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsContentSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsUpdatesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAutostartSelected))]
    private int _selectedSettingsIndex;

    public bool IsSettingsRoutingSelected => SelectedSettingsIndex == 0;
    public bool IsSettingsLeakSelected => SelectedSettingsIndex == 1;
    public bool IsSettingsContentSelected => SelectedSettingsIndex == 2;
    public bool IsSettingsUpdatesSelected => SelectedSettingsIndex == 3;
    public bool IsSettingsAutostartSelected => SelectedSettingsIndex == 4;

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
    public string LblSettingsLeak => Strings.SectionLeakProtection;
    public string LblSettingsContent => Strings.SectionContent;
    public string LblSettingsUpdates => Strings.SectionUpdates;
    public string LblAutostartSection => Strings.AutostartSection;
    public string LblAutostartVpn => Strings.AutostartVpn;
    public string LblAutostartZapret => Strings.AutostartZapret;
    public string LblAutostartTgProxy => Strings.AutostartTgProxy;
    public string LblAutostartUi => Strings.AutostartUi;
    public string LblServerModeVless => Strings.VlessServers;
    public string LblServerModeCustom => Strings.CustomConfigJson;
    public string LblToolZapret => Strings.TabZapret;
    public string LblToolTgProxy => Strings.TabTgWsProxy;
    public string LblDpiBypassTab => Strings.TabZapret;
    public string LblDpiDescription => IsRussian
        ? "Обход блокировок провайдера (zapret от Flowseal). Работает с Discord, YouTube, и другими заблокированными сервисами. Если стратегия не работает — пробуйте другую."
        : "Bypass ISP blocking (zapret by Flowseal). Works with Discord, YouTube, and other blocked services. If a strategy doesn't work — try another.";
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
    public string LblUpdateTgProxy => IsRussian
        ? (TgProxyUpdater.IsInstalled() ? "Обновить" : "Скачать")
        : (TgProxyUpdater.IsInstalled() ? "Update" : "Download");


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
            var dlg = new VPNRouter.App.Views.AboutWindow();

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

    [RelayCommand]
    private void ResetConfig()
    {
        // First click: arm the confirmation.
        if (!ResetConfigArmed)
        {
            ResetConfigArmed = true;
            // Auto-disarm after 5 seconds so a stale armed state can't
            // ambush a later click that was meant for something else.
            _ = Task.Delay(5000).ContinueWith(_ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ResetConfigArmed = false));
            return;
        }
        ResetConfigArmed = false;

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

        AppPaths.EnsureDirectories();
        DeployBundledProfiles();

        _engine = PlatformServices.CreateVpnEngine(_logger);
        _engine.StatusChanged += OnEngineStatus;

        _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

        // Sub-VMs
        UpdateVm = new UpdateNotificationViewModel(_settings.Update, _logger);
        ServiceVm = new ServiceViewModel(_logger);
        FreeConfigsVm = new FreeConfigsPageViewModel(_logger, ApplyFreeConfigAsync, () => _settings);

        LoadSettingsIntoUI();

        // Detect VPN already running (e.g. started by Windows Service on boot)
        DetectServiceManagedVpn();

        // Background update check (fire-and-forget, silent fail)
        _ = UpdateVm.CheckOnStartupAsync();

        // Status dashboard (v2.15.0): poll VPN/Zapret/TgProxy every 2s
        StartRuntimeStatusPolling();
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
        ServerViewModel? active = null;

        foreach (var s in Servers)
        {
            var isActive = IsConnected && !string.IsNullOrEmpty(activeIp) && s.Server == activeIp;
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        foreach (var s in SubscriptionServers)
        {
            var isActive = IsConnected && !string.IsNullOrEmpty(activeIp) && s.Server == activeIp;
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        ActiveServerChanged?.Invoke(active);
    }

    private void DetectServiceManagedVpn()
    {
        try
        {
            var singboxRunning = Process.GetProcessesByName("sing-box").Length > 0;
            if (!singboxRunning) return;

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            StartSubRefreshTimer();
            _logger.Information("[VM] Detected VPN running via service (sing-box alive)");
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

        // Simple-mode 'Start with Windows' checkbox — mirror of AutostartVpn.
        // Setter is a no-op during _isLoadingUI so this doesn't re-trigger
        // ServiceVm.Install.
        SmpAutostartChecked = _settings.App.AutostartVpn;

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
        SelectedServerModeIndex = IsVlessMode ? 0 : 1;
        SubscriptionUrl = _settings.App.SubscriptionUrl ?? "";
        // Set initial tab: 0=Manual, 1=Subscribe, 2=Network, 3=Applications
        SelectedTabIndex = IsSubscribeMode ? 1 : 0;

        // Routing mode
        IsSplitTunnel = !(_settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);

        // Russian geo bypass
        BypassRussianTraffic = _settings.App.BypassRussianTraffic;

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
    /// </summary>
    private void EnsureMacSudoAccess()
    {
        const string sudoersPath = "/etc/sudoers.d/vpnrouter";
        if (File.Exists(sudoersPath)) return;

        StatusText = IsRussian ? "Настройка sudo (один раз)..." : "Setting up sudo (one-time)...";

        // Write sudoers content to temp file (avoids all quoting problems)
        var user = Environment.UserName;
        var singbox = AppPaths.SingBoxExePath;
        var tmpFile = Path.Combine(Path.GetTempPath(), "vpnrouter-sudoers");
        File.WriteAllText(tmpFile,
            $"{user} ALL=(root) NOPASSWD: {singbox}\n" +
            $"{user} ALL=(root) NOPASSWD: /usr/bin/pkill -f sing-box\n");

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

        // Config mode (three-way)
        _settings.App.ConfigMode = IsSubscribeMode ? "subscribe" : IsVlessMode ? "generated" : "custom";

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

        // Russian geo bypass
        _settings.App.BypassRussianTraffic = BypassRussianTraffic;

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
                await Task.Run(() => _engine.Stop());
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

                try { OrphanCleanup.KillOrphans(); } catch { }

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
                _settings.App.ConfigMode = "generated";
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

        // Immediately refresh this new subscription
        await RefreshSubscriptionAsync(svm);
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
            RebuildSubscriptionPool();
            SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", sub.Url);
        }
        finally { sub.IsRefreshing = false; }
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
        finally { foreach (var s in enabled) s.IsRefreshing = false; }

        RebuildSubscriptionPool();
        SaveSettings();
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
        if (!IsSubscribeMode || string.IsNullOrWhiteSpace(SubscriptionUrl)) return;

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

            RebuildSubscriptionPool();
            SaveSettings();

            var afterUuids = SubscriptionServers.Select(s => s.Uuid).OrderBy(u => u).ToList();
            var changed = !beforeUuids.SequenceEqual(afterUuids);

            if (!changed)
            {
                _logger.Information("[SubRefresh] No UUID changes, no reconnect needed");
                return;
            }

            _logger.Information("[SubRefresh] Servers changed, reconnecting...");
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
        try { _zapret?.Stop(); } catch { }

        // Force kill by process name
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch { }
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
        catch { }
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
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret download failed");
            ZapretStatus = $"Download error: {ex.Message}";
        }
        finally
        {
            IsZapretDownloading = false;
        }
#endif
    }

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

        // Auto-download if not installed
        if (!TgProxyUpdater.IsInstalled())
        {
            await UpdateTgProxyAsync();
            if (!TgProxyUpdater.IsInstalled()) return;
        }

        try
        {
            // Generate secret if empty
            if (string.IsNullOrWhiteSpace(TgProxySecret))
            {
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
            }

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
        TgProxyStatus = Strings.TgProxyCopied;
    }

    [RelayCommand]
    private void OpenTgProxyInTelegram()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;
        TgProxyManager.OpenInTelegram("127.0.0.1", TgProxyPort, TgProxySecret);
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
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void CopyTgProxySecret()
    {
        if (string.IsNullOrEmpty(TgProxySecret)) return;
        CopyToClipboard(TgProxySecret);
        TgProxyStatus = Strings.TgProxyCopied;
    }

    [RelayCommand]
    private void RegenerateTgProxySecret()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
        TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        SaveSettings();
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
        catch { }
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
            if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var entry = VlessUriParser.Parse(line);
                // Check duplicate by name (same IP+port with different name/uuid is OK)
                if (Servers.Any(s => s.Name == entry.Name && s.Server == entry.Server))
                    continue;
                Servers.Add(new ServerViewModel(entry));
                SaveSettings();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to parse VLESS URI: {Line}", line);
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

    // Subscribe: selecting a subscription server = choosing which to route through.
    partial void OnSelectedSubscriptionServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;
        if (IsConnected && IsSubscribeMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName);
        }
    }

    // VLESS: selecting a server = choosing which server to route through.
    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        if (_isLoadingUI || value == null || _isReconnecting) return;

        // If connected in VLESS mode → reconnect with newly selected server
        if (IsConnected && IsVlessMode && !IsConnecting)
        {
            if (IsServiceManagedVpn) { WarnServiceManagedReconnect(value.DisplayName); return; }
            _ = ReconnectAsync(value.DisplayName);
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
            _ = ReconnectAsync(value.Name);
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

    private async Task ReconnectAsync(string configName)
    {
        if (_isReconnecting) return;
        _isReconnecting = true;
        IsConnecting = true;
        StatusText = IsRussian
            ? $"Переключение на {configName}..."
            : $"Switching to {configName}...";

        try
        {
            // Stop current VPN
            await Task.Run(() => _engine.Stop());

            // Save + reload settings with the new active config
            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            // Subscribe mode: aggregate enabled subscriptions → feed into engine
            var aggregated = _settings.App.Subscriptions
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers)
                .ToList();
            if (IsSubscribeMode && aggregated.Count > 0)
            {
                _settings.Vless.Servers = aggregated;
                _settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer;
                _settings.App.ConfigMode = "generated";
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
        // v2.17.10: log entry — language toggle is the one that rebuilds the
        // entire MainWindow (see ReloadMainWindowForLocalization below) so
        // we want this clearly traceable in app logs.
        _logger.Information("[VM] ToggleLanguage → {Lang}", IsRussian ? "en" : "ru");
        IsRussian = !IsRussian;
        Strings.Lang = IsRussian ? "ru" : "en";
        SaveSettings();          // persist language before we rebuild UI
        RefreshLocalization();   // updates {Binding Lbl*} across the UI
        ReloadMainWindowForLocalization();  // re-parses XAML so {x:Static} hits new Lang
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
    /// Workaround for Avalonia's <c>{x:Static loc:Strings.*}</c> bindings which
    /// are evaluated ONCE at XAML parse time and never re-read — so a language
    /// toggle doesn't update them (Free Configs page has ~100 such bindings).
    /// We rebuild the window with the same DataContext so XAML re-parses and
    /// picks up the new <see cref="Strings.Lang"/>, while all VM state
    /// (Servers list, connection, Free Configs cache, etc.) is preserved
    /// because the VM instance is shared.
    /// </summary>
    private void ReloadMainWindowForLocalization()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var oldWindow = desktop.MainWindow;
            if (oldWindow == null) return;

            // Remember geometry + state so the new window opens where the old one was
            var pos    = oldWindow.Position;
            var width  = oldWindow.Width;
            var height = oldWindow.Height;
            var state  = oldWindow.WindowState;

            var newWindow = new Views.MainWindow
            {
                DataContext   = this,
                Position      = pos,
                Width         = width,
                Height        = height,
                WindowState   = state,
                // v2.17.10 fix: MainWindow.axaml declares
                // WindowStartupLocation="CenterScreen" which, on the rebuilt
                // instance, would re-centre the window at Show() time and
                // discard the Position we just copied from the old window.
                // Set Manual to tell Avalonia "trust the Position property".
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            desktop.MainWindow = newWindow;
            newWindow.Show();
            oldWindow.Close();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] ReloadMainWindowForLocalization failed (non-fatal)");
        }
    }

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
        try { _tgProxy?.Stop(); TgProxyManager.KillAll(TgProxyPort); } catch { }
#endif

        SaveSettings();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
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
            SelectedServer = target;
            _settings.App.ConfigMode = "generated";
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

        var proceedBtn = new Button
        {
            Content = Strings.FcSecWarnProceed,
            Padding = new Thickness(12, 6),
            FontWeight = FontWeight.SemiBold,
            Background = Avalonia.Media.Brush.Parse("#059669"),
            Foreground = Avalonia.Media.Brushes.White,
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
                            Foreground = Avalonia.Media.Brush.Parse("#B45309"),
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
                            Background = Avalonia.Media.Brush.Parse("#FEF3C7"),
                            BorderBrush = Avalonia.Media.Brush.Parse("#F59E0B"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnDontUseList,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Avalonia.Media.Brush.Parse("#78350F"),
                            },
                        },
                        new Border
                        {
                            Padding = new Thickness(10, 8),
                            Background = Avalonia.Media.Brush.Parse("#DCFCE7"),
                            BorderBrush = Avalonia.Media.Brush.Parse("#059669"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Child = new TextBlock
                            {
                                Text = Strings.FcSecWarnGoodFor,
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Avalonia.Media.Brush.Parse("#14532D"),
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
