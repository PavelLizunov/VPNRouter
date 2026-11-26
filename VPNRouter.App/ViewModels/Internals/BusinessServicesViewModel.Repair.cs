using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Services;
using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels.Internals;

internal sealed partial class BusinessServicesViewModel
{
    private enum RepairStep
    {
        Closed,
        Searching,
        Failed,
        Consent,
        Collecting,
        Sending,
        CollectFailed,
        SendFailed,
        ReceiptPending,
        ReceiptWaitCancelled,
        ReceiptQueued,
        ReceiptUnavailable,
        Advice
    }

    private RepairStep _repairStep;
    private CancellationTokenSource? _repairAttempt;
    private bool _repairSendActive;
    private bool _repairStartedOffline;
    private string? _deliveredReceiptAt;
    private RepairStep _lastStickyStep;
    private BusinessSupportReply? _lastDeliveredReply;
    private readonly Func<CancellationToken, Task<bool>> _connectVerified;

    public bool IsRepairOpen => _repairStep != RepairStep.Closed;
    public bool IsRepairBusy => _repairStep is RepairStep.Searching or RepairStep.Collecting
        or RepairStep.Sending or RepairStep.ReceiptPending;
    public bool IsRepairError => _repairStep is RepairStep.Failed or RepairStep.CollectFailed
        or RepairStep.SendFailed;
    public bool IsRepairReceipt => IsStickyResult;
    public bool HasRepairAdvice => _repairStep == RepairStep.Advice;
    public bool ShowRepairHeaderClose => _repairStep is not (RepairStep.Searching or RepairStep.Collecting
        or RepairStep.Sending or RepairStep.Consent or RepairStep.ReceiptPending);
    public bool ShowRepairBottomDismiss => !ShowRepairHeaderClose;
    public bool HasDeliveredReceiptBadge => !IsRepairOpen && _deliveredReceiptAt != null;
    public string DeliveredReceiptBadgeText => _deliveredReceiptAt is { } time
        ? Strings.BusinessRepairDeliveredBadge(time) : string.Empty;
    public bool HasRepairResendNote => _deliveredReceiptAt != null &&
        _repairStep is RepairStep.Consent or RepairStep.Failed && _configuration.SupportEndpoint != null;
    public string RepairResendNote => _deliveredReceiptAt is { } time
        ? Strings.BusinessRepairResendNote(time) : string.Empty;
    public string RepairAdviceFooter => Strings.BusinessRepairAdviceFooter;
    public string RepairRetrySearchText => Strings.BusinessRepairRetrySearch;
    public bool HasRepairRetrySearchAction => _repairStartedOffline && !_vm.IsConnected &&
        (_repairStep == RepairStep.Failed && _configuration.SupportEndpoint != null ||
            _repairStep is RepairStep.CollectFailed or RepairStep.SendFailed);
    public bool HasRepairPrimaryAction => _repairStep switch
    {
        RepairStep.Consent => _configuration.SupportEndpoint != null,
        RepairStep.Failed => true,
        RepairStep.CollectFailed or RepairStep.SendFailed => _configuration.SupportEndpoint != null,
        _ => false
    };

    public string RepairHeading => _repairStep switch
    {
        RepairStep.Searching => Strings.BusinessRepairChecking,
        RepairStep.Failed => Strings.BusinessRepairFailed,
        RepairStep.Consent => Strings.BusinessRepairConsentTitle,
        RepairStep.Collecting or RepairStep.Sending => Strings.BusinessRepairSendingTitle,
        RepairStep.CollectFailed => Strings.BusinessRepairCollectFailedTitle,
        RepairStep.SendFailed => Strings.BusinessRepairSendFailedTitle,
        RepairStep.ReceiptPending or RepairStep.ReceiptWaitCancelled or RepairStep.ReceiptQueued
            or RepairStep.ReceiptUnavailable => Strings.BusinessRepairReceiptTitle,
        RepairStep.Advice => Strings.BusinessRepairAdviceTitle,
        _ => string.Empty
    };

    public string RepairBody => _repairStep switch
    {
        RepairStep.Searching => Strings.BusinessRepairCheckingDetail,
        RepairStep.Failed => _configuration.SupportEndpoint != null
            ? Strings.BusinessRepairConsent
            : Strings.BusinessRepairNoSupportDetail,
        RepairStep.Consent => _vm.IsConnected
            ? Strings.BusinessRepairConsentOnline
            : Strings.BusinessRepairConsent,
        RepairStep.Collecting => Strings.BusinessDiagnosticsCollecting,
        RepairStep.Sending => Strings.BusinessDiagnosticsSending,
        RepairStep.CollectFailed => Strings.BusinessDiagnosticsCollectFailed,
        RepairStep.SendFailed => Strings.BusinessDiagnosticsSendFailed,
        RepairStep.ReceiptPending => Strings.BusinessRepairReceiptPending,
        RepairStep.ReceiptWaitCancelled => Strings.BusinessRepairReceiptWaitCancelled,
        RepairStep.ReceiptQueued => Strings.BusinessRepairReceiptQueued,
        RepairStep.ReceiptUnavailable => Strings.BusinessRepairReceiptUnavailable,
        RepairStep.Advice => _supportReply?.AdviceText ?? string.Empty,
        _ => string.Empty
    };

