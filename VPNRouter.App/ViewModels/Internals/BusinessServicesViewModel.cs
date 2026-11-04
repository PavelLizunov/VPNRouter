using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Services;
using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels.Internals;

internal sealed partial class BusinessServicesViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _vm;
    private readonly BusinessClientConfiguration _configuration;
    private readonly BusinessClientHttp _http;
    private readonly IBusinessDiagnostics _diagnostics;
    private readonly Action _poolChanged;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refresh, _diagnostic;
    private bool _visible, _disposed, _started, _delivered;
    private string? _report;
    public bool HasReport => _report != null;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isDiagnosticBusy;
    [ObservableProperty] private string _refreshStatus = string.Empty;
    [ObservableProperty] private string _diagnosticStatus = Strings.BusinessDiagnosticsHint;

    public AsyncRelayCommand RefreshListCommand { get; }
    public AsyncRelayCommand CollectCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand CancelDiagnosticCommand { get; }
    public RelayCommand OpenReportCommand { get; }

    internal BusinessServicesViewModel(MainWindowViewModel vm, BusinessClientConfiguration configuration,
        BusinessClientHttp http, Action poolChanged, IBusinessDiagnostics? diagnostics = null)
    {
        _vm = vm; _configuration = configuration; _http = http; _poolChanged = poolChanged;
        _diagnostics = diagnostics ?? new BusinessDiagnostics(configuration, http);
        RefreshListCommand = new(RefreshListAsync, CanRefresh);
        CollectCommand = new(() => RunDiagnosticsAsync(false), () => !_disposed && !IsDiagnosticBusy);
        SendCommand = new(() => RunDiagnosticsAsync(true), () => !_disposed && !IsDiagnosticBusy && configuration.SupportEndpoint != null);
        CancelDiagnosticCommand = new(() => _diagnostic?.Cancel(), () => !_disposed && IsDiagnosticBusy);
        OpenReportCommand = new(OpenReport, () => !_disposed && !IsDiagnosticBusy && _report != null);
        _vm.PropertyChanged += OnVmChanged;
        _timer.Tick += OnTimer;
        if (configuration.Subscription == null) RefreshStatus = Strings.BusinessSubscriptionNotConfigured;
        if (configuration.SupportEndpoint == null) DiagnosticStatus = Strings.BusinessDiagnosticsLocalOnly;
    }

    private bool CanRefresh() => !_disposed && _visible && !IsRefreshing &&
        !_vm.IsConnecting && !_vm.IsConnected && _configuration.Subscription != null;

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) { _timer.Stop(); _refresh?.Cancel(); }
        else if (!_disposed)
        {
            _timer.Start();
            if (!_started && CanRefresh()) { _started = true; _ = RefreshListAsync(); }
        }
        RefreshListCommand.NotifyCanExecuteChanged();
    }

    private void OnTimer(object? sender, EventArgs e) { if (CanRefresh()) _ = RefreshListAsync(); }
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.IsConnected))) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnVmChanged(sender, e)); return; }
        if (_disposed) return;
        if (_vm.IsConnecting || _vm.IsConnected) _refresh?.Cancel();
        if (!_started && CanRefresh()) { _started = true; _ = RefreshListAsync(); }
        RefreshListCommand.NotifyCanExecuteChanged();
    }

    internal async Task RefreshListAsync()
    {
        if (!CanRefresh()) return;
        IsRefreshing = true;
        RefreshListCommand.NotifyCanExecuteChanged();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refresh = operation;
        RefreshStatus = Strings.BusinessSubscriptionUpdating;
        try
        {
            var body = await _http.FetchAsync(_configuration.Subscription!, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_disposed || !_visible || _vm.IsConnected || _vm.IsConnecting) return;
            if (!_vm.ReplaceBusinessServers(body, _configuration.SourceHash!))
                throw new InvalidOperationException("Server pool is busy.");
            _poolChanged();
            RefreshStatus = Strings.BusinessSubscriptionUpdated(_vm.GetBusinessServerChoices().Count);
        }
        catch (OperationCanceledException) { if (!_disposed) RefreshStatus = Strings.BusinessSubscriptionCancelled; }
        catch { if (!_disposed) RefreshStatus = Strings.BusinessSubscriptionFailed; }
        finally
        {
            _refresh = null;
            if (!_disposed) { IsRefreshing = false; RefreshListCommand.NotifyCanExecuteChanged(); }
        }
    }

    internal async Task RunDiagnosticsAsync(bool send)
    {
        if (_disposed || IsDiagnosticBusy || (send && _configuration.SupportEndpoint == null)) return;
        IsDiagnosticBusy = true; NotifyDiagnosticCommands();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnostic = operation;
        var uploading = false;
        try
        {
            // Retry sends the same saved archive, allowing receiver deduplication.
            if (!send || _report == null || _delivered)
            {
                DiagnosticStatus = Strings.BusinessDiagnosticsCollecting;
                var report = await _diagnostics.CollectAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _report = report;
                _delivered = false;
                OnPropertyChanged(nameof(HasReport));
            }
            if (send)
            {
                uploading = true;
                DiagnosticStatus = Strings.BusinessDiagnosticsSending;
                await _diagnostics.SendAsync(_report!, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _delivered = true;
            }
            if (!_disposed) DiagnosticStatus = send ? Strings.BusinessDiagnosticsDelivered : Strings.BusinessDiagnosticsCollected;
        }
        catch (OperationCanceledException) { if (!_disposed) DiagnosticStatus = Strings.BusinessDiagnosticsCancelled; }
        catch { if (!_disposed) DiagnosticStatus = uploading ? Strings.BusinessDiagnosticsSendFailed : Strings.BusinessDiagnosticsCollectFailed; }
        finally
        {
            _diagnostic = null;
            if (!_disposed) { IsDiagnosticBusy = false; NotifyDiagnosticCommands(); }
        }
    }

    private void NotifyDiagnosticCommands()
    {
        CollectCommand.NotifyCanExecuteChanged(); SendCommand.NotifyCanExecuteChanged();
        CancelDiagnosticCommand.NotifyCanExecuteChanged(); OpenReportCommand.NotifyCanExecuteChanged();
    }

    private void OpenReport()
    {
        if (_report == null) return;
        try { using var process = System.Diagnostics.Process.Start(FileManagerHelper.BuildRevealStartInfo(_report)); }
        catch { DiagnosticStatus = Strings.BusinessDiagnosticsOpenFailed; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTimer;
        _vm.PropertyChanged -= OnVmChanged;
        _lifetime.Cancel();
        // Active operations own/dispose their linked sources after completion.
        _http.Dispose();
        _lifetime.Dispose();
        RefreshListCommand.NotifyCanExecuteChanged(); NotifyDiagnosticCommands();
    }
}
