using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels.Internals;

internal sealed partial class BusinessServicesViewModel
{
    private enum RepairStep { Closed, Consent, Analyzing, Advice, Connecting, Connected, Failed }
    private RepairStep _repairStep;
    private CancellationTokenSource? _repairAttempt;
    private readonly Func<CancellationToken, Task<bool>> _connectVerified;

    public bool HasRepairHint => RepairHint.Length != 0;
    public bool IsRepairOpen => _repairStep != RepairStep.Closed;
    public bool ShowAnalyze => _repairStep == RepairStep.Consent && _configuration.SupportEndpoint != null;
    public bool ShowRepairAnalysis => _repairStep is RepairStep.Analyzing or RepairStep.Advice;
    public bool ShowRepairConnect => (_repairStep is RepairStep.Consent or RepairStep.Advice) && !_vm.IsConnected;

    public string RepairHint => _repairStep switch
    {
        RepairStep.Consent when _configuration.SupportEndpoint == null => Strings.BusinessRepairLocalOnly,
        RepairStep.Consent => Strings.BusinessRepairConsent,
        RepairStep.Connecting => Strings.BusinessRepairChecking,
        RepairStep.Connected => Strings.BusinessRepairConnected,
        RepairStep.Failed => Strings.BusinessRepairFailed,
        _ => string.Empty
    };
    public string RepairActionHint => _vm.IsConnected ? Strings.BusinessRepairActive : Strings.BusinessRepairPlan;
    public bool ShowRepairPlan => _repairStep is RepairStep.Consent or RepairStep.Advice;

    private bool RepairAvailable => _visible && !_disposed && !_vm.IsConnecting && !_vm.IsApplying && !_vm.UpdateVm.IsDownloading;
    private bool CanOpenRepair() => RepairAvailable && !IsRepairOpen && !IsDiagnosticBusy && _repairAttempt == null;
    private bool CanAnalyzeRepair() => RepairAvailable && ShowAnalyze && !IsDiagnosticBusy;
    private bool CanConnectRepair() => RepairAvailable && ShowRepairConnect && !IsRefreshing && !IsDiagnosticBusy && _repairAttempt == null;

    [RelayCommand(CanExecute = nameof(CanOpenRepair))]
    private void OpenRepair()
    {
        if (CanOpenRepair()) SetRepairStep(RepairStep.Consent);
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeRepair))]
    private async Task AnalyzeRepairAsync()
    {
        if (!CanAnalyzeRepair()) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _repairAttempt = attempt;
        SetRepairStep(RepairStep.Analyzing);
        try
        {
            await RunDiagnosticsAsync(send: true, fresh: true, cancellationToken: attempt.Token);
            if (!attempt.IsCancellationRequested && !_disposed) SetRepairStep(RepairStep.Advice);
        }
        finally
        {
            _repairAttempt = null;
            if (!_disposed) NotifyRepairCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectRepair))]
    private async Task ConnectRepairAsync()
    {
        if (!CanConnectRepair()) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _repairAttempt = attempt;
        SetRepairStep(RepairStep.Connecting);
        try
        {
            var connected = await _connectVerified(attempt.Token);
            if (!attempt.IsCancellationRequested && !_disposed)
                SetRepairStep(connected ? RepairStep.Connected : RepairStep.Failed);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch { if (!_disposed && !attempt.IsCancellationRequested) SetRepairStep(RepairStep.Failed); }
        finally
        {
            _repairAttempt = null;
            if (!_disposed) NotifyRepairCommands();
        }
    }

    [RelayCommand]
    private void CloseRepair()
    {
        _repairAttempt?.Cancel();
        if (IsRepairOpen) SetRepairStep(RepairStep.Closed);
    }

    private void SetRepairStep(RepairStep step)
    {
        _repairStep = step;
        OnPropertyChanged(nameof(IsRepairOpen));
        OnPropertyChanged(nameof(HasRepairHint));
        OnPropertyChanged(nameof(ShowAnalyze));
        OnPropertyChanged(nameof(ShowRepairAnalysis));
        OnPropertyChanged(nameof(ShowRepairConnect));
        OnPropertyChanged(nameof(ShowRepairPlan));
        RefreshLocalization();
        NotifyDiagnosticCommands();
    }

    private void NotifyRepairCommands()
    {
        OpenRepairCommand.NotifyCanExecuteChanged();
        AnalyzeRepairCommand.NotifyCanExecuteChanged();
        ConnectRepairCommand.NotifyCanExecuteChanged();
    }
}
