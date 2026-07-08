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

/// <summary>
/// Phase 2B (Wave 8, 2026-05-18) — Settings/About surface split out of the
/// <c>MainWindowViewModel</c> god-class. Hosts the non-VPN-runtime surface
/// that backs the Network → settings sub-tabs, the ⋯ menu's About / Theme /
/// Language segments, and the troubleshooting actions:
///
/// <list type="bullet">
///   <item><see cref="VersionText"/> / <see cref="AppVersionShortText"/> /
///   <see cref="GetSingBoxVersion"/> — strings the About dialog and ⋯ menu
///   pill bind to.</item>
///   <item><see cref="OpenLeakTest"/>, <see cref="RunHealthCheck"/>,
///   <see cref="OpenAbout"/>, <see cref="RestartInSafeMode"/>,
///   <see cref="ResetConfig"/>, <see cref="OpenLogs"/> — troubleshooting
///   commands bound to the ⋯ flyout / Network → Updates section.</item>
///   <item><see cref="ToggleTheme"/>, <see cref="ToggleLanguage"/> +
///   their explicit-segment wrappers (<c>SetThemeLight/Dark</c>,
///   <c>SetLanguageRussian/English</c>) — the per-segment commands the
///   redesigned ⋯ menu popover binds to.</item>
///   <item><see cref="ToggleUiMode"/>, <see cref="OpenAutostartSettings"/>,
///   <see cref="InstallServiceForAutostart"/>, <see cref="ApplySettings"/>,
///   <see cref="ShowWindow"/> — Simple/Advanced toggle + Autostart-jump
///   helpers + Apply/ShowWindow used by the system-tray menu.</item>
/// </list>
///
/// <para>Theme rendering (<c>ApplyTheme</c>) and locale broadcast
/// (<c>RefreshLocalization</c>) stay in the main file because they are
/// also called from the constructor + <see cref="LoadSettingsIntoUI"/>
/// boot paths. <see cref="MainWindowViewModel.SaveSettings"/> stays in
/// the main file too — it is THE cross-concern serialisation hub.</para>
/// </summary>
public partial class MainWindowViewModel
{
    // v2.37.0-r8 — magic-number extraction. ResetConfig two-step UX:
    // first click "arms" the button; second click within this window
    // performs the reset. Auto-disarm prevents a stale armed state
    // from ambushing a later click meant for another action.
    private const int ResetConfigArmedTimeoutMs = 5000;

    // ── Version ──
    public string VersionText => $"by NiniTux  ·  v{AppVersion.Version}  ·  sing-box {GetSingBoxVersion()}";

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

    // ── Troubleshooting / About / Reset / Logs ──

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

    [RelayCommand]
    private async Task AutoTuneMtu()
    {
        if (!OperatingSystem.IsWindows())
        {
            MtuAutoTuneStatus = Strings.MtuAutoTuneNoResult;
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

            TunMtu = Math.Clamp(payload, 576, TunSettings.DefaultMtu);
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
            // v2.37.0-r8 — extracted to named constant for the lower-bound
            // rationale (5s is "long enough for the user to find their
            // mouse target, short enough that a wandered click doesn't
            // fire something unexpected").
            _ = Task.Delay(ResetConfigArmedTimeoutMs, token).ContinueWith(t =>
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

    // ── Diagnostics export (v2.39.0) ──
    // Collects a redacted bundle (config + sing-box config + bounded log tails
    // + env/health summary + geo manifest) into one ZIP on the Desktop, so a
    // support request is a one-click attachment. Variant 0: nothing is
    // uploaded; all secrets are stripped by DiagnosticsRedactor before zipping.

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

    /// <summary>Open the OS file manager with the given file selected/revealed.</summary>
    private static void RevealInFileManager(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start(new ProcessStartInfo
                    {
                        FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "/usr/bin/xdg-open",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = false,
                    });
            }
        }
        catch { /* best-effort reveal */ }
    }

    // ── Theme / Language / UI mode / Settings commands ──

    [RelayCommand]
    private void ToggleTheme()
    {
        // v2.17.10: log entry so bug reports about the window teleporting
        // can be traced to the exact toggle that fired.
        // v2.40.x (Fix #7): flipping makes an EXPLICIT light/dark choice
        // (leaving "system"), routed through SetThemePreference so the pref
        // persists. Picks the opposite of whatever variant is showing.
        SetThemePreference(IsDarkTheme ? "light" : "dark");
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
    // (Phase 1). The popover shows Theme as a System|Light|Dark segmented
    // control (System added v2.40.x / Fix #7) and Language as RU|EN — clicking
    // an already-active segment is a no-op. These wrappers let the XAML bind
    // each segment button to its own command.
    [RelayCommand]
    private void SetThemeLight() => SetThemePreference("light");

    [RelayCommand]
    private void SetThemeDark() => SetThemePreference("dark");

    [RelayCommand]
    private void SetThemeSystem() => SetThemePreference("system");

    /// <summary>
    /// v2.40.x (Fix #7): apply + persist a theme preference. No-op if already
    /// active. ApplyTheme resolves "system" against the OS appearance and sets
    /// IsDarkTheme; SaveSettings persists the preference string (mirrors
    /// ToggleLanguage, which also saves on change).
    /// </summary>
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
