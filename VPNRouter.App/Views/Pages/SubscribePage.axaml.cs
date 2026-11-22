using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class SubscribePage : UserControl
{
    private readonly ComboBox _categoryFilter;
    private readonly ServerPickerListBox _serverList;
    private MainWindowViewModel? _subscribedVm;
    private ProtocolGroup? _category;
    private bool _rebuildingCategories;
    private bool _refreshQueued;

    public SubscribePage()
    {
        InitializeComponent();
        _categoryFilter = this.FindControl<ComboBox>("SubscriptionCategory")!;
        _serverList = this.FindControl<ServerPickerListBox>("SubList")!;
        _categoryFilter.SelectionChanged += OnCategoryChanged;
        _serverList.ConfirmSelection = ConfirmServer;
        AttachedToVisualTree += (_, _) => Bind(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) Bind(DataContext as MainWindowViewModel);
        };
    }

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribedVm, viewModel)) return;
        if (_subscribedVm is not null)
        {
            _subscribedVm.ActiveServerChanged -= OnActiveServerChanged;
            _subscribedVm.PropertyChanged -= OnSourceChanged;
            _subscribedVm.SubscriptionServers.CollectionChanged -= OnServersChanged;
        }
        _subscribedVm = viewModel;
        _category = null;
        if (_subscribedVm is not null)
        {
            _subscribedVm.ActiveServerChanged += OnActiveServerChanged;
            _subscribedVm.PropertyChanged += OnSourceChanged;
            _subscribedVm.SubscriptionServers.CollectionChanged += OnServersChanged;
        }
        RebuildCategories();
    }

    private void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueCategoryRefresh();

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedSubscriptionServer))
            SyncSelection();
        else if (e.PropertyName is null or "" or nameof(MainWindowViewModel.IsRussian))
            QueueCategoryRefresh();
    }

    private void QueueCategoryRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RebuildCategories();
        });
    }

    private void RebuildCategories()
    {
        var options = new List<SubscriptionCategoryOption> { new(null, Strings.ServerAllCategories, string.Empty) };
        if (_subscribedVm is { } vm)
        {
            options.AddRange(vm.SubscriptionServers.GroupBy(row => row.ProtocolGroup).OrderBy(group => group.Key)
                .Select(group => new SubscriptionCategoryOption(group.Key, MainWindowViewModel.SimpleCategoryName(group.Key),
                    SimpleServerGroup.DescribeProtocols(group.Key, group))));
        }

        AutomationProperties.SetName(_categoryFilter, Strings.ServerCategoryFilter);
        var selected = options.Find(option => option.Category == _category) ?? options[0];
        _category = selected.Category;
        _rebuildingCategories = true;
        try
        {
            _categoryFilter.ItemsSource = options;
            _categoryFilter.SelectedItem = selected;
        }
        finally { _rebuildingCategories = false; }
        RefreshRows();
    }

    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingCategories || _categoryFilter.SelectedItem is not SubscriptionCategoryOption option) return;
        _category = option.Category;
        RefreshRows();
    }

    private void RefreshRows()
    {
        _serverList.ItemsSource = _subscribedVm?.SubscriptionServers
            .Where(row => _category == null || row.ProtocolGroup == _category).ToArray();
        SyncSelection();
    }

    private void SyncSelection()
    {
        var selected = _subscribedVm?.SelectedSubscriptionServer;
        _serverList.SelectedItem = selected != null && _serverList.Items.Contains(selected) ? selected : null;
    }

    private void ConfirmServer(ServerViewModel row)
    {
        if (_subscribedVm is not { } vm || !vm.SubscriptionServers.Contains(row)) return;
        vm.SelectServerFromPicker(() =>
        {
            vm.SelectedSubscriptionServer = row;
            return true;
        }, () => row);
    }

    private void OnActiveServerChanged(ServerViewModel? active)
    {
        if (active == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_serverList.Items.Contains(active)) _serverList.ScrollIntoView(active);
        });
    }
}

internal sealed record SubscriptionCategoryOption(ProtocolGroup? Category, string Name, string Protocols)
{
    public bool HasCategory => Category.HasValue;
    public ProtocolGroup IconGroup => Category.GetValueOrDefault();
}
