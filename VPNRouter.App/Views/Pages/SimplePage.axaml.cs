using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class SimplePage : UserControl
{
    private readonly StackPanel _serverPanel;
    private readonly TextBox _serverSearch;
    private readonly ServerPickerListBox _serverResults;
    private readonly TextBlock _serverCount;
    private readonly ToggleButton _connectionOptions;
    private readonly Button _changeServer;
    private readonly Button _appRules;
    private MainWindowViewModel? _viewModel;
    private bool _pickerOpen;
    private bool _refreshQueued;

    public SimplePage()
    {
        InitializeComponent();
        _serverPanel = this.FindControl<StackPanel>("SimpleServerPanel")!;
        _serverSearch = this.FindControl<TextBox>("SimpleServerSearch")!;
        _serverResults = this.FindControl<ServerPickerListBox>("SimpleServerResults")!;
        _serverCount = this.FindControl<TextBlock>("SimpleServerCount")!;
        _connectionOptions = this.FindControl<ToggleButton>("SimpleConnectionOptions")!;
        _changeServer = this.FindControl<Button>("SimpleChangeServer")!;
        _appRules = this.FindControl<Button>("SimpleAppRules")!;
        _serverResults.ConfirmSelection = ConfirmServer;
        _serverSearch.TextChanged += (_, _) => RefreshServerPicker();
        _serverSearch.KeyDown += OnPickerKeyDown;
        _serverResults.KeyDown += OnPickerKeyDown;
        AttachedToVisualTree += (_, _) => BindPicker(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => BindPicker(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) BindPicker(DataContext as MainWindowViewModel);
        };
        // The connecting arc of the status emblem spins only when the system allows animations; with reduced motion
        // it stays a still accent arc and the state word carries the meaning.
        Classes.Set("calm", PrefersReducedMotion());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void BindPicker(MainWindowViewModel? viewModel)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnPickerSourceChanged;
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnPickerSourceChanged;
        RefreshPickerLabels();
        RefreshServerPicker();
    }

    private void OnPickerSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpPickerVisible) or
            nameof(MainWindowViewModel.SmpConfigServer) or nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
            return;

        // Subscription refresh publishes a series of collection changes. Filter the finished snapshot once.
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshPickerLabels();
            RefreshServerPicker();
        });
    }

    private void RefreshPickerLabels()
    {
        _serverSearch.PlaceholderText = Strings.SmpSearchServers;
        Avalonia.Automation.AutomationProperties.SetName(_serverSearch, Strings.SmpSearchServers);
        _connectionOptions.Content = Strings.SmpConnectionOptions;
        _appRules.Content = Strings.SmpApplicationRules;
    }

    private void RefreshServerPicker()
    {
        var source = _viewModel;
        var wasOpen = _pickerOpen;
        _pickerOpen = source is { SmpPickerVisible: true, SmpHasSubscriptionServers: true, CanPickSimpleSubscriptionServer: true };
        _serverPanel.IsVisible = _pickerOpen;
        if (!_pickerOpen)
        {
            if (source?.SmpPickerVisible != true) _connectionOptions.IsChecked = false;
            if (wasOpen)
            {
                _serverSearch.Text = string.Empty;
                _serverResults.ItemsSource = null;
            }
            return;
        }

        var vm = source!;
        var current = vm.IsConnected ? vm.SmpConfigServer : vm.SelectedSubscriptionServer;
        var query = _serverSearch.Text?.Trim() ?? string.Empty;
        var matches = vm.SubscriptionServers
            .Where(row => row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Server.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => ReferenceEquals(row, current))
            .ToArray();
        _serverResults.ItemsSource = matches;
        _serverResults.SelectedItem = current != null && matches.Contains(current) ? current : null;
        _serverCount.Text = matches.Length == 0 ? Strings.SmpNoMatchingServers : Strings.SmpKindServers(matches.Length);
        if (!wasOpen && _serverSearch.IsEffectivelyVisible) _serverSearch.Focus();
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _viewModel is not { SmpPickerVisible: true } vm) return;
        vm.SmpFormExpanded = false;
        _changeServer.Focus();
        args.Handled = true;
    }

    private void ConfirmServer(ServerViewModel row)
    {
        _viewModel?.ConfirmSimpleServer(row);
        if (_viewModel?.SmpPickerVisible == false) _changeServer.Focus();
    }

    private void OpenAppRules(object? sender, RoutedEventArgs args) => _viewModel?.OpenSimpleApplicationRules();

    // Windows: "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION). Other systems expose no setting Avalonia can
    // read, so the animation stays on there.
    private static bool PrefersReducedMotion()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
        }
        catch
        {
            return false;
        }
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
}
