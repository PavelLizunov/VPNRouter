using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.App.Localization;
#if PLATFORM_WINDOWS
using VPNRouter.App.Services;
#endif

namespace VPNRouter.App.ViewModels;

public partial class ServiceViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private bool _isLoading = false;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _autostartChecked;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int? _servicePid;
    [ObservableProperty] private bool _isBusy;

#if PLATFORM_WINDOWS
    public bool IsAvailable => true;
#else
    public bool IsAvailable => false;
#endif

    public ServiceViewModel(ILogger logger, bool eagerRefresh = false)
    {
        _logger = logger;
        if (eagerRefresh)
            Refresh();
    }

    public void Refresh()
    {
#if PLATFORM_WINDOWS
        _isLoading = true;
        try
        {
            var isInstalled = WindowsServiceHelper.IsInstalled();
            var isRunning = WindowsServiceHelper.IsRunning();
            var pid = isRunning ? ResolveServicePid() : null;

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyState(isInstalled, isRunning, pid);
                _isLoading = false;
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ApplyState(isInstalled, isRunning, pid);
                    _isLoading = false;
                });
            }
        }
        catch
        {
            _isLoading = false;
        }
#endif
    }

    private void ApplyState(bool isInstalled, bool isRunning, int? pid)
    {
        IsInstalled = isInstalled;
        IsRunning = isRunning;
        AutostartChecked = isInstalled;
        ServicePid = pid;
    }

#if PLATFORM_WINDOWS
    private static int? ResolveServicePid()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("VPNRouter.Service");
            try
            {
                return procs.Length > 0 ? procs[0].Id : null;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }
#endif

    partial void OnAutostartCheckedChanged(bool value)
    {
        if (_isLoading) return;
#if PLATFORM_WINDOWS
        _ = ToggleAutostartAsync(value);
#endif
    }

#if PLATFORM_WINDOWS
    private async Task ToggleAutostartAsync(bool wantInstalled)
    {
        IsBusy = true;
        try
        {
            if (wantInstalled && !IsInstalled)
            {
                StatusMessage = Strings.InstallingService;
                var installResult = await Task.Run(() => WindowsServiceHelper.Install());
                if (!installResult.Success)
                {
                    _logger.Warning("[ServiceVm] Install failed: {Msg}", installResult.Message);
                    StatusMessage = installResult.Message;
                    _isLoading = true;
                    AutostartChecked = false;
                    _isLoading = false;
                    return;
                }

                var startResult = await Task.Run(() => WindowsServiceHelper.Start());
                if (!startResult.Success)
                {
                    _logger.Warning("[ServiceVm] Start failed: {Msg}", startResult.Message);
                    StatusMessage = startResult.Message;
                }
                else
                {
                    StatusMessage = startResult.Message;
                }
            }
            else if (!wantInstalled && IsInstalled)
            {
                StatusMessage = Strings.RemovingService;
                if (IsRunning)
                    await Task.Run(() => WindowsServiceHelper.Stop());

                var uninstallResult = await Task.Run(() => WindowsServiceHelper.Uninstall());
                StatusMessage = uninstallResult.Message;
                if (!uninstallResult.Success)
                    _logger.Warning("[ServiceVm] Uninstall failed: {Msg}", uninstallResult.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ServiceVm] Toggle autostart error");
            StatusMessage = ex.Message;
        }
        finally
        {
            Refresh();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartService()
    {
        if (!IsRunning) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => WindowsServiceHelper.Stop());
            var result = await Task.Run(() => WindowsServiceHelper.Start());
            StatusMessage = result.Message;
        }
        finally
        {
            Refresh();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReinstallService()
    {
        if (!IsInstalled) return;
        IsBusy = true;
        try
        {
            if (IsRunning) await Task.Run(() => WindowsServiceHelper.Stop());
            await Task.Run(() => WindowsServiceHelper.Uninstall());
            var installResult = await Task.Run(() => WindowsServiceHelper.Install());
            if (installResult.Success)
            {
                var startResult = await Task.Run(() => WindowsServiceHelper.Start());
                StatusMessage = startResult.Message;
            }
            else
            {
                StatusMessage = installResult.Message;
            }
        }
        finally
        {
            Refresh();
            IsBusy = false;
        }
    }
#else
    [RelayCommand] private Task RestartService() => Task.CompletedTask;
    [RelayCommand] private Task ReinstallService() => Task.CompletedTask;
#endif
}
