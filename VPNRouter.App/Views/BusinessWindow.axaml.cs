using Avalonia.Controls;
using Avalonia.Threading;
using System.ComponentModel;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Services;
using VPNRouter.App.Services;
using VPNRouter.Core;

namespace VPNRouter.App.Views;

public partial class BusinessWindow : Window
{
    private readonly Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> _probe;
    private BusinessServerHealthMonitor? _health;
    private MainWindowViewModel? _vm;
    private readonly Func<MainWindowViewModel, Action, BusinessServicesViewModel> _serviceFactory;
    private readonly BusinessNodeMetadataController? _metadata;
    private BusinessServicesViewModel? _services;
    private bool _syncingLanguage;
    private bool _syncingSelection;
    public BusinessWindowTextViewModel Texts { get; } = new();

    public BusinessWindow() : this(ProductEdition.IsBusiness
        ? BusinessBootstrap.ReadClientConfiguration() : BusinessClientConfiguration.Empty) { }

    private BusinessWindow(BusinessClientConfiguration configuration) : this(
        static (server, ct) => TcpTlsProbe.ProbeTcpOnlyAsync(server.Server, server.Port, ct),
        (vm, changed) => new(vm, configuration, new BusinessClientHttp(), changed),
        changed => configuration.NodesEndpoint is { } endpoint ? new(endpoint, changed) : null) { }

    internal BusinessWindow(Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> probe,
        Func<MainWindowViewModel, Action, BusinessServicesViewModel>? serviceFactory = null,
        Func<Action, BusinessNodeMetadataController?>? metadataFactory = null)
    {
        _probe = probe;
        _serviceFactory = serviceFactory ?? (static (vm, changed) =>
            new(vm, BusinessClientConfiguration.Empty, new BusinessClientHttp(), changed));
        InitializeComponent();
        _metadata = metadataFactory?.Invoke(ApplyNodeMetadata);
        DataContextChanged += (_, _) => AttachViewModel();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _health?.SetVisible(IsVisible);
                _services?.SetVisible(IsVisible);
                _metadata?.SetVisible(IsVisible && _vm != null);
            }
        };
        Opened += (_, _) => LaunchFailureCounter.MarkStable();
        Closed += (_, _) =>
        {
            DetachViewModel();
            _metadata?.Dispose();
            (DataContext as MainWindowViewModel)?.Dispose();
        };
    }

    private void DetachViewModel()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _metadata?.SetVisible(false);
        _health?.Dispose();
        _services?.Dispose();
        BusinessActions.DataContext = null;
        BusinessSubscriptionActions.DataContext = null;
        _services = null;
        _health = null;
        _vm = null;
        Texts.SetClient(null);
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        _vm = DataContext as MainWindowViewModel;
        Texts.SetClient(_vm);
        RebuildHealth();
        RefreshBusinessLocalization();
        if (_vm != null)
        {
            _services = _serviceFactory(_vm, RebuildHealth);
            BusinessActions.DataContext = _services;
            BusinessSubscriptionActions.DataContext = _services;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _services.SetVisible(IsVisible);
            _metadata?.SetVisible(IsVisible);
        }
    }

    private void RebuildHealth()
    {
        _health?.Dispose();
        _health = _vm == null ? null : new BusinessServerHealthMonitor(_vm, _probe);
        ApplyNodeMetadata();
        _health?.SetVisible(IsVisible);
        _ = _metadata?.RefreshAsync();
    }

    private void ApplyNodeMetadata()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyNodeMetadata);
            return;
        }
        if (_metadata != null && _health != null)
        {
            var snapshot = _metadata.LastSnapshot;
            foreach (var choice in _health.Choices)
            {
                BusinessNodeMetadata? row = null;
                if (snapshot != null && snapshot.TryGetNode(choice.Server.Server, out var matched)) row = matched;
                choice.UpdateMetadata(snapshot, row);
            }
        }
        _syncingSelection = true;
        try
        {
            BusinessServerSelector.ItemsSource = _health == null ? null : BusinessServerChoiceOrdering.Sort(_health.Choices);
            SyncSelection();
        }
        finally { _syncingSelection = false; }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.StatusText) or nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.IsConnected))
        {
            if (Dispatcher.UIThread.CheckAccess()) Texts.SetLanguageSaveFailed(false);
            else Dispatcher.UIThread.Post(() => Texts.SetLanguageSaveFailed(false));
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.IsRussian))
        {
            // IsRussian is raised before the shared language has been updated.
            Dispatcher.UIThread.Post(RefreshBusinessLocalization);
        }
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedServer)) return;
        if (Dispatcher.UIThread.CheckAccess()) SyncSelection();
        else Dispatcher.UIThread.Post(SyncSelection);
    }

    private void RefreshBusinessLocalization()
    {
        Texts.Refresh();
        _services?.RefreshLocalization();
        if (_health != null) foreach (var choice in _health.Choices) choice.RefreshLocalization();
        _syncingLanguage = true;
        try { BusinessLanguageSelector.SetCurrentValue(ComboBox.SelectedIndexProperty, _vm?.IsRussian == true ? 0 : 1); }
        finally { _syncingLanguage = false; }
    }

    private void OnBusinessLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingLanguage || _vm == null || BusinessLanguageSelector.SelectedItem is not ComboBoxItem item) return;
        Texts.SetLanguageSaveFailed(false);
        try
        {
            if (item.Tag as string == "ru") _vm.SetLanguageRussianCommand.Execute(null);
            else if (item.Tag as string == "en") _vm.SetLanguageEnglishCommand.Execute(null);
        }
        catch { Texts.SetLanguageSaveFailed(true); }
        RefreshBusinessLocalization();
    }

    private void SyncSelection()
    {
        var wasSyncing = _syncingSelection;
        _syncingSelection = true;
        try
        {
            BusinessServerSelector.SetCurrentValue(ComboBox.SelectedItemProperty,
                _health?.Choices.FirstOrDefault(c => ReferenceEquals(c.Server, _vm?.SelectedServer)));
        }
        finally { _syncingSelection = wasSyncing; }
    }

    private void OnBusinessServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && DataContext is MainWindowViewModel vm && !vm.IsConnecting && !vm.IsConnected &&
            e.AddedItems.Count == 1 && e.AddedItems[0] is BusinessServerChoiceViewModel choice &&
            !ReferenceEquals(choice.Server, vm.SelectedServer))
        {
            vm.SelectBusinessServer(choice.Server);
            SyncSelection();
        }
    }

    private void OnBusinessServerRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = RefreshBusinessServersAsync();

    internal Task RefreshBusinessServersAsync() => Task.WhenAll(
        _health?.RefreshAsync() ?? Task.CompletedTask,
        _metadata?.RefreshAsync() ?? Task.CompletedTask);
}
