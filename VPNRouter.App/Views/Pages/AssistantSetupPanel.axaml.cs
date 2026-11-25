using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class AssistantSetupPanel : UserControl
{
    private MainWindowViewModel? _viewModel;
    private readonly List<SubscriptionViewModel> _subscriptions = [];
    private CancellationTokenSource? _request;
    private bool _diagnosingSource;
    private bool _moreChecksExpanded;
    private TaskCompletionSource<bool>? _confirmation;

    public AssistantSetupPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as MainWindowViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
        RefreshLabels();
    }

    private void BindViewModel(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm)) return;
        if (_viewModel != null)
        {
            _request?.Cancel();
            _viewModel.PropertyChanged -= ViewModelChanged;
            _viewModel.Subscriptions.CollectionChanged -= SubscriptionsChanged;
        }

        _viewModel = vm;
        if (vm != null)
        {
            vm.PropertyChanged += ViewModelChanged;
            vm.Subscriptions.CollectionChanged += SubscriptionsChanged;
        }
        TrackSubscriptions();
        RefreshLabels();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsSimpleMode) && _viewModel is { IsSimpleMode: false, IsInvokingAssistantControl: false })
            _request?.Cancel();
        if (args.PropertyName is nameof(MainWindowViewModel.IsRussian) or nameof(MainWindowViewModel.HasSourceConnection))
            RefreshLabels();
    }

    private void SubscriptionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_confirmation != null || _diagnosingSource) _request?.Cancel();
        TrackSubscriptions();
        RefreshLabels();
    }

    private void TrackSubscriptions()
    {
        foreach (var subscription in _subscriptions)
            subscription.PropertyChanged -= SubscriptionChanged;
        _subscriptions.Clear();
        if (_viewModel == null) return;
        _subscriptions.AddRange(_viewModel.Subscriptions);
        foreach (var subscription in _subscriptions)
            subscription.PropertyChanged += SubscriptionChanged;
    }

    private void SubscriptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SubscriptionViewModel.Url) or nameof(SubscriptionViewModel.Enabled))
        {
            if (_confirmation != null || _diagnosingSource) _request?.Cancel();
            RefreshLabels();
        }
    }

    private void RefreshLabels()
    {
        var canPlan = _viewModel?.HasAssistantSubscription == true;
        var canUndo = _viewModel?.HasAssistantUndo == true;
        var running = _request != null;
        var consent = _confirmation != null;
        var hasStatus = !string.IsNullOrWhiteSpace(AssistantStatus.Text);
        IsVisible = canPlan || canUndo || running || consent || hasStatus;
        AutomationProperties.SetName(this, Strings.AssistantTitle);

        AssistantGoal.Watermark = Strings.AssistantGoalHint;
        AutomationProperties.SetName(AssistantGoal, Strings.AssistantGoalHint);
        RunAssistant.Content = Strings.AssistantPlan;
        AutomationProperties.SetName(RunAssistant, Strings.AssistantPlan);
        MoreChecksLabel.Text = Strings.AssistantMoreChecks;
        AutomationProperties.SetName(MoreChecksToggle, Strings.AssistantMoreChecks);
        UndoAssistant.Content = Strings.AssistantUndo;
        CancelRun.Content = Strings.AssistantCancel;
        ConfirmAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisSend : Strings.AssistantConfirm;
        CancelAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        DiagnoseSource.Content = Strings.SourceDiagnosisPreview;
        DiagnoseConnection.Content = Strings.SourceConnectionPreview;
        DiagnoseCrash.Content = Strings.AssistantDiagnoseCrash;
        RepairVpn.Content = Strings.AssistantRepair;

        AssistantGoal.IsVisible = canPlan && !running && !consent;
        RunAssistant.IsVisible = canPlan && !running && !consent;
        AssistantStatus.IsVisible = !consent && hasStatus;
        CancelRun.IsVisible = running && !consent;
        UndoAssistant.IsVisible = canUndo && !running && !consent;
        MoreChecksToggle.IsVisible = canPlan && !running && !consent;
        MoreChecks.IsVisible = canPlan && !running && !consent && _moreChecksExpanded;
        RepairVpn.IsVisible = canPlan && !running && !consent;
        DiagnoseCrash.IsVisible = canPlan && !running && !consent;
        DiagnoseSource.IsVisible = canPlan && !running && !consent;
        DiagnoseConnection.IsVisible = canPlan && !running && !consent && _viewModel?.HasSourceConnection == true;
        AssistantConsent.IsVisible = consent;
        ConfirmAssistant.IsVisible = consent;
        CancelAssistant.IsVisible = consent;
        if (!canPlan) _moreChecksExpanded = false;
        MoreChecksChevron.Classes.Set("open", _moreChecksExpanded);
    }

    private void MoreChecksToggle_Click(object? sender, RoutedEventArgs args)
    {
        _moreChecksExpanded = !_moreChecksExpanded;
        RefreshLabels();
    }

    private async void RunAssistant_Click(object? sender, RoutedEventArgs args)
    {
        var goal = AssistantGoal.Text;
        if (_viewModel is not { } vm || string.IsNullOrWhiteSpace(goal)
            || TopLevel.GetTopLevel(this) is not Window window) return;
        window.AddHandler(InputElement.KeyDownEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        try
        {
            await RunAsync(ct => vm.RunAssistantGoalAsync(goal, window, this, ConfirmChangeAsync, ct), Strings.AssistantPlanning);
            if (!vm.IsSimpleMode) vm.StatusText = AssistantStatus.Text ?? string.Empty;
        }
        finally
        {
            window.RemoveHandler(InputElement.KeyDownEvent, CancelFromWindow);
            window.RemoveHandler(InputElement.PointerPressedEvent, CancelFromWindow);
        }
    }

    private async void UndoAssistant_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is { } vm) await RunAsync(vm.UndoAssistantGoalAsync);
    }

    private async void DiagnoseCrash_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is { } vm)
            await RunAsync(vm.DiagnoseAssistantCrashAsync, Strings.AssistantDiagnosingCrash, Strings.AssistantUnavailable);
    }

    private async void DiagnoseSource_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is not { } vm || _request != null || TopLevel.GetTopLevel(this) is not Window window) return;
        _diagnosingSource = true;
        window.AddHandler(InputElement.KeyDownEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        try
        {
            await RunAsync(ct => vm.DiagnoseAssistantSourceAsync(ReferenceEquals(sender, DiagnoseConnection), ConfirmChangeAsync, ct), Strings.SourceDiagnosisWorking, Strings.AssistantUnavailable);
        }
        finally
        {
            window.RemoveHandler(InputElement.KeyDownEvent, CancelFromWindow);
            window.RemoveHandler(InputElement.PointerPressedEvent, CancelFromWindow);
            _diagnosingSource = false;
            RefreshLabels();
        }
    }

    private async void RepairVpn_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is { } vm)
            await RunAsync(vm.RepairAssistantVpnAsync, Strings.AssistantRepairWorking, Strings.AssistantRepairFailed);
    }

    private async Task<bool> ConfirmChangeAsync(string description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmation = choice;
        AssistantConsentText.Text = description;
        AssistantStatus.Text = string.Empty;
        ConfirmAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisSend : Strings.AssistantConfirm;
        CancelAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        ConfirmAssistant.IsEnabled = true;
        CancelAssistant.IsEnabled = true;
        RefreshLabels();
        CancelAssistant.Focus();
        try
        {
            return await choice.Task.WaitAsync(ct);
        }
        finally
        {
            _confirmation = null;
            RefreshLabels();
        }
    }

    private void ConfirmAssistant_Click(object? sender, RoutedEventArgs args)
    {
        if (_confirmation?.TrySetResult(true) == true)
        {
            AssistantConsent.IsVisible = false;
            ConfirmAssistant.IsVisible = false;
            CancelAssistant.IsVisible = false;
            CancelRun.IsVisible = _request != null;
            AssistantStatus.Text = _diagnosingSource ? Strings.SourceDiagnosisWorking : Strings.AssistantWorking;
            AssistantStatus.IsVisible = true;
        }
    }

    private void CancelFromWindow(object? sender, RoutedEventArgs args)
    {
        if (_confirmation != null && args.Source is Avalonia.Visual origin
            && (ReferenceEquals(origin, this) || origin.GetVisualAncestors().Contains(this))
            && args is not KeyEventArgs { Key: Key.Escape }) return;
        DeclineOrCancel();
    }

    private void CancelAssistant_Click(object? sender, RoutedEventArgs args) => DeclineOrCancel();

    private void CancelRun_Click(object? sender, RoutedEventArgs args) => _request?.Cancel();

    private void DeclineOrCancel()
    {
        if (_confirmation?.TrySetResult(false) != true) _request?.Cancel();
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> action, string? progress = null, string? error = null)
    {
        if (_request != null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        DiagnoseConnection.IsEnabled = DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled = RunAssistant.IsEnabled
            = UndoAssistant.IsEnabled = AssistantGoal.IsEnabled = MoreChecksToggle.IsEnabled = false;
        AssistantStatus.Text = progress ?? Strings.AssistantWorking;
        RefreshLabels();
        try
        {
            AssistantStatus.Text = await action(request.Token);
        }
        catch (OperationCanceledException)
        {
            AssistantStatus.Text = Strings.AssistantCancelled;
        }
        catch (Exception)
        {
            AssistantStatus.Text = error ?? Strings.AssistantApplyFailed;
        }
        finally
        {
            _request = null;
            ConfirmAssistant.IsEnabled = false;
            DiagnoseConnection.IsEnabled = DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled = RunAssistant.IsEnabled
                = UndoAssistant.IsEnabled = AssistantGoal.IsEnabled = MoreChecksToggle.IsEnabled = true;
            RefreshLabels();
        }
    }
}
