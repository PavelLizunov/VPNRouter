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
    private readonly TimeProvider _timeProvider;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    private readonly CancellationTokenSource _lifetime = new();
    private RefreshOperation? _refresh;
    private CancellationTokenSource? _diagnostic;
    private long? _lastSuccessfulRefresh;
    private bool _visible, _disposed, _started, _delivered;
    private string? _report;
    private BusinessSupportReply? _supportReply;
    public bool HasReport => _report != null;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isDiagnosticBusy;
    private RefreshState _refreshState;
    private DiagnosticState _diagnosticState;
    private int _refreshedCount;
    private enum RefreshState { None, NotConfigured, Updating, Updated, Failed, Cancelled }
    private enum DiagnosticState { Ready, LocalOnly, Collecting, Sending, Collected, Delivered, CollectFailed, SendFailed, Cancelled, OpenFailed }

    public string RefreshStatus => _refreshState switch
    {
        RefreshState.NotConfigured => Strings.BusinessSubscriptionNotConfigured,
        RefreshState.Updating => Strings.BusinessSubscriptionUpdating,
        RefreshState.Updated => Strings.BusinessSubscriptionUpdated(_refreshedCount),
        RefreshState.Failed => Strings.BusinessSubscriptionFailed,
        RefreshState.Cancelled => Strings.BusinessSubscriptionCancelled,
        _ => string.Empty
    };
    public bool IsRefreshStatusVisible => _refreshState is RefreshState.Updating or RefreshState.Failed or RefreshState.Cancelled;
    public string DiagnosticStatus => _diagnosticState switch
    {
        DiagnosticState.LocalOnly => Strings.BusinessDiagnosticsLocalOnly,
        DiagnosticState.Collecting => Strings.BusinessDiagnosticsCollecting,
        DiagnosticState.Sending => Strings.BusinessDiagnosticsSending,
        DiagnosticState.Collected => Strings.BusinessDiagnosticsCollected,
        DiagnosticState.Delivered => _supportReply?.Message ?? Strings.BusinessDiagnosticsDelivered,
        DiagnosticState.CollectFailed => Strings.BusinessDiagnosticsCollectFailed,
        DiagnosticState.SendFailed => Strings.BusinessDiagnosticsSendFailed,
        DiagnosticState.Cancelled => Strings.BusinessDiagnosticsCancelled,
        DiagnosticState.OpenFailed => Strings.BusinessDiagnosticsOpenFailed,
        _ => Strings.BusinessDiagnosticsHint
    };
    public bool IsDiagnosticStatusVisible => _diagnosticState != DiagnosticState.Ready;

    private void SetRefreshState(RefreshState state)
    {
        _refreshState = state;
        RefreshLocalization();
    }
    private void SetDiagnosticState(DiagnosticState state)
    {
        _diagnosticState = state;
        RefreshLocalization();
    }
    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(RepairHint));
        OnPropertyChanged(nameof(RefreshStatus));
        OnPropertyChanged(nameof(IsRefreshStatusVisible));
        OnPropertyChanged(nameof(DiagnosticStatus));
        OnPropertyChanged(nameof(IsDiagnosticStatusVisible));
    }

    public AsyncRelayCommand RefreshListCommand { get; }
    public AsyncRelayCommand CollectCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand CancelDiagnosticCommand { get; }
    public RelayCommand OpenReportCommand { get; }

    internal BusinessServicesViewModel(MainWindowViewModel vm, BusinessClientConfiguration configuration,
        BusinessClientHttp http, Action poolChanged, IBusinessDiagnostics? diagnostics = null,
        Func<CancellationToken, Task<bool>>? connectVerified = null, TimeProvider? timeProvider = null)
    {
        _vm = vm; _configuration = configuration; _http = http; _poolChanged = poolChanged;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectVerified = connectVerified ?? vm.ConnectVerifiedBusinessServerAsync;
        _diagnostics = diagnostics ?? new BusinessDiagnostics(configuration, http);
        RefreshListCommand = new(RefreshListAsync, CanRefresh);
        CollectCommand = new(() => RunDiagnosticsAsync(false), () => !_disposed && !IsRepairOpen && !IsDiagnosticBusy);
        SendCommand = new(() => RunDiagnosticsAsync(true), () => !_disposed && !IsRepairOpen && !IsDiagnosticBusy && configuration.SupportEndpoint != null);
        CancelDiagnosticCommand = new(() => _diagnostic?.Cancel(), () => !_disposed && IsDiagnosticBusy);
        OpenReportCommand = new(OpenReport, () => !_disposed && !IsDiagnosticBusy && _report != null);
        _vm.PropertyChanged += OnVmChanged;
        _vm.UpdateVm.PropertyChanged += OnUpdateChanged;
        _timer.Tick += OnTimer;
        if (configuration.Subscription == null) SetRefreshState(RefreshState.NotConfigured);
        else _vm.RefreshBusinessSubscriptionBeforeConnect = RefreshForConnectAsync;
        if (configuration.SupportEndpoint == null) SetDiagnosticState(DiagnosticState.LocalOnly);
    }

    private bool CanRefresh() => _visible && _refresh == null &&
        !_vm.IsBusinessSubscriptionPreflight && !_vm.IsBusinessRepairInProgress && CanRefreshPool;

    private bool CanRefreshPool => !_disposed && _configuration.Subscription != null &&
        (_visible || _vm.IsBusinessSubscriptionPreflight) && _vm.CanRefreshBusinessServers;

    private bool NeedsRefresh => _lastSuccessfulRefresh is not { } stamp ||
        _timeProvider.GetElapsedTime(stamp) >= RefreshInterval;

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible)
        {
            CloseRepair();
            _timer.Stop();
            if (!_vm.IsBusinessSubscriptionPreflight) _refresh?.Cancellation.Cancel();
        }
        else if (!_disposed)
        {
            _timer.Start();
            if (!_started) _ = RefreshListAsync();
        }
        RefreshListCommand.NotifyCanExecuteChanged();
        NotifyRepairCommands();
    }

    private void OnUpdateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.UpdateVm.IsDownloading)) OnVmChanged(sender, e);
    }

    private void OnTimer(object? sender, EventArgs e) => _ = RefreshListAsync();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.IsConnected)
            or nameof(MainWindowViewModel.IsApplying) or nameof(MainWindowViewModel.SelectedServer)
            or nameof(MainWindowViewModel.IsBusinessRepairInProgress) or nameof(_vm.UpdateVm.IsDownloading))) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnVmChanged(sender, e)); return; }
        if (_disposed) return;
        if (e.PropertyName == nameof(MainWindowViewModel.IsBusinessRepairInProgress))
        {
            RefreshListCommand.NotifyCanExecuteChanged();
            return;
        }
        if (_repairStep is RepairStep.Analyzing or RepairStep.Advice or RepairStep.Failed ||
            (_repairStep == RepairStep.Consent && e.PropertyName != nameof(MainWindowViewModel.SelectedServer)))
            CloseRepair();
        NotifyRepairCommands();
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedServer)) return;
        if (_refresh != null && !CanRefreshPool) _refresh.Cancellation.Cancel();
        if (!_started) _ = RefreshListAsync();
        RefreshListCommand.NotifyCanExecuteChanged();
    }

    internal Task RefreshListAsync() => CanRefresh() ? StartRefresh() : Task.CompletedTask;

    private async Task RefreshForConnectAsync(CancellationToken ct)
    {
        try
        {
            if (_refresh is { } current)
            {
                var alreadyCancelled = current.Cancellation.IsCancellationRequested;
                await current.Completion.Task.WaitAsync(ct);
                // A hidden window may already have cancelled the periodic fetch.
                // After it drains, allow a single connection-owned attempt.
                if (!alreadyCancelled) return;
            }
            ct.ThrowIfCancellationRequested();
            if (CanRefreshPool && NeedsRefresh) await StartRefresh().WaitAsync(ct);
        }
        finally
        {
            if (!_visible) _refresh?.Cancellation.Cancel();
        }
    }

    private Task StartRefresh()
    {
        var operation = new RefreshOperation(_lifetime.Token);
        _refresh = operation;
        _started = true;
        _ = RunRefreshAsync(operation);
        return operation.Completion.Task;
    }

    private async Task RunRefreshAsync(RefreshOperation operation)
    {
        var ct = operation.Cancellation.Token;
        try
        {
            IsRefreshing = true;
            NotifyRepairCommands();
            RefreshListCommand.NotifyCanExecuteChanged();
            SetRefreshState(RefreshState.Updating);

            var body = await _http.FetchAsync(_configuration.Subscription!, ct);
            ct.ThrowIfCancellationRequested();
            if (!CanRefreshPool || !_vm.ReplaceBusinessServers(body, _configuration.SourceHash!))
            {
                if (!_disposed) SetRefreshState(RefreshState.Cancelled);
                return;
            }
            _lastSuccessfulRefresh = _timeProvider.GetTimestamp();
            if (_visible)
            {
                _timer.Stop();
                _timer.Start();
            }
            _poolChanged();
            _refreshedCount = _vm.GetBusinessServerChoices().Count;
            SetRefreshState(RefreshState.Updated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_disposed) SetRefreshState(RefreshState.Cancelled);
        }
        catch { if (!_disposed) SetRefreshState(RefreshState.Failed); }
        finally
        {
            _refresh = null;
            operation.Cancellation.Dispose();
            if (!_disposed)
            {
                IsRefreshing = false;
                RefreshListCommand.NotifyCanExecuteChanged();
                NotifyRepairCommands();
            }
            operation.Completion.TrySetResult();
        }
    }

    private sealed class RefreshOperation(CancellationToken lifetime)
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async Task RunDiagnosticsAsync(bool send, bool fresh = false, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsDiagnosticBusy || (IsRepairOpen && !fresh) || (send && _configuration.SupportEndpoint == null)) return;
        IsDiagnosticBusy = true; NotifyDiagnosticCommands();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _diagnostic = operation;
        var uploading = false;
        var acknowledged = false;
        try
        {
            // Retry sends the same saved archive, allowing receiver deduplication.
            if (fresh || !send || _report == null || _delivered)
            {
                SetDiagnosticState(DiagnosticState.Collecting);
                var report = await _diagnostics.CollectAsync(operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                _report = report;
                _delivered = false;
                _supportReply = null;
                OnPropertyChanged(nameof(HasReport));
            }
            if (send)
            {
                uploading = true;
                SetDiagnosticState(DiagnosticState.Sending);
                _supportReply = await _diagnostics.SendWithReplyAsync(_report!, () =>
                {
                    acknowledged = true;
                    void ShowReceipt()
                    {
                        if (_disposed || _diagnostic != operation || _supportReply != null) return;
                        _delivered = true;
                        _supportReply = BusinessSupportReply.Pending;
                        SetDiagnosticState(DiagnosticState.Delivered);
                    }
                    if (Dispatcher.UIThread.CheckAccess()) ShowReceipt();
                    else Dispatcher.UIThread.Post(ShowReceipt);
                }, operation.Token);
                _delivered = true;
            }
            if (!_disposed) SetDiagnosticState(send ? DiagnosticState.Delivered : DiagnosticState.Collected);
        }
        catch (OperationCanceledException) { if (!_disposed) FinishFailure(DiagnosticState.Cancelled); }
        catch { if (!_disposed) FinishFailure(uploading ? DiagnosticState.SendFailed : DiagnosticState.CollectFailed); }
        finally
        {
            _diagnostic = null;
            if (!_disposed) { IsDiagnosticBusy = false; NotifyDiagnosticCommands(); }
        }
        void FinishFailure(DiagnosticState state)
        {
            if (acknowledged) { _delivered = true; _supportReply = BusinessSupportReply.Unavailable; }
            SetDiagnosticState(acknowledged ? DiagnosticState.Delivered : state);
        }
    }

    private void NotifyDiagnosticCommands()
    {
        CollectCommand.NotifyCanExecuteChanged(); SendCommand.NotifyCanExecuteChanged();
        CancelDiagnosticCommand.NotifyCanExecuteChanged(); OpenReportCommand.NotifyCanExecuteChanged();
        NotifyRepairCommands();
    }

    private void OpenReport()
    {
        if (_report == null) return;
        try { using var process = System.Diagnostics.Process.Start(FileManagerHelper.BuildRevealStartInfo(_report)); }
        catch { SetDiagnosticState(DiagnosticState.OpenFailed); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTimer;
        _vm.PropertyChanged -= OnVmChanged;
        if (_vm.RefreshBusinessSubscriptionBeforeConnect == RefreshForConnectAsync)
            _vm.RefreshBusinessSubscriptionBeforeConnect = null;
        _vm.UpdateVm.PropertyChanged -= OnUpdateChanged;
        _lifetime.Cancel();
        // Active operations own/dispose their linked sources after completion.
        _http.Dispose();
        _lifetime.Dispose();
        RefreshListCommand.NotifyCanExecuteChanged(); NotifyDiagnosticCommands();
    }
}
