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

/// <summary>
/// Manages Windows Service install/uninstall/restart UI. On macOS this VM
/// reports IsAvailable=false and the corresponding UI section is hidden.
/// </summary>
public partial class ServiceViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private bool _isLoading;

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _autostartChecked;
    [ObservableProperty] private string _statusMessage = string.Empty;
    // v2.27 §4.5 — expose service PID so the UI can show a prominent
    // "● Running — PID 1234" status line instead of the tiny pill users miss.
    // Best-effort; null when not running or when process enumeration fails
    // (cross-session access-denied for a non-admin App, for example).
    [ObservableProperty] private int? _servicePid;
    [ObservableProperty] private bool _isBusy;

#if PLATFORM_WINDOWS
    public bool IsAvailable => true;
#else
    public bool IsAvailable => false;
#endif

    public ServiceViewModel(ILogger logger)
    {
        _logger = logger;
        Refresh();
    }

    public void Refresh()
    {
#if PLATFORM_WINDOWS
        _isLoading = true;
        try
        {
            IsInstalled = WindowsServiceHelper.IsInstalled();
            IsRunning = WindowsServiceHelper.IsRunning();
            AutostartChecked = IsInstalled;
            ServicePid = IsRunning ? ResolveServicePid() : null;
        }
        finally
        {
            _isLoading = false;
        }
#endif
    }

#if PLATFORM_WINDOWS
    // Best-effort lookup of VPNRouter.Service PID via Process enumeration.
    // Returns null if the service isn't running, enumeration fails, or the
    // caller lacks rights to see cross-session processes (non-admin App).
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
