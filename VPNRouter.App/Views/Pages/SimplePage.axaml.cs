using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class SimplePage : UserControl
{
    private readonly SubscriptionServerPicker _picker;
    private readonly StackPanel _checksPanel;
    private readonly ToggleButton _changeServer;
    private readonly Button _appRules;
    private readonly TextBlock _refreshFeedback;
    private readonly TextBlock _refreshBlockedReason;
    private readonly Button _applyPending;
    private MainWindowViewModel? _viewModel;
    private bool _refreshQueued;

    public SimplePage()
    {
        InitializeComponent();
        _picker = this.FindControl<SubscriptionServerPicker>("SimplePicker")!;
        _checksPanel = this.FindControl<StackPanel>("SimpleChecks")!;
        _changeServer = this.FindControl<ToggleButton>("SimpleChangeServer")!;
        _appRules = this.FindControl<Button>("SimpleAppRules")!;
        _refreshFeedback = this.FindControl<TextBlock>("SimpleRefreshFeedback")!;
        _refreshBlockedReason = this.FindControl<TextBlock>("SimpleRefreshBlockedReason")!;
        _applyPending = this.FindControl<Button>("SimpleApplyPending")!;
        _picker.LinkTransferred += this.FindControl<SubscriptionLinkEditor>("SimpleLink")!.SetDraft;
        _picker.CloseRequested += () =>
        {
            if (_viewModel is { } vm) vm.SmpFormExpanded = false;
            _changeServer.Focus();
        };
        _picker.ChoiceConfirmed += () =>
        {
            if (_viewModel?.SmpPickerVisible == false) _changeServer.Focus();
        };
        AttachedToVisualTree += (_, _) =>
        {
            Bind(DataContext as MainWindowViewModel);
            SyncThemeLogo();
        };
        ActualThemeVariantChanged += (_, _) => SyncThemeLogo();
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree())
            {
                Bind(DataContext as MainWindowViewModel);
                SyncThemeLogo();
            }
        };
        // The connecting arc of the status emblem spins only when the system allows animations; with reduced motion
        // it stays a still accent arc and the state word carries the meaning.
        Classes.Set("calm", !UiMotion.Allowed());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SyncThemeLogo()
    {
        if (_viewModel is { } vm)
            vm.IsDarkTheme = ActualThemeVariant == ThemeVariant.Dark;
    }

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnSourceChanged;
            _viewModel.SubscriptionRefreshFeedbackChanged -= OnRefreshFeedbackChanged;
        }
        _viewModel = viewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnSourceChanged;
            _viewModel.SubscriptionRefreshFeedbackChanged += OnRefreshFeedbackChanged;
        }
        Refresh();
    }

    private void OnRefreshFeedbackChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpPickerVisible) or
            nameof(MainWindowViewModel.SmpHasSubscriptionServers) or
            nameof(MainWindowViewModel.SmpHasRefreshableSubscriptions) or
            nameof(MainWindowViewModel.SmpCanRefreshSubscriptions) or
            nameof(MainWindowViewModel.SmpSubscriptionsRefreshing) or
            nameof(MainWindowViewModel.HasPendingAppChanges) or
            nameof(MainWindowViewModel.CanApplyAppChanges) or
            nameof(MainWindowViewModel.IsConnected) or
            nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
            return;

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        _changeServer.IsEnabled = _viewModel is { SmpHasSubscriptionServers: true, CanPickSimpleSubscriptionServer: true };
        var open = _changeServer.IsEnabled && _viewModel?.SmpPickerVisible == true;
        if (!open && _picker.IsVisible) _picker.Close();
        _picker.IsVisible = open;
        _checksPanel.IsVisible = _viewModel is { SmpHasConfig: true, SmpHasSelectableSubscriptionServers: true };
        var checkButton = this.FindControl<ToggleButton>("SimpleCheckServers")!;
        this.FindControl<TextBlock>("SimpleCheckServersLabel")!.Text = Strings.SmpCheckServers;
        AutomationProperties.SetName(checkButton, Strings.SmpCheckServers);
        _appRules.Content = Strings.SmpApplicationRules;

        var feedback = _viewModel is { SmpSubscriptionsRefreshing: false } vm ? vm.SmpRefreshResultText : string.Empty;
        _refreshFeedback.Text = feedback;
        _refreshFeedback.IsVisible = feedback.Length > 0;
        if (feedback.Length > 0)
        {
            var key = _viewModel?.SmpRefreshResultIsError == true ? "WarningFgBrush" : "SuccessFgBrush";
            if (this.TryFindResource(key, ActualThemeVariant, out var res) && res is IBrush brush)
                _refreshFeedback.Foreground = brush;
        }

        var blocked = _viewModel?.SmpRefreshBlockedReason ?? string.Empty;
        _refreshBlockedReason.Text = blocked;
        _refreshBlockedReason.IsVisible = blocked.Length > 0;

        _applyPending.Content = Strings.SmpApplyServiceNow;
        AutomationProperties.SetName(_applyPending, Strings.SmpApplyServiceNow);
        _applyPending.IsVisible = _viewModel?.CanApplySimplePendingChanges == true;
    }

    // Expanding "Test servers" does not auto-start a network probe so users can inspect or refresh first.
    private void ToggleServerChecks(object? sender, RoutedEventArgs args) { }

    private void OpenAppRules(object? sender, RoutedEventArgs args) => _viewModel?.OpenSimpleApplicationRules();
}