    public string RepairActionText => _repairStep switch
    {
        RepairStep.Consent when _configuration.SupportEndpoint != null => Strings.BusinessRepairAnalyze,
        RepairStep.Failed => _configuration.SupportEndpoint != null
            ? Strings.BusinessRepairAnalyze
            : Strings.BusinessRepairRetrySearch,
        RepairStep.CollectFailed or RepairStep.SendFailed when _configuration.SupportEndpoint != null =>
            Strings.BusinessRepairRetrySend,
        _ => string.Empty
    };

    public string RepairDismissText => _repairStep switch
    {
        RepairStep.ReceiptPending => Strings.BusinessRepairStopWaiting,
        RepairStep.Searching or RepairStep.Collecting or RepairStep.Sending or RepairStep.Consent =>
            Strings.BusinessRepairCancel,
        _ => Strings.BusinessRepairClose
    };

    public bool HasRepairHint => RepairHint.Length != 0;
    public string RepairHint => _repairStep switch
    {
        RepairStep.Searching => Strings.BusinessRepairChecking,
        RepairStep.Failed => Strings.BusinessRepairFailed,
        _ => string.Empty
    };
    public bool ShowAnalyze => _configuration.SupportEndpoint != null &&
        _repairStep is RepairStep.Consent or RepairStep.Failed or RepairStep.CollectFailed
            or RepairStep.SendFailed;
    public bool ShowRepairAnalysis => _repairStep is RepairStep.Collecting or RepairStep.Sending
        or RepairStep.CollectFailed or RepairStep.SendFailed or RepairStep.ReceiptPending
        or RepairStep.ReceiptWaitCancelled or RepairStep.ReceiptQueued or RepairStep.ReceiptUnavailable
        or RepairStep.Advice;

    private bool RepairHostReady => !_disposed && !_vm.IsConnecting && !_vm.IsApplying &&
        !_vm.UpdateVm.IsDownloading;
    private bool RepairAvailable => _visible && RepairHostReady;
    private bool CanOpenRepair() => RepairAvailable && !IsRepairOpen && !IsDiagnosticBusy &&
        _repairAttempt == null && !_repairSendActive &&
        (!_vm.IsConnected || _configuration.SupportEndpoint != null);
    private bool CanRunRepairPrimary() => RepairHostReady && IsRepairOpen && !IsDiagnosticBusy &&
        _repairAttempt == null && !_repairSendActive && HasRepairPrimaryAction;
    private bool CanRetryRepairSearch() => CanRunRepairPrimary() && HasRepairRetrySearchAction;
    private bool CanAnalyzeRepair() => CanRunRepairPrimary() && ShowAnalyze;

