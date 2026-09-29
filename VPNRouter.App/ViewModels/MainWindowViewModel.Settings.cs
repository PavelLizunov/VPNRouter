#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.Diagnostics;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Serilog;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private const int ResetConfigArmedTimeoutMs = 5000;

    public string VersionText => $"by NiniTux  ·  v{AppVersion.Version}  ·  sing-box {GetSingBoxVersion()}";

    public string AppVersionShortText => $"v{AppVersion.Version}";

    private static string GetSingBoxVersion()
    {
        try
        {
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

    [RelayCommand]
    private void OpenLeakTest()
    {
        OpenUrl("https://ipleak.net/");
    }

    [RelayCommand]
    private void OpenSetupWizard()
    {
        try
        {
            var viewModel = new SetupWizardViewModel(
                TunMtu,
                IsSplitTunnel,
                ApplySetupWizardSettings,
                VPNRouter.Core.Services.HealthCheck.RunAll,
                ExportDiagnosticsAsync);
            var dialog = new VPNRouter.App.Views.SetupWizardWindow(viewModel);

            var app = Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime;
            if (app?.MainWindow is { } owner)
                dialog.ShowDialog(owner);
            else
                dialog.Show();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ViewModel] Failed to open setup wizard");
        }
    }

    partial void OnTunMtuChanged(int value)
    {
        if (_isLoadingUI || value < TunSettings.MinimumMtu) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on TunMtu change failed"); }
        MarkRoutingSettingsChanged();
    }

    private void ApplySetupWizardSettings(int mtu, bool splitTunnel)
    {
        var previousMtu = TunMtu;
        var previousSplitTunnel = IsSplitTunnel;
        var previousRoutingMode = _settings.App.RoutingMode;
        var previousProfile = _settings.ActiveProfile;

        try
        {
            TunMtu = mtu;
            if (IsSplitTunnel != splitTunnel)
                IsSplitTunnel = splitTunnel;
            else
                SaveSettings();

            if (previousMtu != mtu)
                MarkRoutingSettingsChanged();
        }
        catch
        {
            _isLoadingUI = true;
            try
            {
                TunMtu = previousMtu;
                IsSplitTunnel = previousSplitTunnel;
                _settings.Tun.Mtu = previousMtu;
                _settings.App.RoutingMode = previousRoutingMode;
                _settings.ActiveProfile = previousProfile;
            }
            finally
            {
                _isLoadingUI = false;
            }
            throw;
        }
    }

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

            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    UseShellExecute = false
                };
            }
            else
            {
                var opener = OperatingSystem.IsMacOS()
                    ? "/usr/bin/open"
                    : "xdg-open";
                psi = new ProcessStartInfo
                {
                    FileName = opener,
                    UseShellExecute = false
                };
            }
            psi.ArgumentList.Add(reportPath);
            System.Diagnostics.Process.Start(psi);
            ShowRulesToast(VPNRouter.App.Localization.Strings.HealthCheckSavedToast);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[ViewModel] Health check failed");
        }
    }

    [RelayCommand]
    private async Task AutoTuneMtu()
    {
        if (!OperatingSystem.IsWindows())
        {
            MtuAutoTuneStatus = Strings.MtuAutoTuneWindowsOnly;
            return;
        }

        IsMtuAutoTuneRunning = true;
        MtuAutoTuneStatus = Strings.MtuAutoTuneRunning;
        try
        {
            var probe = await Task.Run(VPNRouter.Core.Services.HealthCheck.ProbePathMtuPayload);
            if (probe.PlainPingBlocked)
            {
                MtuAutoTuneStatus = Strings.MtuAutoTuneBlocked;
                return;
            }

            if (probe.BestPayload is not { } payload)
            {
                MtuAutoTuneStatus = Strings.MtuAutoTuneNoResult;
                return;
            }

            if (payload < 1332)
            {
                MtuAutoTuneStatus = Strings.MtuAutoTuneTooLow(payload);
                return;
            }

            TunMtu = Math.Clamp(payload, TunSettings.MinimumMtu, TunSettings.DefaultMtu);
            SaveSettings();
            MtuAutoTuneStatus = Strings.MtuAutoTuneApplied(TunMtu);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[ViewModel] MTU auto-tune failed");
            MtuAutoTuneStatus = Strings.MtuAutoTuneNoResult;
        }
        finally
        {
            IsMtuAutoTuneRunning = false;
        }
    }

    [RelayCommand]
    private void OpenAbout()
    {
        try
        {
            var dlg = new VPNRouter.App.Views.AboutWindow
            {
                DataContext = this
            };

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
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/setsid",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--fork");
                psi.ArgumentList.Add(exe);
                psi.ArgumentList.Add("--safe");
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--safe");
            }
            System.Diagnostics.Process.Start(psi);
            try { VPNRouter.Core.Services.LockFile.Release(); } catch { }
            Environment.Exit(0);
        }
        catch { }
    }

    private System.Threading.CancellationTokenSource? _resetDisarmCts;

    [RelayCommand]
    private void ResetConfig()
    {
        if (!ResetConfigArmed)
        {
            ResetConfigArmed = true;
            var oldCts = _resetDisarmCts;
            _resetDisarmCts = new System.Threading.CancellationTokenSource();
            var token = _resetDisarmCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                oldCts.Dispose();
            }
            _ = Task.Delay(ResetConfigArmedTimeoutMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ResetConfigArmed = false);
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }
        ResetConfigArmed = false;
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

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            ProcessStartInfo psi;
            if (OperatingSystem.IsLinux())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/setsid",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--fork");
                psi.ArgumentList.Add(exe);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            System.Diagnostics.Process.Start(psi);
            try { VPNRouter.Core.Services.LockFile.Release(); } catch { }
            Environment.Exit(0);
        }
        catch { }
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
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(logsDir);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(logsDir);
            }
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    [ObservableProperty]
    private bool _isExportingDiagnostics;

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (IsExportingDiagnostics) return;
        IsExportingDiagnostics = true;
        try
        {
            var connected = IsConnected;
            var now = DateTime.Now;
            var result = await Task.Run(() => DiagnosticsExporter.Export(now, connected));
            var name = Path.GetFileName(result.ZipPath);
            ShowRulesToast(IsRussian
                ? $"Диагностика сохранена на рабочий стол: {name}"
                : $"Diagnostics saved to Desktop: {name}");
            _logger?.Information("[VM] Diagnostics exported: {Path} ({Entries} entries, {Warnings} warnings)",
                result.ZipPath, result.Entries.Count, result.Warnings.Count);
            RevealInFileManager(result.ZipPath);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[VM] Diagnostics export failed");
            ShowRulesToast(IsRussian ? "Не удалось собрать диагностику" : "Diagnostics export failed");
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    private static void RevealInFileManager(string filePath)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetDirectoryName(filePath)))
                return;

            var psi = VPNRouter.App.Services.FileManagerHelper.BuildRevealStartInfo(filePath);
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        SetThemePreference(IsDarkTheme ? "light" : "dark");
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        _logger.Information("[VM] ToggleLanguage → {Lang}", IsRussian ? "en" : "ru");
        IsRussian = !IsRussian;
        Strings.Lang = IsRussian ? "ru" : "en";
        SaveSettings();
        RefreshLocalization();
        RefreshL10nProxies();
    }

    [RelayCommand]
    private void SetThemeLight() => SetThemePreference("light");

    [RelayCommand]
    private void SetThemeDark() => SetThemePreference("dark");

    [RelayCommand]
    private void SetThemeSystem() => SetThemePreference("system");

    private void SetThemePreference(string pref)
    {
        pref = NormalizeThemePref(pref);
        if (string.Equals(ThemePreference, pref, StringComparison.OrdinalIgnoreCase)) return;
        _logger.Information("[VM] SetTheme → {Pref}", pref);
        ThemePreference = pref;
        ApplyTheme();
        RefreshLocalization();
        SaveSettings();
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

    [RelayCommand]
    private void ToggleUiMode()
    {
        IsSimpleMode = !IsSimpleMode;
        _settings.App.UiMode = IsSimpleMode ? "simple" : "advanced";
        SaveSettings();
    }

    [RelayCommand]
    private void OpenAutostartSettings()
    {
        IsSimpleMode = false;
        _settings.App.UiMode = "advanced";
        SelectedTabIndex = 2;
        SelectedSettingsIndex = 5;
        SaveSettings();
    }

    [RelayCommand]
    private void InstallServiceForAutostart()
    {
        if (ServiceVm.IsInstalled || ServiceVm.IsBusy) return;
        ServiceVm.AutostartChecked = true;
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

    [ObservableProperty] private bool _hasPendingAppChanges;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyAppChanges))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    private bool _isApplying;
    private int _routingSettingsRevision;
    public bool CanApplyAppChanges => IsConnected && !IsConnecting && !IsApplying;

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

    partial void OnIsDnsLeakLockdownEnabledChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on IsDnsLeakLockdownEnabled change failed"); }
        MarkRoutingSettingsChanged();
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
}
