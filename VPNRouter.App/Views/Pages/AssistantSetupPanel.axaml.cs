using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class AssistantSetupPanel : UserControl
{
    private MainWindowViewModel? _viewModel;
    private readonly List<SubscriptionViewModel> _subscriptions = [];
    private CancellationTokenSource? _request;

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
        if (_viewModel != null && vm != null) _request?.Cancel();
        if (_viewModel != null)
        {
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
        if (args.PropertyName == nameof(MainWindowViewModel.IsRussian)) RefreshLabels();
    }

    private void SubscriptionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
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
            RefreshLabels();
    }

    private void RefreshLabels()
    {
        var canPlan = _viewModel?.HasAssistantSubscription == true;
        var canUndo = _viewModel?.HasAssistantUndo == true;
        IsVisible = _request != null || canPlan || canUndo;
        OpenAssistant.Content = Strings.AssistantTitle;
        AssistantGoal.Watermark = Strings.AssistantGoalHint;
        AutomationProperties.SetName(AssistantGoal, Strings.AssistantTitle);
        AssistantNotice.Text = Strings.AssistantDataNotice;
        RunAssistant.Content = Strings.AssistantApply;
        CancelAssistant.Content = Strings.AssistantCancel;
        UndoAssistant.Content = Strings.AssistantUndo;
        AssistantGoal.IsVisible = canPlan;
        AssistantNotice.IsVisible = canPlan;
        RunAssistant.IsVisible = canPlan;
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
        if (_viewModel is not { } vm || string.IsNullOrWhiteSpace(goal)) return;
        await RunAsync(ct => vm.RunAssistantGoalAsync(goal, ct));
    }

    private async void UndoAssistant_Click(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is { } vm) await RunAsync(vm.UndoAssistantGoalAsync);
    }

    private void CancelAssistant_Click(object? sender, RoutedEventArgs args) => _request?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task<string>> action)
    {
        if (_request != null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        RunAssistant.IsEnabled = UndoAssistant.IsEnabled = OpenAssistant.IsEnabled = AssistantGoal.IsEnabled = false;
        CancelAssistant.IsVisible = true;
        AssistantStatus.Text = Strings.AssistantWorking;
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
            AssistantStatus.Text = Strings.AssistantApplyFailed;
        }
        finally
        {
            _request = null;
            CancelAssistant.IsVisible = false;
            RunAssistant.IsEnabled = UndoAssistant.IsEnabled = OpenAssistant.IsEnabled = AssistantGoal.IsEnabled = true;
            RefreshLabels();
        }
    }
}