    [RelayCommand(CanExecute = nameof(CanOpenRepair))]
    private async Task OpenRepairAsync()
    {
        if (!CanOpenRepair()) return;
        if (_vm.IsConnected)
        {
            BeginRepairWorkflow(RepairStep.Consent);
            return;
        }
        BeginRepairWorkflow(RepairStep.Searching);
        await RunLocalRepairAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunRepairPrimary))]
    private async Task RepairPrimaryAsync()
    {
        if (!CanRunRepairPrimary()) return;
        switch (_repairStep)
        {
            case RepairStep.Consent:
            case RepairStep.CollectFailed:
                await SendRepairReportAsync(reuseArchive: false);
                break;
            case RepairStep.Failed when _configuration.SupportEndpoint != null:
                await SendRepairReportAsync(reuseArchive: false);
                break;
            case RepairStep.Failed:
                await RunLocalRepairAsync();
                break;
            case RepairStep.SendFailed:
                await SendRepairReportAsync(reuseArchive: true);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryRepairSearch))]
    private async Task RetryRepairSearchAsync()
    {
        if (CanRetryRepairSearch()) await RunLocalRepairAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeRepair))]
    private async Task AnalyzeRepairAsync()
    {
        if (!CanAnalyzeRepair()) return;
        await SendRepairReportAsync(reuseArchive: _repairStep == RepairStep.SendFailed);
    }

    private async Task RunLocalRepairAsync()
    {
        if (_disposed || _repairAttempt != null || IsDiagnosticBusy) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _repairAttempt = attempt;
        SetRepairStep(RepairStep.Searching);
        try
        {
            bool connected;
            try { connected = await _connectVerified(attempt.Token); }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return; }
            catch { connected = false; }
            if (attempt.IsCancellationRequested || _disposed) return;
            SetRepairStep(connected ? RepairStep.Closed : RepairStep.Failed);
        }
        finally
        {
            _repairAttempt = null;
            if (!_disposed) NotifyRepairState();
        }
    }

    private async Task SendRepairReportAsync(bool reuseArchive)
    {
        if (!CanRunRepairPrimary() || _configuration.SupportEndpoint == null || _delivered) return;
        if (reuseArchive && _report == null) reuseArchive = false;
        var previousStep = _repairStep;
        _repairSendActive = true;
        SetRepairStep(RepairStep.Collecting);
        try
        {
            var outcome = await RunDiagnosticsCoreAsync(send: true, fresh: !reuseArchive, cancellationToken: default);
            if (_disposed || _repairStep is RepairStep.Closed or RepairStep.ReceiptWaitCancelled) return;
            switch (outcome)
            {
                case DiagnosticOutcome.CollectFailed:
                    SetRepairStep(RepairStep.CollectFailed);
                    break;
                case DiagnosticOutcome.SendFailed:
                    SetRepairStep(RepairStep.SendFailed);
                    break;
                case DiagnosticOutcome.Cancelled:
                    SetRepairStep(RepairStep.Closed);
                    break;
                case DiagnosticOutcome.Delivered:
                    SetRepairStep(StepForReply(_supportReply));
                    break;
                default:
                    SetRepairStep(previousStep);
                    break;
            }
        }
        finally
        {
            _repairSendActive = false;
            if (!_disposed) NotifyRepairState();
        }
    }

    [RelayCommand]
    private void CloseRepair()
    {
        if (_repairStep == RepairStep.Closed) return;
        switch (_repairStep)
        {
            case RepairStep.Searching:
                _repairAttempt?.Cancel();
                SetRepairStep(RepairStep.Closed);
                break;
            case RepairStep.Collecting:
                _diagnostic?.Cancel();
                SetRepairStep(RepairStep.Closed);
                break;
            case RepairStep.Sending:
                // A receipt may already be on its way to the dispatcher.
                // Let the upload settle before deciding whether to close the card.
                _diagnostic?.Cancel();
                break;
            case RepairStep.ReceiptPending:
                // The upload is already acknowledged. Keep the receipt visible
                // after stopping the optional reply wait; a second close dismisses it.
                _diagnostic?.Cancel();
                SetRepairStep(RepairStep.ReceiptWaitCancelled);
                break;
            default:
                SetRepairStep(RepairStep.Closed);
                break;
        }
    }

    [RelayCommand]
    private void ReopenDeliveredReceipt()
    {
        if (_disposed || IsRepairOpen || _deliveredReceiptAt == null) return;
        _supportReply = _lastDeliveredReply;
        var target = _lastStickyStep is RepairStep.Closed or RepairStep.ReceiptPending
            ? RepairStep.ReceiptWaitCancelled : _lastStickyStep;
        SetRepairStep(target);
    }

    private void CloseRepairForHide()
    {
        if (_repairStep == RepairStep.Closed) return;
        if (IsStickyResult)
        {
            if (_repairStep == RepairStep.ReceiptPending)
            {
                _diagnostic?.Cancel();
                SetRepairStep(RepairStep.ReceiptWaitCancelled);
            }
            return;
        }
        _repairAttempt?.Cancel();
        _diagnostic?.Cancel();
        SetRepairStep(RepairStep.Closed);
    }

    private void BeginRepairWorkflow(RepairStep step)
    {
        if (IsRepairOpen) return;
        _repairStartedOffline = step == RepairStep.Searching;
        if (_delivered || _diagnosticState is not (DiagnosticState.Ready or DiagnosticState.LocalOnly
            or DiagnosticState.Collected))
        {
            _supportReply = null;
            _delivered = false;
            SetDiagnosticState(_report != null ? DiagnosticState.Collected : DiagnosticState.Ready);
        }
        SetRepairStep(step);
    }

    private void OnRepairHostChanged(string? propertyName)
    {
        if (_disposed) return;
        if (_repairStep == RepairStep.Closed)
        {
            NotifyRepairCommands();
            return;
        }

        if (propertyName == nameof(MainWindowViewModel.SelectedServer))
        {
            if (_repairAttempt != null && _vm.IsBusinessSubscriptionPreflight) return;
            if (_repairStep is RepairStep.Searching or RepairStep.Failed)
            {
                _repairAttempt?.Cancel();
                SetRepairStep(RepairStep.Closed);
            }
            else
            {
                NotifyRepairState();
            }
            return;
        }

        if (propertyName == nameof(_vm.UpdateVm.IsDownloading))
        {
            if (_vm.UpdateVm.IsDownloading) CloseRepair();
            else NotifyRepairState();
            return;
        }

        if (propertyName is nameof(MainWindowViewModel.IsConnecting)
            or nameof(MainWindowViewModel.IsConnected) or nameof(MainWindowViewModel.IsApplying))
        {
            // A repair owns its own connecting/sending transitions. External
            // connection changes close transient prompts but never discard an
            // already delivered receipt or advice.
            if (_repairAttempt != null || _repairSendActive)
            {
                NotifyRepairState();
                return;
            }
            if (IsStickyResult) NotifyRepairState();
            else CloseRepair();
            return;
        }

        NotifyRepairCommands();
    }

    private bool IsStickyResult => _repairStep is RepairStep.ReceiptPending
        or RepairStep.ReceiptWaitCancelled or RepairStep.ReceiptQueued or RepairStep.ReceiptUnavailable
        or RepairStep.Advice;

    private void SyncRepairFromDiagnosticState(DiagnosticState state)
    {
        if (!_repairSendActive || _repairStep is not (RepairStep.Collecting or RepairStep.Sending
            or RepairStep.ReceiptPending)) return;
        switch (state)
        {
            case DiagnosticState.Collecting:
                SetRepairStep(RepairStep.Collecting);
                break;
            case DiagnosticState.Sending:
                SetRepairStep(RepairStep.Sending);
                break;
            case DiagnosticState.Delivered:
                SetRepairStep(StepForReply(_supportReply));
                break;
        }
    }

    private static RepairStep StepForReply(BusinessSupportReply? reply) => reply switch
    {
        null => RepairStep.ReceiptUnavailable,
        { IsReadyAdvice: true } => RepairStep.Advice,
        { IsPendingAnalysis: true } => RepairStep.ReceiptPending,
        _ => reply.State switch
        {
            "wait_cancelled" => RepairStep.ReceiptWaitCancelled,
            "queued" => RepairStep.ReceiptQueued,
            _ => RepairStep.ReceiptUnavailable
        }
    };

    private void NotifyRepairState()
    {
        OnPropertyChanged(nameof(IsRepairOpen));
        OnPropertyChanged(nameof(IsRepairBusy));
        OnPropertyChanged(nameof(IsRepairError));
        OnPropertyChanged(nameof(HasRepairPrimaryAction));
        OnPropertyChanged(nameof(HasRepairRetrySearchAction));
        OnPropertyChanged(nameof(IsRepairReceipt));
        OnPropertyChanged(nameof(HasRepairAdvice));
        OnPropertyChanged(nameof(ShowRepairHeaderClose));
        OnPropertyChanged(nameof(ShowRepairBottomDismiss));
        OnPropertyChanged(nameof(HasDeliveredReceiptBadge));
        OnPropertyChanged(nameof(DeliveredReceiptBadgeText));
        OnPropertyChanged(nameof(HasRepairResendNote));
        OnPropertyChanged(nameof(RepairResendNote));
        OnPropertyChanged(nameof(RepairHeading));
        OnPropertyChanged(nameof(RepairBody));
        OnPropertyChanged(nameof(RepairActionText));
        OnPropertyChanged(nameof(RepairDismissText));
        OnPropertyChanged(nameof(HasRepairHint));
        OnPropertyChanged(nameof(RepairHint));
        OnPropertyChanged(nameof(ShowAnalyze));
        OnPropertyChanged(nameof(ShowRepairAnalysis));
        NotifyRepairCommands();
    }

    private void SetRepairStep(RepairStep step)
    {
        _repairStep = step;
        if (IsStickyResult)
        {
            _deliveredReceiptAt ??= DateTime.Now.ToString("HH:mm");
            _lastStickyStep = step;
            _lastDeliveredReply = _supportReply;
        }
        NotifyRepairState();
    }

    private void NotifyRepairCommands()
    {
        OpenRepairCommand.NotifyCanExecuteChanged();
        AnalyzeRepairCommand.NotifyCanExecuteChanged();
        RepairPrimaryCommand.NotifyCanExecuteChanged();
        RetryRepairSearchCommand.NotifyCanExecuteChanged();
        CloseRepairCommand.NotifyCanExecuteChanged();
        ReopenDeliveredReceiptCommand.NotifyCanExecuteChanged();
        CollectCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }
}
