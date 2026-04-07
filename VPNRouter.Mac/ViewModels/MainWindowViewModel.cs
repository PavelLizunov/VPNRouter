using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
using VPNRouter.Mac.Localization;

namespace VPNRouter.Mac.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly VpnEngine _engine;
    private readonly ILogger _logger;
    private AppSettings _settings;

    // ── Observable state ──

    [ObservableProperty] private string _statusText = Strings.NotConnected;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectButtonText = Strings.StartVPN;
    [ObservableProperty] private bool _isDarkTheme;
    [ObservableProperty] private string _themeToggleText = Strings.ThemeDark;
    [ObservableProperty] private bool _isRussian;
    [ObservableProperty] private bool _isVlessMode = true;
    [ObservableProperty] private bool _isSplitTunnel = true;
    [ObservableProperty] private bool _bypassRussianTraffic = true;
    [ObservableProperty] private bool _strictMode = false;
    [ObservableProperty] private bool _forceIpv4Only = true;
    [ObservableProperty] private bool _flushDnsOnStart = true;
    [ObservableProperty] private bool _strictDns = false;

    // ── Version ──
    public string VersionText => $"by NiniTux  \u00b7  v{AppVersion.Version}";

    // ── Localized labels (proxies to Strings.cs, refreshed on language toggle) ──
    public string LblTabServers => Strings.TabServers;
    public string LblTabApps => Strings.TabApps;
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
    public string LblAddCustomAppHint => Strings.AddCustomAppHint;
    public string BypassRuLabel => Strings.BypassRussianTrafficLabel;
    public string BypassRuHint => Strings.BypassRussianTrafficHint;
    public string CheckLeaksLabel => Strings.CheckLeaks;
    public string ShowLogsLabel => Strings.ShowLogs;
    public string StrictModeLabel => Strings.StrictModeLabel;
    public string StrictModeHint => Strings.StrictModeHint;
    public string ForceIpv4Label => Strings.ForceIpv4Label;
    public string FlushDnsLabel => Strings.FlushDnsLabel;
    public string StrictDnsLabel => Strings.StrictDnsLabel;

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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                Arguments = $"\"{logsDir}\"",
                UseShellExecute = false
            });
        }
        catch { /* best-effort */ }
    }

    // ── VLESS fields (for single-server quick edit) ──
    [ObservableProperty] private string _vlessUri = string.Empty;

    // ── Collections ──
    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<CustomConfigViewModel> CustomConfigs { get; } = new();
    public ObservableCollection<AppGroupViewModel> AppGroups { get; } = new();

    // ── Selected items ──
    [ObservableProperty] private ServerViewModel? _selectedServer;
    [ObservableProperty] private CustomConfigViewModel? _selectedCustomConfig;

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
        LoadSettingsIntoUI();
    }

    // ── Settings Load/Save ──

    private void LoadSettingsIntoUI()
    {
        // Language
        IsRussian = (_settings.App.Language ?? "en").Equals("ru", StringComparison.OrdinalIgnoreCase);
        Strings.Lang = IsRussian ? "ru" : "en";

        // Theme
        IsDarkTheme = (_settings.App.Theme ?? "light").Equals("dark", StringComparison.OrdinalIgnoreCase);
        ApplyTheme();

        // Config mode
        IsVlessMode = !(_settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

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

        // Load servers
        Servers.Clear();
        foreach (var entry in _settings.Vless.GetEffectiveServers())
            Servers.Add(new ServerViewModel(entry));

        // Load custom configs
        CustomConfigs.Clear();
        foreach (var entry in _settings.App.CustomConfigs ?? new())
        {
            var isActive = entry.Name == _settings.App.ActiveCustomConfig;
            CustomConfigs.Add(new CustomConfigViewModel(entry, isActive));
        }

        // Load apps from profiles + custom apps
        LoadApps();

        RefreshLocalization();
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

        // Custom apps group
        var customApps = _settings.CustomApps ?? new();
        if (customApps.Count > 0)
        {
            var customGroup = new AppGroupViewModel("Custom Apps", "Your custom applications", true);
            foreach (var app in customApps)
            {
                if (!string.IsNullOrEmpty(app))
                    customGroup.Apps.Add(new AppItemViewModel(StripExe(app), true, isCustom: true));
            }
            AppGroups.Add(customGroup);
        }
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

    private static string StripExe(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    private void SaveSettings()
    {
        // Config mode
        _settings.App.ConfigMode = IsVlessMode ? "generated" : "custom";

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

        // Theme & language
        _settings.App.Theme = IsDarkTheme ? "dark" : "light";
        _settings.App.Language = IsRussian ? "ru" : "en";

        // Servers
        _settings.Vless.Servers = Servers.Select(s => s.ToEntry()).ToList();
        if (_settings.Vless.Servers.Count > 0)
        {
            var first = _settings.Vless.Servers[0];
            _settings.Vless.Server = first.Server;
            _settings.Vless.Port = first.Port;
            _settings.Vless.Uuid = first.Uuid;
            _settings.Vless.Flow = first.Flow;
            _settings.Vless.Security = first.Security;
            _settings.Vless.Reality = first.Reality;
        }

        // Custom configs
        _settings.App.CustomConfigs = CustomConfigs.Select(c => c.ToEntry()).ToList();
        var active = CustomConfigs.FirstOrDefault(c => c.IsActive);
        _settings.App.ActiveCustomConfig = active?.Name ?? "";

        // Active profiles (checked groups)
        var activeProfileNames = AppGroups
            .Where(g => g.IsChecked && g.Name != "Custom Apps")
            .Select(g => g.Name);
        _settings.ActiveProfile = string.Join(",", activeProfileNames);

        // Custom apps
        var customGroup = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        _settings.CustomApps = customGroup?.Apps
            .Where(a => a.IsChecked)
            .Select(a => a.ProcessName)
            .ToList() ?? new();

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
                StatusText = Strings.Connected(
                    _engine.ActiveProfileName,
                    _engine.SingBoxPid);
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
        if (IsConnected)
        {
            IsConnecting = true;
            StatusText = Strings.Stopping;
            await Task.Run(() => _engine.Stop());
        }
        else
        {
            IsConnecting = true;
            StatusText = Strings.Starting;
            ConnectButtonText = Strings.Starting;

            SaveSettings();
            _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

            // macOS: ensure sudo access (one-time password prompt)
            if (OperatingSystem.IsMacOS())
                await Task.Run(EnsureMacSudoAccess);

            try
            {
                await Task.Run(() => _engine.StartAsync(_settings));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start VPN");
                IsConnecting = false;
                StatusText = $"{Strings.FailedStartVpn} {ex.Message}";
                ConnectButtonText = Strings.StartVPN;
            }
        }
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
                // Check duplicate
                if (Servers.Any(s => s.Server == entry.Server && s.Port == entry.Port))
                    continue;
                Servers.Add(new ServerViewModel(entry));
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
        var window = GetMainWindow();
        if (window == null) return;

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

        var vm = new CustomConfigViewModel(entry, CustomConfigs.Count == 0);
        CustomConfigs.Add(vm);
    }

    [RelayCommand]
    private void RemoveCustomConfig()
    {
        if (SelectedCustomConfig != null)
            CustomConfigs.Remove(SelectedCustomConfig);
    }

    [RelayCommand]
    private void SetActiveCustomConfig(CustomConfigViewModel? config)
    {
        if (config == null) return;
        foreach (var c in CustomConfigs)
            c.IsActive = false;
        config.IsActive = true;
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
