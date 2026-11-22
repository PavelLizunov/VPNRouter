using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Services;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class SubscriptionServerPicker : UserControl
{
    private readonly ToggleButton _searchButton;
    private readonly TextBox _serverSearch;
    private readonly StackPanel _searchLinkHint;
    private readonly ServerPickerListBox _serverResults;
    private readonly ItemsControl _serverGroups;
    private ProtocolGroup? _expandedServerGroup;
    private ServerViewModel[] _sourceRows = [];
    private string? _language;
    private readonly TextBlock _serverCount;
    private MainWindowViewModel? _viewModel;
    private bool _refreshQueued;

    internal event Action<string>? LinkTransferred;
    internal event Action? CloseRequested;
    internal event Action? ChoiceConfirmed;

    public SubscriptionServerPicker()
    {
        InitializeComponent();
        _searchButton = this.FindControl<ToggleButton>("SimpleServerSearchButton")!;
        _serverSearch = this.FindControl<TextBox>("SimpleServerSearch")!;
        _searchLinkHint = this.FindControl<StackPanel>("SimpleSearchLinkHint")!;
        _serverResults = this.FindControl<ServerPickerListBox>("SimpleServerResults")!;
        _serverGroups = this.FindControl<ItemsControl>("SimpleServerGroups")!;
        _serverCount = this.FindControl<TextBlock>("SimpleServerCount")!;
        _serverResults.ConfirmSelection = ConfirmServer;
        _serverSearch.TextChanged += (_, _) => RefreshServerPicker();
        this.FindControl<StackPanel>("SimpleServerPanel")!.KeyDown += OnPickerKeyDown;
        AttachedToVisualTree += (_, _) => Bind(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) Bind(DataContext as MainWindowViewModel);
        };
    }

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnSourceChanged;
        _viewModel = viewModel;
        if (_viewModel == null)
        {
            _sourceRows = [];
            _serverGroups.ItemsSource = null;
            _serverResults.ItemsSource = null;
            _language = null;
        }
        if (_viewModel != null) _viewModel.PropertyChanged += OnSourceChanged;
        _expandedServerGroup = viewModel?.SimpleServerPreference?.GetCategory() ??
            (viewModel?.SimpleServerPreference is { CategoryName: null } ? viewModel?.SelectedSubscriptionServer?.ProtocolGroup : null);
        RefreshLabels();
        ResetSearch();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpConfigServer) or
            nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
            return;

        // Publish collection and selection changes together, after the current notification burst.
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshLabels();
            RefreshServerPicker();
        });
    }

    private void RefreshLabels()
    {
        _serverSearch.PlaceholderText = Strings.SmpSearchHint;
        AutomationProperties.SetName(_serverSearch, Strings.SmpSearchLabel);
        SetActionLabel(_searchButton, "SimpleServerSearchButtonLabel", Strings.SmpSearchServers);
        this.FindControl<TextBlock>("SimpleServerSearchLabel")!.Text = Strings.SmpSearchLabel;
        this.FindControl<TextBlock>("SimpleSearchLinkMessage")!.Text = Strings.SmpSearchLooksLikeLink;
        this.FindControl<Button>("SimpleTransferLink")!.Content = Strings.SmpMoveLink;
    }

    private void SetActionLabel(Button button, string labelName, string label)
    {
        this.FindControl<TextBlock>(labelName)!.Text = label;
        AutomationProperties.SetName(button, label);
    }

    private void RefreshChoice(MainWindowViewModel vm)
    {
        var choice = this.FindControl<TextBlock>("PickerSelection")!;
        choice.Text = vm.SimpleServerPreference is { } preference
            ? vm.SelectedSubscriptionServer == null ? Strings.SmpSelectionUnavailableTitle
                : preference.GetCategory() is { } category
                    ? $"{MainWindowViewModel.SimpleCategoryName(category)} · {Strings.SmpAutoInCategory}"
                    : $"{vm.SelectedSubscriptionServer.SimpleDisplayName} · {Strings.SmpPinnedServer}"
            : string.Empty;
        choice.IsVisible = choice.Text.Length > 0;
        var running = this.FindControl<TextBlock>("PickerRunning")!;
        running.IsVisible = Classes.Contains("advanced") && vm.IsConnected;
        running.Text = running.IsVisible ? $"{Strings.SmpCurrentServer} · {vm.SmpConfigName}" : string.Empty;
    }

    private void RefreshServerPicker()
    {
        if (_viewModel is not { } vm) return;

        RefreshChoice(vm);
        var selected = vm.SelectedSubscriptionServer;
        var current = vm.IsConnected ? vm.SmpConfigServer : selected;
        var query = _searchButton.IsChecked == true ? _serverSearch.Text?.Trim() ?? string.Empty : string.Empty;
        var looksLikeLink = SimpleInputDetector.Classify(query) != SmpInputKind.Invalid;
        _searchLinkHint.IsVisible = looksLikeLink;
        var filtering = query.Length != 0 && !looksLikeLink;
        _serverGroups.IsVisible = !filtering;
        _serverResults.IsVisible = _serverCount.IsVisible = filtering;
        if (!filtering)
        {
            _serverResults.ItemsSource = null;
            RefreshGroups(vm, selected, current);
            return;
        }

        var matches = vm.SubscriptionServers
            .Where(row => row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ServerCountryFlags.MatchesCountry(row.CountryCode, query))
            .OrderByDescending(row => ReferenceEquals(row, current))
            .ToArray();
        if (!_serverResults.Items.Cast<ServerViewModel>().SequenceEqual(matches))
            _serverResults.ItemsSource = matches;
        _serverResults.SelectedItem = selected != null && matches.Contains(selected) ? selected : null;
        _serverCount.Text = matches.Length == 0 ? Strings.SmpNoMatchingServers : Strings.SmpKindServers(matches.Length);
    }

    private void RefreshGroups(MainWindowViewModel vm, ServerViewModel? selected, ServerViewModel? current)
    {
        if (_language != Strings.Lang || !_sourceRows.SequenceEqual(vm.SubscriptionServers))
        {
            _language = Strings.Lang;
            _sourceRows = vm.SubscriptionServers.ToArray();
            _serverGroups.ItemsSource = _sourceRows.GroupBy(row => row.ProtocolGroup).OrderBy(group => group.Key)
                .Select(group => new SimpleServerGroup(group.Key,
                    group.OrderByDescending(row => ReferenceEquals(row, current)).ToArray()))
                .ToArray();
        }

        var category = vm.SimpleServerPreference?.GetCategory();
        foreach (var group in _serverGroups.Items.OfType<SimpleServerGroup>())
        {
            group.IsExpanded = group.Category == _expandedServerGroup;
            group.SelectedServer = selected != null && group.Servers.Contains(selected) ? selected : null;
            group.IsCurrent = group.Category == category && group.CanAutoSelect;
        }
    }

    private void ToggleServerSearch(object? sender, RoutedEventArgs args)
    {
        if (_searchButton.IsChecked == true) _serverSearch.Focus();
        else
        {
            ResetSearch();
        }
    }

    internal void Close()
    {
        _expandedServerGroup = null;
        ResetSearch();
    }

    private void ResetSearch()
    {
        _searchButton.IsChecked = false;
        _serverSearch.Text = string.Empty;
        _searchLinkHint.IsVisible = false;
        RefreshServerPicker();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) args.Handled = true;
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _viewModel == null) return;
        if (_searchButton.IsChecked == true)
        {
            ResetSearch();
            _searchButton.Focus();
        }
        else if (CloseRequested != null) CloseRequested();
        else return;
        args.Handled = true;
    }

    private void TransferSearchLink(object? sender, RoutedEventArgs args)
    {
        var input = _serverSearch.Text?.Trim();
        if (input == null || SimpleInputDetector.Classify(input) == SmpInputKind.Invalid) return;
        ResetSearch();
        LinkTransferred?.Invoke(input);
    }

    private void ToggleServerCategory(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: SimpleServerGroup group }) return;
        _expandedServerGroup = group.IsExpanded ? null : group.Category;
        foreach (var item in _serverGroups.Items.OfType<SimpleServerGroup>())
            item.IsExpanded = item.Category == _expandedServerGroup;
    }

    private void ChooseServerCategory(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: SimpleServerGroup group }) return;
        _viewModel?.ConfirmSimpleCategory(group.Category);
        ChoiceConfirmed?.Invoke();
    }

    private void WireGroupServerList(object? sender, RoutedEventArgs args)
    {
        if (sender is ServerPickerListBox list) list.ConfirmSelection = ConfirmServer;
    }

    private void ConfirmServer(ServerViewModel row)
    {
        _viewModel?.ConfirmSimpleServer(row);
        ChoiceConfirmed?.Invoke();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
