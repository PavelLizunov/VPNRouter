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
}
