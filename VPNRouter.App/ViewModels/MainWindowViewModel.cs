using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly VpnEngine _engine;
    private readonly ILogger _logger;
    private AppSettings _settings;
    private bool _isLoadingUI;
    private bool _appsLoaded;

    // ── Observable state ──

    [ObservableProperty] private string _statusText = Strings.NotConnected;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectButtonText = Strings.StartVPN;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogoSource))]
    private bool _isDarkTheme;

    private static readonly Bitmap _logo = LoadAsset("avares://VPNRouter.App/Assets/penguin_logo.png");
    public Bitmap LogoSource => _logo;
    private static Bitmap LoadAsset(string uri) => new(AssetLoader.Open(new System.Uri(uri)));
    [ObservableProperty] private string _themeToggleText = Strings.ThemeDark;
    [ObservableProperty] private bool _isRussian;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    private bool _isVlessMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
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
        // Tab 2 (Network), Tab 3 (Applications) — don't change mode
    }
    [ObservableProperty] private string _subscriptionUrl = string.Empty;
    [ObservableProperty] private bool _isSplitTunnel = true;
    [ObservableProperty] private bool _bypassRussianTraffic = true;
    [ObservableProperty] private bool _strictMode = false;
    [ObservableProperty] private bool _forceIpv4Only = true;
    [ObservableProperty] private bool _flushDnsOnStart = true;
    [ObservableProperty] private bool _strictDns = false;
    [ObservableProperty] private bool _receivePrereleases = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServersTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsSubscribeTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsNetworkTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsAppsTabSelected))]
    private int _selectedTabIndex;

    public bool IsServersTabSelected => SelectedTabIndex == 0;
    public bool IsSubscribeTabSelected => SelectedTabIndex == 1;
    public bool IsNetworkTabSelected => SelectedTabIndex == 2;
    public bool IsAppsTabSelected => SelectedTabIndex == 3;

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

    private static string GetSingBoxVersion()
    {
        try
        {
            var exePath = OperatingSystem.IsWindows()
                ? Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\bin\sing-box.exe")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library/Application Support/VPNRouter/bin/sing-box");
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
    public string LblTabManual => Strings.ModeManual;
    public string LblTabSubscribe => Strings.ModeSubscribe;
    public string LblTabApps => Strings.TabApps;
    public string LblTabNetwork => Strings.TabNetwork;
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
    public string ReceivePrereleasesLabel => IsRussian ? "Получать prerelease обновления (experimental канал)" : "Receive prereleases (experimental channel)";
    public string UpdateChannelHeader => IsRussian ? "Канал обновлений" : "Update channel";

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

        LoadSettingsIntoUI();

        // Background update check (fire-and-forget, silent fail)
        _ = UpdateVm.CheckOnStartupAsync();
    }

    // ── Settings Load/Save ──

    private void LoadSettingsIntoUI()
    {
        _isLoadingUI = true;
        try
        {
        // Language
        IsRussian = (_settings.App.Language ?? "en").Equals("ru", StringComparison.OrdinalIgnoreCase);
        Strings.Lang = IsRussian ? "ru" : "en";

        // Theme
        IsDarkTheme = (_settings.App.Theme ?? "light").Equals("dark", StringComparison.OrdinalIgnoreCase);
        ApplyTheme();

        // Config mode (three-way: generated / custom / subscribe)
        // Mode is determined by which tab is active. On load, select the
        // correct tab based on saved config_mode.
        var configMode = _settings.App.ConfigMode ?? "generated";
        IsSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);
        IsVlessMode = !configMode.Equals("custom", StringComparison.OrdinalIgnoreCase) && !IsSubscribeMode;
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

        // Load subscription servers (cached from last sync)
        SubscriptionServers.Clear();
        ServerViewModel? activeSubServer = null;
        foreach (var entry in _settings.App.SubscriptionServers ?? new())
        {
            var vm = new ServerViewModel(entry);
            SubscriptionServers.Add(vm);
            if (!string.IsNullOrEmpty(_settings.App.ActiveSubscriptionServer) &&
                entry.Name?.Equals(_settings.App.ActiveSubscriptionServer, StringComparison.OrdinalIgnoreCase) == true)
                activeSubServer = vm;
        }
        SelectedSubscriptionServer = activeSubServer ?? SubscriptionServers.FirstOrDefault();

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

        // Load from profiles (macOS uses default-macos.json)
        var profileFile = OperatingSystem.IsMacOS() ? "default-macos.json" : "default.json";
        var profilePath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        // Fallback to default.json if macOS variant doesn't exist
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

        _appsLoaded = true;
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
    /// Strip .exe suffix — only on macOS (sing-box uses bare names there).
    /// On Windows .exe MUST be preserved or sing-box won't match the process.
    /// </summary>
    private static string StripExe(string name)
    {
        name = name.Trim();
        if (OperatingSystem.IsMacOS())
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
        _settings.App.SubscriptionUrl = SubscriptionUrl;

        // Subscription servers (cached)
        _settings.App.SubscriptionServers = SubscriptionServers.Select(s => s.ToEntry()).ToList();
        var activeSub = SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault();
        _settings.App.ActiveSubscriptionServer = activeSub?.Name ?? "";

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

        // Update channel
        _settings.Update.Channel = ReceivePrereleases ? "experimental" : "stable";

        // Theme & language
        _settings.App.Theme = IsDarkTheme ? "dark" : "light";
        _settings.App.Language = IsRussian ? "ru" : "en";

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

            // Subscribe mode: feed subscription servers into VLESS engine path
            if (IsSubscribeMode && _settings.App.SubscriptionServers?.Count > 0)
            {
                _settings.Vless.Servers = _settings.App.SubscriptionServers;
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
            _ = ReconnectAsync(value.Name);
        }
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

            // Subscribe mode: feed subscription servers into engine
            if (IsSubscribeMode && _settings.App.SubscriptionServers?.Count > 0)
            {
                _settings.Vless.Servers = _settings.App.SubscriptionServers;
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

    [RelayCommand]
    private void AddCustomApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var name = StripExe(processName.Trim());

        // Find or create Custom Apps group
        var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (customGroup == null)
        {
            customGroup = new AppGroupViewModel("Custom Apps", "Your custom applications", true);
            AppGroups.Add(customGroup);
        }

        if (customGroup.Apps.Any(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        customGroup.Apps.Add(new AppItemViewModel(name, true, isCustom: true));
    }

    [RelayCommand]
    private void RemoveCustomApps()
    {
        var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (customGroup == null) return;

        var toRemove = customGroup.Apps.Where(a => a.IsChecked).ToList();
        foreach (var app in toRemove)
            customGroup.Apps.Remove(app);
    }

    [RelayCommand]
    private void RemoveCustomApp(AppItemViewModel? app)
    {
        if (app == null) return;
        var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        customGroup?.Apps.Remove(app);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ApplyTheme();
        RefreshLocalization();
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        IsRussian = !IsRussian;
        Strings.Lang = IsRussian ? "ru" : "en";
        RefreshLocalization();
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
    }

    // ── Helpers ──

    /// <summary>
    /// First-run setup: deploy bundled profiles and sing-box binary.
    /// </summary>
    private void DeployBundledProfiles()
    {
        // Deploy profiles
        string[] profileFiles = OperatingSystem.IsMacOS()
            ? new[] { "default-macos.json", "default.json" }
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

        // Deploy sing-box binary (bundled in .app)
        if (OperatingSystem.IsMacOS())
        {
            var destSingBox = AppPaths.SingBoxExePath;
            var bundledSingBox = Path.Combine(AppContext.BaseDirectory, "sing-box");
            if (File.Exists(bundledSingBox) && !File.Exists(destSingBox))
            {
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
}
