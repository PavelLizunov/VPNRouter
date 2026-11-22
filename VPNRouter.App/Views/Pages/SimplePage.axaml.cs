using System.ComponentModel;
using System.Runtime.InteropServices;
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

public partial class SimplePage : UserControl
{
    private readonly StackPanel _serverPanel;
    private readonly ToggleButton _searchButton;
    private readonly TextBox _serverSearch;
    private readonly StackPanel _searchLinkHint;
    private readonly ServerPickerListBox _serverResults;
    private readonly ItemsControl _serverGroups;
    private readonly ScrollViewer _serverGroupsScroll;
    private ProtocolGroup? _expandedServerGroup;
    private readonly TextBlock _serverCount;
    private readonly ToggleButton _linkButton;
    private readonly StackPanel _linkEditor;
    private readonly TextBox _linkInput;
    private readonly TextBlock _linkError;
    private readonly TextBlock _linkProgress;
    private readonly TextBlock _linkNotice;
    private readonly Button _saveLink;
    private readonly StackPanel _checksPanel;
    private readonly Button _changeServer;
    private readonly Button _appRules;
    private MainWindowViewModel? _viewModel;
    private bool _pickerOpen;
    private bool _refreshQueued;
    private bool _hasLinkDraft;
    private bool _savingLink;
    private int _linkDraftRevision;

