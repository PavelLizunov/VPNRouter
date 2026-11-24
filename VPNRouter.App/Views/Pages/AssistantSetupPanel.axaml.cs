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
        if (args.PropertyName == nameof(MainWindowViewModel.IsRussian)) RefreshLabels();
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
        IsVisible = _request != null || canPlan || canUndo;
        OpenAssistant.Content = Strings.AssistantTitle;
        AssistantGoal.Watermark = Strings.AssistantGoalHint;
        AutomationProperties.SetName(AssistantGoal, Strings.AssistantGoalHint);
        AssistantNotice.Text = Strings.AssistantDataNotice;
        RunAssistant.Content = Strings.AssistantPlan;
        ConfirmAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisSend : Strings.AssistantConfirm;
        DiagnoseCrash.Content = Strings.AssistantDiagnoseCrash;
        SourceDiagnosisDetails.Header = Strings.SourceDiagnosisTitle;
        SourceDiagnosisDetails.IsVisible = canPlan;
        SourceDiagnosisNotice.Text = Strings.SourceDiagnosisNotice;
        DiagnoseSource.Content = Strings.SourceDiagnosisPreview;
        RepairVpn.Content = Strings.AssistantRepair;
        CancelAssistant.Content = _confirmation == null ? Strings.AssistantCancel :
            _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        UndoAssistant.Content = Strings.AssistantUndo;
        AssistantGoal.IsVisible = canPlan;
        AssistantNotice.IsVisible = canPlan;
        RunAssistant.IsVisible = canPlan;
        DiagnoseCrash.IsVisible = canPlan;
        RepairVpn.IsVisible = canPlan;
        UndoAssistant.IsVisible = canUndo;
    }

    private void OpenAssistant_Click(object? sender, RoutedEventArgs args)
    {
        AssistantFields.IsVisible = !AssistantFields.IsVisible;
        if (AssistantFields.IsVisible) AssistantGoal.Focus();
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
            await RunAsync(ct => vm.DiagnoseAssistantSourceAsync(ConfirmChangeAsync, ct), Strings.SourceDiagnosisWorking, Strings.AssistantUnavailable);
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
        AssistantStatus.Text = description;
        CancelAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        ConfirmAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisSend : Strings.AssistantConfirm;
        ConfirmAssistant.IsVisible = true;
        CancelAssistant.Focus();
        try
        {
            return await choice.Task.WaitAsync(ct);
        }
        finally
        {
            _confirmation = null;
            CancelAssistant.Content = Strings.AssistantCancel;
            ConfirmAssistant.IsVisible = false;
        }
    }

    private void ConfirmAssistant_Click(object? sender, RoutedEventArgs args)
    {
        if (_confirmation?.TrySetResult(true) == true)
        {
            ConfirmAssistant.IsVisible = false;
            AssistantStatus.Text = _diagnosingSource ? Strings.SourceDiagnosisWorking : Strings.AssistantWorking;
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

    private void DeclineOrCancel()
    {
        if (_confirmation?.TrySetResult(false) != true) _request?.Cancel();
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> action, string? progress = null, string? error = null)
    {
        if (_request != null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled = RunAssistant.IsEnabled
            = UndoAssistant.IsEnabled = OpenAssistant.IsEnabled = AssistantGoal.IsEnabled = false;
        CancelAssistant.IsVisible = true;
        AssistantStatus.Text = progress ?? Strings.AssistantWorking;
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
            CancelAssistant.IsVisible = false;
            DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled = RunAssistant.IsEnabled
                = UndoAssistant.IsEnabled = OpenAssistant.IsEnabled = AssistantGoal.IsEnabled = true;
            RefreshLabels();
        }
    }
}
