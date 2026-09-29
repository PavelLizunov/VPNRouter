using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Services;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private const int TgProxySettleDelayMs = 2000;

    public bool IsTgProxyAvailable => OperatingSystem.IsWindows();

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

    public bool IsTgProxyToolSelected => SelectedToolIndex == 1;

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

#if PLATFORM_WINDOWS
    private void OnTgProxyStats(string stats)
        => Dispatcher.UIThread.Post(() => TgProxyStats = ParseStatsShort(stats));
#endif

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
}