    public SimplePage()
    {
        InitializeComponent();
        _serverPanel = this.FindControl<StackPanel>("SimpleServerPanel")!;
        _searchButton = this.FindControl<ToggleButton>("SimpleServerSearchButton")!;
        _serverSearch = this.FindControl<TextBox>("SimpleServerSearch")!;
        _searchLinkHint = this.FindControl<StackPanel>("SimpleSearchLinkHint")!;
        _serverResults = this.FindControl<ServerPickerListBox>("SimpleServerResults")!;
        _serverGroups = this.FindControl<ItemsControl>("SimpleServerGroups")!;
        _serverGroupsScroll = this.FindControl<ScrollViewer>("SimpleServerGroupsScroll")!;
        _serverCount = this.FindControl<TextBlock>("SimpleServerCount")!;
        _linkButton = this.FindControl<ToggleButton>("SimpleLinkButton")!;
        _linkEditor = this.FindControl<StackPanel>("SimpleLinkEditor")!;
        _linkInput = this.FindControl<TextBox>("SimpleConfigLink")!;
        _linkError = this.FindControl<TextBlock>("SimpleLinkError")!;
        _linkProgress = this.FindControl<TextBlock>("SimpleLinkProgress")!;
        _linkNotice = this.FindControl<TextBlock>("SimpleLinkNotice")!;
        _saveLink = this.FindControl<Button>("SimpleSaveLink")!;
        _checksPanel = this.FindControl<StackPanel>("SimpleChecks")!;
        _changeServer = this.FindControl<Button>("SimpleChangeServer")!;
        _appRules = this.FindControl<Button>("SimpleAppRules")!;
        _serverResults.ConfirmSelection = ConfirmServer;
        _serverSearch.TextChanged += (_, _) => RefreshServerPicker();
        _linkInput.TextChanged += (_, _) =>
        {
            _linkDraftRevision++;
            _linkError.Text = string.Empty;
        };
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
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            _linkDraftRevision++;
            _hasLinkDraft = false;
            _linkButton.IsChecked = false;
            _linkInput.Text = string.Empty;
            _linkNotice.Text = string.Empty;
        }
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnPickerSourceChanged;
        RefreshPickerLabels();
        RefreshServerPicker();
    }

    private void OnPickerSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsSimpleMode) && _viewModel?.IsSimpleMode == false)
            _linkDraftRevision++;

        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpPickerVisible) or
            nameof(MainWindowViewModel.SmpConfigServer) or nameof(MainWindowViewModel.SmpInput) or
            nameof(MainWindowViewModel.SmpHasRefreshableSubscriptions) or nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
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
        _serverSearch.PlaceholderText = Strings.SmpSearchHint;
        AutomationProperties.SetName(_serverSearch, Strings.SmpSearchLabel);
        SetActionLabel(_searchButton, "SimpleServerSearchButtonLabel", Strings.SmpSearchServers);
        SetActionLabel(_linkButton, "SimpleLinkButtonLabel", _viewModel?.SimpleUsesDirectLink == true
            ? Strings.SmpDirectLink : Strings.SmpSubscriptionLink);
        SetActionLabel(this.FindControl<ToggleButton>("SimpleCheckServers")!, "SimpleCheckServersLabel", Strings.SmpCheckServers);
        this.FindControl<TextBlock>("SimpleServerSearchLabel")!.Text = Strings.SmpSearchLabel;
        this.FindControl<TextBlock>("SimpleSearchLinkMessage")!.Text = Strings.SmpSearchLooksLikeLink;
        this.FindControl<Button>("SimpleTransferLink")!.Content = Strings.SmpMoveLink;
        this.FindControl<Button>("SimpleCancelLink")!.Content = Strings.CommonCancel;
        _saveLink.Content = Strings.SmpSaveLink;
        _linkProgress.Text = Strings.SmpLinkSaving;
        _appRules.Content = Strings.SmpApplicationRules;
    }

    private void SetActionLabel(Button button, string labelName, string label)
    {
        this.FindControl<TextBlock>(labelName)!.Text = label;
        AutomationProperties.SetName(button, label);
    }

    private void RefreshServerPicker()
    {
        var source = _viewModel;
        var wasOpen = _pickerOpen;
        _changeServer.IsVisible = source is { SmpHasSubscriptionServers: true, CanPickSimpleSubscriptionServer: true };
        _pickerOpen = source?.SmpPickerVisible == true && _changeServer.IsVisible;
        _serverPanel.IsVisible = _pickerOpen;
        _checksPanel.IsVisible = source is { SmpHasConfig: true } &&
            (source.SmpHasSubscriptionServers || source.SmpHasRefreshableSubscriptions);
        if (!_pickerOpen)
        {
            if (wasOpen)
            {
                ResetSearch();
                _serverResults.ItemsSource = null;
                _serverGroups.ItemsSource = null;
                _expandedServerGroup = null;
            }
            return;
        }

        var vm = source!;
        var selected = vm.SelectedSubscriptionServer;
        var current = vm.IsConnected ? vm.SmpConfigServer : selected;
        var query = _searchButton.IsChecked == true ? _serverSearch.Text?.Trim() ?? string.Empty : string.Empty;
        var looksLikeLink = SimpleInputDetector.Classify(query) != SmpInputKind.Invalid;
        _searchLinkHint.IsVisible = looksLikeLink;
        var filtering = query.Length != 0 && !looksLikeLink;
        _serverGroupsScroll.IsVisible = !filtering;
        _serverResults.IsVisible = _serverCount.IsVisible = filtering;
        if (!filtering)
        {
            _serverResults.ItemsSource = null;
            _serverGroups.ItemsSource = vm.SubscriptionServers
                .GroupBy(row => row.ProtocolGroup)
                .OrderBy(group => group.Key)
                .Select(group => new SimpleServerGroup(group.Key,
                    group.OrderByDescending(row => ReferenceEquals(row, current)).ToArray(),
                    selected != null && group.Contains(selected) ? selected : null,
                    vm.SimpleServerPreference?.GetCategory() == group.Key)
                {
                    IsExpanded = group.Key == _expandedServerGroup,
                }).ToArray();
            return;
        }

        var matches = vm.SubscriptionServers
            .Where(row => row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ServerCountryFlags.MatchesCountry(row.CountryCode, query))
            .OrderByDescending(row => ReferenceEquals(row, current))
            .ToArray();
        _serverResults.ItemsSource = matches;
        _serverResults.SelectedItem = selected != null && matches.Contains(selected) ? selected : null;
        _serverCount.Text = matches.Length == 0 ? Strings.SmpNoMatchingServers : Strings.SmpKindServers(matches.Length);
    }

    private void ToggleServerSearch(object? sender, RoutedEventArgs args)
    {
        if (_searchButton.IsChecked == true) _serverSearch.Focus();
        else
        {
            ResetSearch();
            RefreshServerPicker();
        }
    }

    private void ResetSearch()
    {
        _searchButton.IsChecked = false;
        _serverSearch.Text = string.Empty;
        _searchLinkHint.IsVisible = false;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) args.Handled = true;
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _viewModel is not { SmpPickerVisible: true } vm) return;
        if (_searchButton.IsChecked == true)
        {
            ResetSearch();
            RefreshServerPicker();
            _searchButton.Focus();
        }
        else
        {
            vm.SmpFormExpanded = false;
            _changeServer.Focus();
        }
        args.Handled = true;
    }

    private void ToggleLinkEditor(object? sender, RoutedEventArgs args)
    {
        _linkDraftRevision++;
        _linkNotice.Text = string.Empty;
        if (_linkButton.IsChecked != true) return;
        if (!_hasLinkDraft)
        {
            _linkInput.Text = _viewModel?.SimpleSavedLink ?? string.Empty;
            _hasLinkDraft = true;
        }
        _linkInput.Focus();
    }

    private void TransferSearchLink(object? sender, RoutedEventArgs args)
    {
        var input = _serverSearch.Text?.Trim();
        if (SimpleInputDetector.Classify(input) == SmpInputKind.Invalid) return;
        _linkDraftRevision++;
        _linkInput.Text = input;
        _linkError.Text = _linkNotice.Text = string.Empty;
        _hasLinkDraft = true;
        _linkButton.IsChecked = true;
        ResetSearch();
        RefreshServerPicker();
        _linkInput.Focus();
    }

    private void CancelLinkEdit(object? sender, RoutedEventArgs args)
    {
        _linkDraftRevision++;
        _hasLinkDraft = false;
        _linkInput.Text = _viewModel?.SimpleSavedLink ?? string.Empty;
        _linkError.Text = string.Empty;
        _linkButton.IsChecked = false;
        _linkButton.Focus();
    }

    private async void OnLinkEditorKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            CancelLinkEdit(sender, args);
        }
        else if (args.Key == Key.Enter && _linkInput.IsFocused)
        {
            args.Handled = true;
            await SaveLinkAsync();
        }
    }

    private async void SaveLink(object? sender, RoutedEventArgs args) => await SaveLinkAsync();

    private async Task SaveLinkAsync()
    {
        if (_savingLink || _viewModel is not { } vm || _linkButton.IsChecked != true) return;
        var revision = _linkDraftRevision;
        var input = _linkInput.Text ?? string.Empty;
        bool Current() => ReferenceEquals(_viewModel, vm) && _linkButton.IsChecked == true &&
            revision == _linkDraftRevision && input == (_linkInput.Text ?? string.Empty);

        _savingLink = true;
        _linkProgress.IsVisible = true;
        _linkError.Text = string.Empty;
        try
        {
            var error = await vm.SaveSimpleLinkAsync(input, Current);
            if (!Current()) return;
            if (error != null) { _linkError.Text = error; return; }

            var restoreFocus = _linkEditor.IsKeyboardFocusWithin;
            _hasLinkDraft = false;
            _linkButton.IsChecked = false;
            _linkNotice.Text = Strings.SmpLinkSaved;
            if (restoreFocus) _linkButton.Focus();
        }
        finally
        {
            _savingLink = false;
            _linkProgress.IsVisible = false;
        }
    }

    private void ToggleServerChecks(object? sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { IsChecked: true } || _viewModel is not { SmpHasSubscriptionServers: true } vm ||
            vm.IsTestingServers || vm.IsDeepTestingServers || vm.SmpSubscriptionsRefreshing) return;
        if (vm.TestAllSubscriptionServersCommand.CanExecute(null)) vm.TestAllSubscriptionServersCommand.Execute(null);
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
        if (_viewModel?.SmpPickerVisible == false) _changeServer.Focus();
    }

    private void WireGroupServerList(object? sender, RoutedEventArgs args)
    {
        if (sender is ServerPickerListBox list) list.ConfirmSelection = ConfirmServer;
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
