using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private MainWindowViewModel? _viewModel;
    private bool _refreshQueued;

    public SimplePage()
    {
        InitializeComponent();
        _picker = this.FindControl<SubscriptionServerPicker>("SimplePicker")!;
        _checksPanel = this.FindControl<StackPanel>("SimpleChecks")!;
        _changeServer = this.FindControl<ToggleButton>("SimpleChangeServer")!;
        _appRules = this.FindControl<Button>("SimpleAppRules")!;
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
        AttachedToVisualTree += (_, _) => Bind(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) Bind(DataContext as MainWindowViewModel);
        };
        // The connecting arc of the status emblem spins only when the system allows animations; with reduced motion
        // it stays a still accent arc and the state word carries the meaning.
        Classes.Set("calm", !UiMotion.Allowed());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnSourceChanged;
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnSourceChanged;
        Refresh();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpPickerVisible) or
            nameof(MainWindowViewModel.SmpHasRefreshableSubscriptions) or nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
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
        _checksPanel.IsVisible = _viewModel is { SmpHasConfig: true } vm &&
            (vm.SmpHasSubscriptionServers || vm.SmpHasRefreshableSubscriptions);
        var checkButton = this.FindControl<ToggleButton>("SimpleCheckServers")!;
        this.FindControl<TextBlock>("SimpleCheckServersLabel")!.Text = Strings.SmpCheckServers;
        AutomationProperties.SetName(checkButton, Strings.SmpCheckServers);
        _appRules.Content = Strings.SmpApplicationRules;
    }

    private void ToggleServerChecks(object? sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { IsChecked: true } || _viewModel is not { SmpHasSubscriptionServers: true } vm ||
            vm.IsTestingServers || vm.IsDeepTestingServers || vm.SmpSubscriptionsRefreshing) return;
        if (vm.TestAllSubscriptionServersCommand.CanExecute(null)) vm.TestAllSubscriptionServersCommand.Execute(null);
    }

    private void OpenAppRules(object? sender, RoutedEventArgs args) => _viewModel?.OpenSimpleApplicationRules();
}
