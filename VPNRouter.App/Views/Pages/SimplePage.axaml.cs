using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private readonly ToggleButton _changeServer;
    private readonly Button _appRules;
    private readonly Button _applyPending;
    private MainWindowViewModel? _viewModel;
    private bool _refreshQueued;

    public SimplePage()
    {
        InitializeComponent();
        _picker = this.FindControl<SubscriptionServerPicker>("SimplePicker")!;
        _changeServer = this.FindControl<ToggleButton>("SimpleChangeServer")!;
        _appRules = this.FindControl<Button>("SimpleAppRules")!;
        _applyPending = this.FindControl<Button>("SimpleApplyPending")!;
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
        // With reduced motion the connection ring stays still and the status text carries the meaning.
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
            _viewModel.PropertyChanged -= OnSourceChanged;
        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnSourceChanged;
        Refresh();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpPickerVisible) or
            nameof(MainWindowViewModel.SmpHasSubscriptionServers) or
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
        _appRules.Content = Strings.SmpApplicationRules;

        _applyPending.Content = Strings.SmpApplyServiceNow;
        AutomationProperties.SetName(_applyPending, Strings.SmpApplyServiceNow);
        _applyPending.IsVisible = _viewModel?.CanApplySimplePendingChanges == true;
    }

    private void OnHeroRingClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs args)
    {
        if (_viewModel is not { } vm || vm.SimpleCtaIsConnecting) return;
        if (!vm.IsConnected && !vm.SmpCanConnect) return;
        if (vm.SmpToggleConnectCommand.CanExecute(null))
            vm.SmpToggleConnectCommand.Execute(null);
    }

    private void OpenAppRules(object? sender, RoutedEventArgs args) => _viewModel?.OpenSimpleApplicationRules();
}
