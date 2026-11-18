using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Services;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    public bool IsZapretAvailable => OperatingSystem.IsWindows();

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

    public string LblZapretRunSelected =>
        IsRussian ? "Запустить" : "Run";

    [RelayCommand]
    private void OpenProbeLog()
    {
        var path = LastProbeLogPath;
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return;
        try
        {
            var root = ZapretRuntimeDirectory.PrepareRoot();
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(path).StartsWith("zapret-probe-", StringComparison.Ordinal) ||
                !path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Core.Localization.Strings.ZapretRepairPackage);

            var psi = new ProcessStartInfo(ZapretRuntimeDirectory.SystemFile("notepad.exe"))
            {
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(path);
            ZapretRuntimeDirectory.SetProcessEnvironment(psi);
            using var viewer = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] OpenProbeLog failed for {Path}", path);
            ZapretStatus = ex.Message;
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
        var path = ZapretUpdater.PackageDisplayPath;
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
            _logger.Debug(ex, "[VM] Failed to copy Zapret whitelist path");
        }
    }

    [RelayCommand]
    private void DismissZapretAvBlockToast() => ZapretAvBlockToast = string.Empty;

    partial void OnAutostartZapretChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger.Warning(ex, "[VM] Auto-save on AutostartZapret change failed"); }
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

    public bool IsZapretToolSelected => SelectedToolIndex == 0;

    private bool StopZapret()
    {
#if PLATFORM_WINDOWS
        if (_zapret?.TryStop() == false)
        {
            ZapretStatus = Core.Localization.Strings.ZapretStopPending;
            return false;
        }
#endif
        return true;
    }

    private bool IsZapretRunning()
    {
#if PLATFORM_WINDOWS
        return _zapret?.HasOwnedProcess == true;
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
            _logger.Warning(
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
            _logger.Information(
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
            var logsDir = ZapretRuntimeDirectory.Root;
            if (!Directory.Exists(logsDir)) return;
            logsDir = ZapretRuntimeDirectory.PrepareRoot();
            var newest = Directory.GetFiles(logsDir, "zapret-probe-*.log")
                .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(newest))
            {
                LastProbeLogPath = newest;
                _logger.Debug("[VM] TryRestoreLastProbeLog: {Path}", newest);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] TryRestoreLastProbeLog failed (non-fatal)");
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
            _logger.Debug(ex, "[VM] RefreshZapretStrategiesDisplay: probe cache load failed");
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
        if (!IsZapretMagicButtonEnabled || _disposed) return;
        IsZapretDownloading = true;
        ZapretStatus = IsRussian ? "Загрузка zapret..." : "Downloading zapret...";

        try
        {
            if (ZapretEnabled || IsZapretRunning())
            {
                if (!StopZapret()) return;
                ZapretEnabled = false;
            }

            var updater = new VPNRouter.Core.Services.ZapretUpdater(_logger);
            updater.StatusChanged += s =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ZapretStatus = s);

            await updater.DownloadAndExtractAsync(System.Threading.CancellationToken.None);

            LoadZapretStrategies();

            var migration = await ZapretLegacyServices.RetryAsync(_logger);
            ZapretStatus = migration.Message ?? (IsRussian
                ? $"zapret {ZapretVersionText} установлен"
                : $"zapret {ZapretVersionText} installed");
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
        if (!IsZapretMagicButtonEnabled || _disposed) return;
        if (ZapretEnabled || IsZapretRunning())
        {
            if (!StopZapret()) return;
            ZapretEnabled = false;
            ZapretStatus = Strings.Stopped;
            SaveSettings();
            return;
        }

        if (!VPNRouter.Core.Services.ZapretUpdater.IsInstalled())
        {
            await UpdateZapretAsync();
            if (_disposed || !VPNRouter.Core.Services.ZapretUpdater.IsInstalled()) return;
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
            if (_zapret.Pid is int pid)
            {
                ZapretEnabled = true;
                ZapretStatus = IsRussian
                    ? $"Работает [{strategyName}] (PID {pid})"
                    : $"Running [{strategyName}] (PID {pid})";
            }
            else if (_zapret.HasOwnedProcess)
            {
                ZapretStatus = Core.Localization.Strings.ZapretStopPending;
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

    public string LblZapretMagicButton => ZapretEnabled
        ? Strings.ZapretOneTapStopButton
        : Strings.ZapretOneTapStartButton;

    public bool IsZapretMagicButtonEnabled => !IsZapretDownloading && !IsZapretProbing && !IsZapretActionRunning;

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

    [RelayCommand]
    private async Task ZapretOneClickAsync()
    {
#if PLATFORM_WINDOWS
        if (!IsZapretMagicButtonEnabled || _disposed) return;
        if (ReportOtherZapretInstance()) return;
        if (ZapretEnabled || IsZapretRunning())
        {
            if (!StopZapret()) return;
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

#if PLATFORM_WINDOWS
    private bool _forceFreshProbe;
#endif

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblZapretHeroLede))]
    [NotifyPropertyChangedFor(nameof(LblZapretProbeElapsed))]
    private int _zapretProbeElapsedSeconds;

    private DateTime _zapretProbeStartTime;
    private System.Threading.Timer? _zapretProbeElapsedTimer;

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

        if (!IsZapretMagicButtonEnabled || _disposed) return;
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
            if (_zapret.Pid is int pid)
            {
                ZapretEnabled = true;
                ZapretWinningStrategy = strategyName;
                ZapretProbePassCount = 0;
                ZapretProbeTotalCount = 0;
                IsZapretFallback = false;
                ZapretStatus = Strings.ZapretRunningSelected(strategyName, pid);
                VPNRouter.Core.Services.ZapretProbeCache.RecordSuccess(strategyName, _logger);
                RefreshZapretStrategiesDisplay();
                SaveSettings();
            }
            else if (_zapret.HasOwnedProcess)
            {
                ZapretStatus = Core.Localization.Strings.ZapretStopPending;
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

    private CancellationTokenSource? _zapretProbeCts = null;

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
        if (!IsZapretMagicButtonEnabled || _disposed) return;
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

    private bool ReportOtherZapretInstance()
    {
        if (IsZapretRunning() || !ZapretAutoStrategy.AnyWinwsRunning()) return false;
        ZapretEnabled = false;
        ZapretStatus = Core.Localization.Strings.ZapretOtherHelper;
        return true;
    }

    internal static Func<ZapretManager, IProgress<ZapretAutoStrategy.StrategyProgress>?, Serilog.ILogger?,
        CancellationToken, Task<ZapretAutoStrategy.StrategySweepResult>> InstalledProbe { get; set; } =
            ZapretAutoStrategy.RunInstalledProbeAsync;

    private async Task ProbeAndStartZapretAsync()
    {
        if (!IsZapretMagicButtonEnabled || _disposed) return;
        if (ReportOtherZapretInstance()) return;
        if (_zapret == null)
        {
            _zapret = new ZapretManager(_logger);
            _zapret.ImmediateExitDetected += OnZapretImmediateExit;
        }

        using var cancellation = new CancellationTokenSource();
        _zapretProbeCts = cancellation;
        var ct = cancellation.Token;
        IsZapretProbing = true;
        _suppressZapretAvToast = true;
        StartZapretProbeElapsedTimer();
        try
        {
            if (ZapretRuntimeDirectory.TestRoot == null && !ZapretAutoStrategy.IsRunningAsAdmin())
            {
                ReportSweepWithoutWinner("not_admin");
                return;
            }
            if (!Directory.Exists(ZapretRuntimeDirectory.Root))
            {
                ReportSweepWithoutWinner("missing_package");
                return;
            }
            using var installLock = ZapretRuntimeDirectory.LockInstallation(ZapretRuntimeDirectory.Root);
            if (!ZapretUpdater.IsInstalled())
            {
                ReportSweepWithoutWinner("missing_package");
                return;
            }
            if (_zapret.HasDriverConflict())
            {
                ZapretStatus = Core.Localization.Strings.ZapretServiceOutcome(ZapretActions.ServiceApply.Conflict);
                return;
            }
            if (_forceFreshProbe && (ZapretEnabled || IsZapretRunning()))
            {
                if (!StopZapret()) return;
                ZapretEnabled = false;
            }
            IsZapretFallback = false;
            ZapretWinningStrategy = string.Empty;
            ZapretProbePassCount = ZapretProbeTotalCount = 0;
            var cached = _forceFreshProbe ? null : ZapretProbeCache.TryLoad(_logger);
            if (cached?.IsRecentAndReliable() == true)
            {
                if (await TryApplyCachedWinnerAsync(cached.Strategy, ct))
                {
                    ZapretProbeCache.RecordSuccess(cached.Strategy, ZapretProbePassCount, ZapretProbeTotalCount, _logger);
                    return;
                }
                ZapretProbeCache.RecordFailure(cached.Strategy, _logger);
            }

            var progress = new Progress<ZapretAutoStrategy.StrategyProgress>(p =>
            {
                if (_zapretProbeCts != cancellation) return;
                ZapretProbeIndex = p.CurrentIndex - 1;
                ZapretProbeTotal = p.TotalCount;
                if (!string.IsNullOrEmpty(p.StrategyName)) ZapretProbeStrategy = p.StrategyName;
                ZapretProbePassCount = p.OkCount;
                ZapretProbeTotalCount = p.TotalChecks;
            });
            var sweep = await InstalledProbe(_zapret, progress, _logger, ct);
            LastProbeLogPath = sweep.ProbeLogPath;
            ct.ThrowIfCancellationRequested();
            if (sweep.Winner != null)
                await StartSweepWinnerAsync(sweep, ct);
            else
                ReportSweepWithoutWinner(sweep.Diagnostic);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReportSweepWithoutWinner("canceled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Zapret strategy probe failed");
            if (!IsZapretRunning())
            {
                ZapretEnabled = false;
                IsZapretFallback = true;
            }
            ZapretStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _zapretProbeCts = null;
            IsZapretProbing = false;
            _suppressZapretAvToast = false;
            ZapretProbeIndex = ZapretProbeTotal = 0;
            ZapretProbeStrategy = string.Empty;
            StopZapretProbeElapsedTimer();
        }
    }

    private async Task StartSweepWinnerAsync(ZapretAutoStrategy.StrategySweepResult sweep, CancellationToken ct)
    {
        if (ReportOtherZapretInstance()) return;
        var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == sweep.Winner);
        if (parsed?.BatPath is not string path)
        {
            ReportSweepWithoutWinner("missing_package");
            return;
        }
        try
        {
            _zapret!.StartFromBat(path, parsed.Arguments);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] Could not start the selected installed strategy");
            ReportSweepWithoutWinner("missing_package");
            return;
        }

        try
        {
            await Task.Delay(1500, ct);
            if (_zapret.Pid is not int pid)
            {
                if (_zapret.HasOwnedProcess)
                {
                    ZapretStatus = Core.Localization.Strings.ZapretStopPending;
                    return;
                }
                IsZapretFallback = true;
                ZapretEnabled = false;
                ZapretStatus = IsRussian
                    ? $"Стратегия {parsed.Name} не запустилась"
                    : $"Strategy {parsed.Name} failed to start";
                return;
            }

            ZapretEnabled = true;
            ZapretWinningStrategy = parsed.Name;
            ZapretStatus = IsRussian
                ? $"Работает [{parsed.Name}] (PID {pid})"
                : $"Running [{parsed.Name}] (PID {pid})";
            ZapretStrategyIndex = ZapretStrategies.IndexOf(parsed.Name);
            var scores = sweep.PerStrategyResults?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, ZapretStrategyTestResult>(StringComparer.Ordinal);
            scores.TryGetValue(parsed.Name, out var winnerScore);
            ZapretProbePassCount = winnerScore?.Passed ?? 0;
            ZapretProbeTotalCount = winnerScore?.Total ?? 0;
            ZapretProbeCache.RecordSweepResults(parsed.Name, ZapretProbePassCount, ZapretProbeTotalCount, scores, _logger);
            RefreshZapretStrategiesDisplay();
            NotifyZapretSummaryChanged();
            SaveSettings();
        }
        catch
        {
            StopZapret();
            throw;
        }
    }

    private void ReportSweepWithoutWinner(string? diagnostic)
    {
        if (!IsZapretRunning())
        {
            IsZapretFallback = true;
            ZapretEnabled = false;
        }
        ZapretStatus = diagnostic switch
        {
            "not_admin" => IsRussian
                ? "Нужны права администратора для подбора стратегии. Перезапустите VPNRouter от админа."
                : "Administrator rights required to probe strategies. Restart VPNRouter as admin.",
            "sweep_timeout" => IsRussian
                ? "Подбор стратегии превысил 10 минут. Проверьте интернет и попробуйте ещё раз."
                : "Strategy probe exceeded 10 min cap. Check network and retry.",
            "missing_package" => Core.Localization.Strings.ZapretRepairPackage,
            "helper_active" => IsZapretRunning() ? Core.Localization.Strings.ZapretStopPending : Core.Localization.Strings.ZapretOtherHelper,
            "driver_conflict" => Core.Localization.Strings.ZapretServiceOutcome(ZapretActions.ServiceApply.Conflict),
            "canceled" => IsRussian ? "Подбор отменён." : "Probe canceled.",
            _ => Strings.ZapretOneTapAllFailedToast,
        };
    }

    private async Task<bool> TryApplyCachedWinnerAsync(string strategy, CancellationToken ct)
    {
        var parsed = _parsedStrategies.FirstOrDefault(s => s.Name == strategy);
        if (parsed?.BatPath is not string path) return false;

        ZapretProbeStrategy = strategy;
        ZapretProbeTotal = 1;
        bool started = false;
        bool keep = false;
        try
        {
            _zapret!.StartFromBat(path, parsed.Arguments);
            started = true;
            await Task.Delay(1500, ct);
            if (_zapret.IsRunning)
            {
                var results = await ZapretAutoStrategy.CheckTargetsAsync(_logger, ct);
                ct.ThrowIfCancellationRequested();
                var passed = results.Count(o => o == ZapretAutoStrategy.ProbeOutcome.Success);
                if (_zapret.Pid is int pid && ZapretAutoStrategy.IsUsable(passed, results.Length))
                {
                    ZapretWinningStrategy = strategy;
                    ZapretEnabled = true;
                    ZapretProbePassCount = passed;
                    ZapretProbeTotalCount = results.Length;
                    ZapretStatus = IsRussian
                        ? $"Работает [{strategy}] (PID {pid}, warm)"
                        : $"Running [{strategy}] (PID {pid}, warm)";
                    ZapretStrategyIndex = ZapretStrategies.IndexOf(strategy);
                    SaveSettings();
                    keep = true;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "[VM] Could not apply cached Zapret strategy");
        }
        finally
        {
            if (started && !keep && !StopZapret())
                _logger.Warning("[VM] Could not confirm cached Zapret trial stopped");
        }
        return false;
    }
#endif

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretMagicButtonEnabled))]
    private bool _isZapretActionRunning;
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

    public string L_ZapretApplyServiceStrategy => Strings.ZapretApplyServiceStrategy;

    [RelayCommand]
    private async Task ApplyZapretServiceStrategyAsync()
    {
#if PLATFORM_WINDOWS
        if (!IsZapretMagicButtonEnabled || _disposed) return;
        if (IsZapretRunning())
        {
            ZapretStatus = Core.Localization.Strings.ZapretOtherHelper;
            return;
        }
        string? selection = ZapretStrategyIndex >= 0 && ZapretStrategyIndex < ZapretStrategies.Count
            ? ZapretStrategies[ZapretStrategyIndex] : null;
        await RunZapretActionAsync(Strings.ZapretApplyServiceStrategy,
            ct => ZapretActions.ApplyServiceStrategyAsync(selection, ct));
#endif
    }

    [RelayCommand]
    private async Task RunZapretTestsAsync()
    {
#if PLATFORM_WINDOWS
        await RunZapretActionAsync(Strings.RunTestsLabel, ZapretActions.RunTestsAsync);
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
    private void OpenZapretFolder()
    {
        OpenFolderInExplorer(ZapretUpdater.ZapretDir);
    }

    [RelayCommand]
    private void OpenZapretGitHub()
    {
        OpenUrl("https://github.com/Flowseal/zapret-discord-youtube");
    }

    private List<VPNRouter.Core.Services.ZapretStrategy> _parsedStrategies = new();

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

    public string LblZapretAirPill
    {
        get
        {
            var name = string.IsNullOrEmpty(ZapretWinningStrategy) ? "..." : ZapretWinningStrategy;
            if (ZapretProbeTotalCount > 0)
                return Strings.ZapretOneTapAirPillScored(name, ZapretProbePassCount, ZapretProbeTotalCount);
#if PLATFORM_WINDOWS
            var pid = _zapret?.Pid ?? 0;
#else
            const int pid = 0;
#endif
            return Strings.ZapretOneTapAirPill(name, pid);
        }
    }

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
}
