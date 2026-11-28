using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class AssistantSetupPanel : UserControl
{
    private MainWindowViewModel? _viewModel;
    private readonly List<SubscriptionViewModel> _subscriptions = [];
    private CancellationTokenSource? _request;
    private bool _diagnosingSource;
    private bool _diagnosingConnection;
    private bool _sourceSentToProvider;
    private Control? _lastTrigger;
    private TaskCompletionSource<bool>? _confirmation;

    public AssistantSetupPanel()
    {
        InitializeComponent();
        AssistantGoal.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            UpdateComposer();
            if (_request == null) AssistantStatus.Text = string.Empty;
        };
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
        if (args.PropertyName == nameof(MainWindowViewModel.HasSourceConnection) && _request == null && _confirmation == null)
            AssistantStatus.Text = string.Empty;
        if (args.PropertyName is nameof(MainWindowViewModel.IsRussian) or nameof(MainWindowViewModel.HasSourceConnection))
            RefreshLabels();
    }

    private void SubscriptionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_confirmation != null || _diagnosingSource) _request?.Cancel();
        else if (_request == null) AssistantStatus.Text = string.Empty;
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
            else if (_request == null) AssistantStatus.Text = string.Empty;
            RefreshLabels();
        }
    }

    private void RefreshLabels()
    {
        var canPlan = _viewModel?.HasAssistantSubscription == true;
        var canUndo = _viewModel?.HasAssistantUndo == true;
        IsVisible = _request != null || canPlan || canUndo;
        AssistantHeading.Text = Strings.AssistantTitle;
        AssistantGoal.Watermark = Strings.AssistantGoalHint;
        AutomationProperties.SetName(AssistantGoal, Strings.AssistantGoalHint);
        AssistantCrashNotice.Text = Strings.AssistantCrashNotice;
        RunAssistant.Content = Strings.AssistantPlan;
        ConfirmAssistant.Content = _diagnosingSource
            ? (_diagnosingConnection ? Strings.SourceConnectionSend : Strings.SourceDiagnosisSend)
            : Strings.AssistantConfirm;
        DiagnoseCrash.Content = Strings.AssistantDiagnoseCrash;
        DiagnosticsDetails.Header = Strings.AssistantDiagnostics;
        DiagnosticsDetails.IsVisible = canPlan;
        SourceDiagnosisNotice.Text = Strings.SourceDiagnosisNotice;
        DiagnoseSource.Content = Strings.SourceDiagnosisPreview;
        DiagnoseConnection.Content = Strings.SourceConnectionPreview;
        DiagnoseConnection.IsVisible = canPlan && _viewModel?.HasSourceConnection == true;
        RepairVpn.Content = Strings.AssistantRepair;
        CancelAssistant.Content = _confirmation == null ? Strings.AssistantCancel :
            _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        UndoAssistant.Content = Strings.AssistantUndo;
        AssistantFields.IsVisible = canPlan;
        UpdateComposer();
        UndoAssistant.IsVisible = canUndo;
    }

    private void UpdateComposer()
    {
        var hasText = !string.IsNullOrWhiteSpace(AssistantGoal.Text);
        var notice = hasText && AssistantRouteRecipe.Parse(AssistantGoal.Text!) != null
            ? Strings.AssistantLocalNotice : Strings.AssistantDataNotice;
        AssistantNotice.Text = notice;
        AutomationProperties.SetHelpText(AssistantGoal, notice);
        RunAssistant.IsEnabled = _request == null && hasText;
        AssistantNotice.IsVisible = _request == null && hasText;
    }

    private void AssistantGoal_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers != KeyModifiers.None ||
            !AssistantGoal.IsFocused || !RunAssistant.IsEffectivelyEnabled) return;
        args.Handled = true;
        RunAssistant.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async void RunAssistant_Click(object? sender, RoutedEventArgs args)
    {
        var goal = AssistantGoal.Text;
        if (_request != null || _viewModel is not { } vm || string.IsNullOrWhiteSpace(goal)
            || TopLevel.GetTopLevel(this) is not Window window) return;
        _lastTrigger = RunAssistant;
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
        _lastTrigger = UndoAssistant;
        if (_viewModel is { } vm) await RunAsync(vm.UndoAssistantGoalAsync, Strings.AssistantUndoWorking);
    }

    private async void DiagnoseCrash_Click(object? sender, RoutedEventArgs args)
    {
        _lastTrigger = DiagnoseCrash;
        if (_viewModel is { } vm)
            await RunAsync(vm.DiagnoseAssistantCrashAsync, Strings.AssistantDiagnosingCrash, Strings.AssistantUnavailable);
    }

    private async void DiagnoseSource_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is not { } vm || _request != null || TopLevel.GetTopLevel(this) is not Window window) return;
        var connection = ReferenceEquals(sender, DiagnoseConnection);
        _lastTrigger = connection ? DiagnoseConnection : DiagnoseSource;
        _diagnosingSource = true;
        _diagnosingConnection = connection;
        _sourceSentToProvider = false;
        window.AddHandler(InputElement.KeyDownEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, CancelFromWindow, RoutingStrategies.Tunnel);
        try
        {
            var workingText = connection ? Strings.SourceConnectionWorking : Strings.SourceDiagnosisWorking;
            await RunAsync(ct => vm.DiagnoseAssistantSourceAsync(connection, ConfirmChangeAsync, ct), workingText, Strings.AssistantUnavailable);
        }
        finally
        {
            window.RemoveHandler(InputElement.KeyDownEvent, CancelFromWindow);
            window.RemoveHandler(InputElement.PointerPressedEvent, CancelFromWindow);
            _diagnosingSource = false;
            _diagnosingConnection = false;
            _sourceSentToProvider = false;
            RefreshLabels();
        }
    }

    private async void RepairVpn_Click(object? sender, RoutedEventArgs args)
    {
        _lastTrigger = RepairVpn;
        if (_viewModel is { } vm)
            await RunAsync(vm.RepairAssistantVpnAsync, Strings.AssistantRepairWorking, Strings.AssistantRepairFailed);
    }

    private async Task<bool> ConfirmChangeAsync(string description, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmation = choice;
        AssistantProgress.IsVisible = false;
        AssistantStatus.Text = description;
        CancelAssistant.Content = _diagnosingSource ? Strings.SourceDiagnosisDoNotSend : Strings.AssistantDecline;
        ConfirmAssistant.Content = _diagnosingSource
            ? (_diagnosingConnection ? Strings.SourceConnectionSend : Strings.SourceDiagnosisSend)
            : Strings.AssistantConfirm;
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
            if (_diagnosingSource) _sourceSentToProvider = true;
            ConfirmAssistant.IsVisible = false;
            AssistantProgress.IsVisible = true;
            AssistantStatus.Text = _diagnosingSource
                ? (_diagnosingConnection ? Strings.SourceConnectionWorking : Strings.SourceDiagnosisWorking)
                : Strings.AssistantWorking;
        }
    }

    private void CancelFromWindow(object? sender, RoutedEventArgs args)
    {
        if (args is KeyEventArgs keyArgs)
        {
            if (keyArgs.Key == Key.Escape)
            {
                keyArgs.Handled = true;
                DeclineOrCancel();
            }
            return;
        }

        // Only decline an open confirmation on outside pointer press, never an in-flight request,
        // and never while dragging/clicking a scrollbar on narrow windows.
        if (_confirmation == null) return;
        if (args.Source is Avalonia.Visual origin)
        {
            if (ReferenceEquals(origin, this) || origin.GetVisualAncestors().Contains(this)) return;
            if (origin is ScrollBar || origin.GetVisualAncestors().OfType<ScrollBar>().Any()) return;
        }
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
        UpdateComposer();
        DiagnoseConnection.IsEnabled = DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled
            = UndoAssistant.IsEnabled = AssistantGoal.IsEnabled = false;
        CancelAssistant.IsVisible = true;
        AssistantProgress.IsVisible = true;
        AssistantStatus.Text = progress ?? Strings.AssistantWorking;
        try
        {
            AssistantStatus.Text = await action(request.Token);
        }
        catch (OperationCanceledException)
        {
            AssistantStatus.Text = _sourceSentToProvider
                ? Strings.SourceDiagnosisCancelledAfterSend
                : Strings.AssistantCancelled;
        }
        catch (Exception)
        {
            AssistantStatus.Text = error ?? Strings.AssistantApplyFailed;
        }
        finally
        {
            _request = null;
            AssistantProgress.IsVisible = false;
            CancelAssistant.IsVisible = false;
            DiagnoseConnection.IsEnabled = DiagnoseSource.IsEnabled = RepairVpn.IsEnabled = DiagnoseCrash.IsEnabled
                = UndoAssistant.IsEnabled = AssistantGoal.IsEnabled = true;
            RefreshLabels();
            if (_lastTrigger is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } trigger)
                trigger.Focus();
        }
    }
}
