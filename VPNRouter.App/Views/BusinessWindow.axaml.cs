using Avalonia.Controls;
using Avalonia.Threading;
using System.ComponentModel;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class BusinessWindow : Window
{
    private readonly Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> _probe;
    private BusinessServerHealthMonitor? _health;
    private MainWindowViewModel? _vm;

    public BusinessWindow() : this(static (server, ct) =>
        TcpTlsProbe.ProbeTcpOnlyAsync(server.Server, server.Port, ct)) { }

    internal BusinessWindow(Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> probe)
    {
        _probe = probe;
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) _health?.SetVisible(IsVisible);
        };
        Opened += (_, _) => LaunchFailureCounter.MarkStable();
        Closed += (_, _) =>
        {
            DetachViewModel();
            (DataContext as MainWindowViewModel)?.Dispose();
        };
    }

    private void DetachViewModel()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _health?.Dispose();
        _health = null;
        _vm = null;
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        _vm = DataContext as MainWindowViewModel;
        _health = _vm == null ? null : new BusinessServerHealthMonitor(_vm, _probe);
        BusinessServerSelector.ItemsSource = _health?.Choices;
        SyncSelection();
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        _health?.SetVisible(IsVisible);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedServer)) return;
        if (Dispatcher.UIThread.CheckAccess()) SyncSelection();
        else Dispatcher.UIThread.Post(SyncSelection);
    }

    private void SyncSelection()
    {
        BusinessServerSelector.SetCurrentValue(ComboBox.SelectedItemProperty,
            _health?.Choices.FirstOrDefault(c => ReferenceEquals(c.Server, _vm?.SelectedServer)));
    }

    private void OnBusinessServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            e.AddedItems.Count == 1 && e.AddedItems[0] is BusinessServerChoiceViewModel choice)
        {
            vm.SelectBusinessServer(choice.Server);
            SyncSelection();
        }
    }

    private void OnBusinessServerRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = RefreshBusinessServersAsync();

    internal Task RefreshBusinessServersAsync() => _health?.RefreshAsync() ?? Task.CompletedTask;
}
