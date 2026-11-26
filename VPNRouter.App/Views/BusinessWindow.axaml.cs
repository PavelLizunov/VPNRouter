using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>>? _probe;
    private BusinessServerHealthMonitor? _health;
    private MainWindowViewModel? _vm;
    private readonly Func<MainWindowViewModel, Action, BusinessServicesViewModel> _serviceFactory;
    private readonly BusinessNodeMetadataController? _metadata;
    private BusinessServicesViewModel? _services;
    private bool _syncingLanguage;
    private bool _syncingSelection;
    private bool _repairWasOpen;
    private Button? _repairLauncher;
    public BusinessWindowTextViewModel Texts { get; } = new();

    public BusinessWindow() : this(ProductEdition.IsBusiness
        ? BusinessBootstrap.ReadClientConfiguration() : BusinessClientConfiguration.Empty) { }

    private BusinessWindow(BusinessClientConfiguration configuration) : this(
        probe: null,
        (vm, changed) => new(vm, configuration, new BusinessClientHttp(), changed),
        changed => configuration.NodesEndpoint is { } endpoint ? new(endpoint, changed) : null) { }

    internal BusinessWindow(Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>>? probe,
        Func<MainWindowViewModel, Action, BusinessServicesViewModel>? serviceFactory = null,
        Func<Action, BusinessNodeMetadataController?>? metadataFactory = null)
    {
        _probe = probe;
        _serviceFactory = serviceFactory ?? (static (vm, changed) =>
            new(vm, BusinessClientConfiguration.Empty, new BusinessClientHttp(), changed));
        InitializeComponent();
        _metadata = metadataFactory?.Invoke(ApplyNodeMetadata);
        DataContextChanged += (_, _) => AttachViewModel();
        ActualThemeVariantChanged += (_, _) => SyncThemeVariant();
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
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.UpdateVm.PropertyChanged -= OnUpdateVmPropertyChanged;
        }
        _metadata?.SetVisible(false);
        DetachHealth();
        if (_services != null)
        {
            _services.PropertyChanged -= OnServicesPropertyChanged;
            _services.Dispose();
        }
        _repairWasOpen = false;
        _repairLauncher = null;
        BusinessActions.DataContext = null;
        BusinessSubscriptionActions.DataContext = null;
        BusinessRepairActions.DataContext = null;
        BusinessServerPanel.IsVisible = false;
        BusinessModePanel.IsVisible = false;
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
            BusinessRepairActions.DataContext = _services;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.UpdateVm.PropertyChanged += OnUpdateVmPropertyChanged;
            _services.PropertyChanged += OnServicesPropertyChanged;
            _services.SetVisible(IsVisible);
            _metadata?.SetVisible(IsVisible);
            SyncThemeVariant();
        }
    }

    private void SyncThemeVariant()
    {
        if (_vm == null) return;
        _vm.IsDarkTheme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    }

    private void OnUpdateVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateNotificationViewModel.IsVersionHistoryVisible) &&
            _vm?.UpdateVm.IsVersionHistoryVisible == true)
        {
            Dispatcher.UIThread.Post(() => BusinessVersionHistoryPanel.BringIntoView(), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(UpdateNotificationViewModel.IsRollbackConfirmationVisible) &&
                 _vm?.UpdateVm.IsRollbackConfirmationVisible == true)
        {
            Dispatcher.UIThread.Post(() => BusinessRollbackConfirmPanel.BringIntoView(), DispatcherPriority.Background);
        }
    }

    private void RebuildHealth()
    {
        DetachHealth();
        _health = _vm == null ? null : new BusinessServerHealthMonitor(_vm, _probe);
        if (_health != null) _health.Updated += OnServerHealthChanged;
        OnServerHealthChanged();
        ApplyNodeMetadata();
        _health?.SetVisible(IsVisible);
        _ = _metadata?.RefreshAsync();
    }

    private void DetachHealth()
    {
        if (_health == null) return;
        _health.Updated -= OnServerHealthChanged;
        _health.Dispose();
        _health = null;
    }

    private void OnServerHealthChanged()
    {
        Texts.UpdateServerCheck(
            hasTargets: _health?.HasProbeTargets == true,
            isChecking: _health?.IsChecking == true);
        Texts.RefreshServerSummary();
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
                choice.UpdateMetadata(snapshot);
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
        if (e.PropertyName is nameof(MainWindowViewModel.StatusText) or nameof(MainWindowViewModel.IsConnecting)
            or nameof(MainWindowViewModel.IsConnected) or nameof(MainWindowViewModel.IsApplying))
        {
            if (Dispatcher.UIThread.CheckAccess()) Texts.SetLanguageSaveFailed(false);
            else Dispatcher.UIThread.Post(() => Texts.SetLanguageSaveFailed(false));
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainWindowViewModel.IsRussian))
        {
            // IsRussian is raised before the shared language has been updated.
            Dispatcher.UIThread.Post(RefreshBusinessLocalization);
        }
        if (e.PropertyName is nameof(MainWindowViewModel.IsBusinessRepairInProgress)
            or nameof(MainWindowViewModel.BusinessActiveServerName)
            or nameof(MainWindowViewModel.IsBusinessServiceManaged)
            or nameof(MainWindowViewModel.BusinessActiveRoutingMode))
        {
            if (Dispatcher.UIThread.CheckAccess()) Texts.Refresh();
            else Dispatcher.UIThread.Post(Texts.Refresh);
        }
        if (e.PropertyName == nameof(MainWindowViewModel.IsSplitTunnel))
        {
            if (Dispatcher.UIThread.CheckAccess()) Texts.RefreshModeSummary();
            else Dispatcher.UIThread.Post(Texts.RefreshModeSummary);
        }
        if (e.PropertyName is nameof(MainWindowViewModel.BusinessAutoSelect)
            or nameof(MainWindowViewModel.BusinessAutoSelectStatusText)
            or nameof(MainWindowViewModel.HasBusinessAutoSelectStatus))
        {
            if (Dispatcher.UIThread.CheckAccess()) Texts.RefreshAutoSelect();
            else Dispatcher.UIThread.Post(Texts.RefreshAutoSelect);
        }
        if (_syncingSelection || e.PropertyName is not (nameof(MainWindowViewModel.SelectedServer)
            or nameof(MainWindowViewModel.BusinessAutoSelect))) return;
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
            var saved = _health?.Choices.FirstOrDefault(c => ReferenceEquals(c.Server, _vm?.SelectedServer));
            // In Auto, the saved fallback is only a label. Selecting even that
            // same row is an explicit request to switch to manual selection.
            var selected = _vm?.BusinessAutoSelect == true ? null : saved;
            BusinessServerSelector.SetCurrentValue(ComboBox.SelectedItemProperty, selected);
            Texts.SetSavedServer(saved);
        }
        finally { _syncingSelection = wasSyncing; }
    }

    private void OnBusinessServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && DataContext is MainWindowViewModel vm && !vm.IsConnecting && !vm.IsConnected &&
            !vm.IsBusinessRepairInProgress && e.AddedItems.Count == 1 && e.AddedItems[0] is BusinessServerChoiceViewModel choice &&
            (vm.BusinessAutoSelect || !ReferenceEquals(choice.Server, vm.SelectedServer)))
        {
            _syncingSelection = true;
            try { vm.SelectBusinessServer(choice.Server); }
            finally { _syncingSelection = false; }
            SyncSelection();
        }
    }

    private void OnBusinessServerMenuToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BusinessServerPanel.IsVisible = !BusinessServerPanel.IsVisible;
        if (BusinessServerPanel.IsVisible) Texts.RefreshServerSummary();
        BusinessModePanel.IsVisible = false;
    }

    private void OnBusinessModeMenuToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BusinessModePanel.IsVisible = !BusinessModePanel.IsVisible;
        BusinessServerPanel.IsVisible = false;
    }

    private void OnBusinessRepairClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _repairLauncher = BusinessRepairButton;

    private void OnBusinessRepairActionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => FocusRepairDismiss();

    private Control ActiveRepairDismissButton() =>
        BusinessRepairDismiss.IsVisible ? BusinessRepairDismiss : BusinessRepairCloseButton;

    private bool FocusRepairDismiss() => ActiveRepairDismissButton().Focus();

    private void OnBusinessDiagnosticMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _repairLauncher = BusinessMenuButton;
        BusinessMenuButton.Flyout?.Hide();
    }

    private void OnServicesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BusinessServicesViewModel.IsRepairOpen) ||
            sender is not BusinessServicesViewModel services || _repairWasOpen == services.IsRepairOpen) return;
        var open = _repairWasOpen = services.IsRepairOpen;
        var launcher = _repairLauncher ?? BusinessRepairButton;
        if (!open) _repairLauncher = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !ReferenceEquals(_services, services) || services.IsRepairOpen != open) return;
            var target = open ? ActiveRepairDismissButton() : launcher;
            if (!target.Focus() && !BusinessConnectButton.Focus()) BusinessMenuButton.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnBusinessKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (BusinessMenuButton.Flyout is { IsOpen: true } menu)
        {
            menu.Hide();
            e.Handled = true;
            return;
        }
        if (_services?.IsRepairOpen != true) return;
        _services.CloseRepairCommand.Execute(null);
        e.Handled = true;
    }

    private void OnBusinessServerRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = RefreshBusinessServersAsync();

    internal Task RefreshBusinessServersAsync() => Task.WhenAll(
        _health?.RefreshAsync() ?? Task.CompletedTask,
        _metadata?.RefreshAsync() ?? Task.CompletedTask);
}
