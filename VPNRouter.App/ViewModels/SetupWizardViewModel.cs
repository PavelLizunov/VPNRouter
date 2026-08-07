#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly int _initialMtu;
    private readonly bool _initialSplitTunnel;
    private readonly Action<int, bool> _applySettings;
    private readonly Func<IReadOnlyList<HealthCheck.Result>> _runChecks;
    private readonly Func<Task> _exportDiagnostics;
    private int _appliedMtu;
    private bool _appliedSplitTunnel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepOne))]
    [NotifyPropertyChangedFor(nameof(IsStepTwo))]
    [NotifyPropertyChangedFor(nameof(IsStepThree))]
    [NotifyPropertyChangedFor(nameof(IsStepFour))]
    [NotifyPropertyChangedFor(nameof(IsNextVisible))]
    [NotifyPropertyChangedFor(nameof(IsBackVisible))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _currentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFullTunnel))]
    private bool _selectedSplitTunnel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MtuText))]
    [NotifyPropertyChangedFor(nameof(MtuHint))]
    private int _currentMtu;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _checkSummary;
    [ObservableProperty] private string _operationStatus = string.Empty;
    [ObservableProperty] private bool _canUndo;

    public SetupWizardViewModel(
        int initialMtu,
        bool initialSplitTunnel,
        Action<int, bool> applySettings,
        Func<IReadOnlyList<HealthCheck.Result>> runChecks,
        Func<Task> exportDiagnostics)
    {
        _initialMtu = _appliedMtu = CurrentMtu = initialMtu;
        _initialSplitTunnel = _appliedSplitTunnel = SelectedSplitTunnel = initialSplitTunnel;
        _applySettings = applySettings;
        _runChecks = runChecks;
        _exportDiagnostics = exportDiagnostics;
        _checkSummary = Strings.SetupWizardChecksNotRun;
    }

    public event Action? CloseRequested;

    public ObservableCollection<SetupWizardCheckItem> CheckResults { get; } = new();

    public bool IsStepOne => CurrentStep == 0;
    public bool IsStepTwo => CurrentStep == 1;
    public bool IsStepThree => CurrentStep == 2;
    public bool IsStepFour => CurrentStep == 3;
    public bool IsNextVisible => CurrentStep < 2;
    public bool IsBackVisible => CurrentStep > 0 && CurrentStep < 3;
    public string ProgressText => Strings.SetupWizardProgress(CurrentStep + 1);
    public string MtuText => Strings.SetupWizardCurrentMtu(CurrentMtu);
    public string MtuHint => CurrentMtu == TunSettings.DefaultMtu
        ? Strings.SetupWizardMtuDefault
        : Strings.SetupWizardMtuSuspicious;

    public bool SelectedFullTunnel
    {
        get => !SelectedSplitTunnel;
        set
        {
            if (value)
                SelectedSplitTunnel = false;
        }
    }

    public string TitleText => Strings.SetupWizardTitle;
    public string SubtitleText => Strings.SetupWizardSubtitle;
    public string RoutingTitle => Strings.SetupWizardRoutingTitle;
    public string RoutingBody => Strings.SetupWizardRoutingBody;
    public string SplitTitle => Strings.SetupWizardSplitTitle;
    public string SplitHint => Strings.SetupWizardSplitHint;
    public string FullTitle => Strings.SetupWizardFullTitle;
    public string FullHint => Strings.SetupWizardFullHint;
    public string KillSwitchTitle => Strings.SetupWizardKillSwitchTitle;
    public string KillSwitchBody => Strings.SetupWizardKillSwitchBody;
    public string ChecksTitle => Strings.SetupWizardChecksTitle;
    public string ChecksBody => Strings.SetupWizardChecksBody;
    public string RunChecksText => Strings.SetupWizardRunChecks;
    public string ChecksRunningText => Strings.SetupWizardChecksRunning;
    public string RepairTitle => Strings.SetupWizardRepairTitle;
    public string RepairBody => Strings.SetupWizardRepairBody;
    public string ResetMtuText => Strings.SetupWizardResetMtu;
    public string RestoreText => Strings.SetupWizardRestore;
    public string RestoreHint => Strings.SetupWizardRestoreHint;
    public string SafeModeDifference => Strings.SetupWizardSafeModeDifference;
    public string ResultTitle => Strings.SetupWizardResultTitle;
    public string UndoText => Strings.SetupWizardUndo;
    public string ExportText => Strings.SetupWizardExport;
    public string BackText => Strings.SetupWizardBack;
    public string NextText => Strings.SetupWizardNext;
    public string CloseText => Strings.SetupWizardClose;
    public string FinishText => Strings.SetupWizardFinish;

    [RelayCommand]
    private void Next()
    {
        if (CurrentStep < 2)
            CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep > 0)
            CurrentStep--;
    }

    [RelayCommand]
    private async Task RunChecksAsync() => await RefreshChecksAsync();

    [RelayCommand]
    private async Task ResetMtuAsync()
        => await ApplyAndCheckAsync(TunSettings.DefaultMtu, _appliedSplitTunnel, Strings.SetupWizardMtuResetApplied);

    [RelayCommand]
    private async Task RestoreSafeSettingsAsync()
        => await ApplyAndCheckAsync(TunSettings.DefaultMtu, SelectedSplitTunnel, Strings.SetupWizardApplied);

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo || IsBusy)
            return;

        await ApplyAndCheckAsync(_initialMtu, _initialSplitTunnel, Strings.SetupWizardUndone);
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync() => await _exportDiagnostics();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private async Task ApplyAndCheckAsync(int mtu, bool splitTunnel, string successMessage)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            _applySettings(mtu, splitTunnel);
            _appliedMtu = CurrentMtu = mtu;
            _appliedSplitTunnel = splitTunnel;
            SelectedSplitTunnel = splitTunnel;
            CanUndo = _appliedMtu != _initialMtu || _appliedSplitTunnel != _initialSplitTunnel;
            OperationStatus = successMessage;
            CurrentStep = 3;
            await RefreshChecksCoreAsync();
        }
        catch
        {
            OperationStatus = Strings.SetupWizardApplyFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshChecksAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await RefreshChecksCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshChecksCoreAsync()
    {
        CheckSummary = Strings.SetupWizardChecksRunning;
        try
        {
            var results = await Task.Run(_runChecks);
            CheckResults.Clear();
            foreach (var result in results)
                CheckResults.Add(SetupWizardCheckItem.From(result));

            var warnings = results.Count(r => r.Severity == HealthCheck.Level.Warn);
            var errors = results.Count(r => r.Severity == HealthCheck.Level.Err);
            CheckSummary = warnings == 0 && errors == 0
                ? Strings.SetupWizardChecksPassed
                : Strings.SetupWizardChecksSummary(warnings, errors);
        }
        catch
        {
            CheckResults.Clear();
            CheckSummary = Strings.SetupWizardCheckFailed;
        }
    }
}

public sealed record SetupWizardCheckItem(string Level, string Message)
{
    public static SetupWizardCheckItem From(HealthCheck.Result result) => new(
        result.Severity switch
        {
            HealthCheck.Level.Ok => Strings.SetupWizardCheckOk,
            HealthCheck.Level.Warn => Strings.SetupWizardCheckWarning,
            _ => Strings.SetupWizardCheckError,
        },
        result.Message);
}
