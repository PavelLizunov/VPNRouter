using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class SubscriptionLinkEditor : UserControl
{
    private readonly ToggleButton _linkButton;
    private readonly StackPanel _linkEditor;
    private readonly TextBox _linkInput;
    private readonly TextBlock _linkError;
    private readonly TextBlock _linkProgress;
    private readonly TextBlock _linkNotice;
    private readonly Button _saveLink;
    private MainWindowViewModel? _viewModel;
    private bool _hasLinkDraft;
    private bool _savingLink;
    private int _linkDraftRevision;

    public SubscriptionLinkEditor()
    {
        InitializeComponent();
        _linkButton = this.FindControl<ToggleButton>("SimpleLinkButton")!;
        _linkEditor = this.FindControl<StackPanel>("SimpleLinkEditor")!;
        _linkInput = this.FindControl<TextBox>("SimpleConfigLink")!;
        _linkError = this.FindControl<TextBlock>("SimpleLinkError")!;
        _linkProgress = this.FindControl<TextBlock>("SimpleLinkProgress")!;
        _linkNotice = this.FindControl<TextBlock>("SimpleLinkNotice")!;
        _saveLink = this.FindControl<Button>("SimpleSaveLink")!;
        _linkEditor.AddHandler(InputElement.KeyDownEvent, OnLinkEditorKeyDown, RoutingStrategies.Tunnel);
        _linkInput.TextChanged += (_, _) =>
        {
            _linkDraftRevision++;
            _linkError.Text = string.Empty;
        };
        AttachedToVisualTree += (_, _) => Bind(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) Bind(DataContext as MainWindowViewModel);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnSourceChanged;
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            _linkDraftRevision++;
            _hasLinkDraft = false;
            _linkButton.IsChecked = false;
            _linkInput.Text = string.Empty;
            _linkNotice.Text = string.Empty;
        }
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnSourceChanged;
        RefreshLabels();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.IsSimpleMode)) _linkDraftRevision++;
        if (args.PropertyName is null or "" or nameof(MainWindowViewModel.SmpInput) or
            nameof(MainWindowViewModel.IsRussian))
            Dispatcher.UIThread.Post(RefreshLabels);
    }

    private void RefreshLabels()
    {
        SetActionLabel(_linkButton, "SimpleLinkButtonLabel", _viewModel?.SimpleUsesDirectLink == true
            ? Strings.SmpDirectLink : Strings.SmpSubscriptionLink);
        this.FindControl<Button>("SimpleCancelLink")!.Content = Strings.CommonCancel;
        _saveLink.Content = Strings.SmpSaveLink;
        _linkProgress.Text = Strings.SmpLinkSaving;
    }

    private void SetActionLabel(Button button, string labelName, string label)
    {
        this.FindControl<TextBlock>(labelName)!.Text = label;
        AutomationProperties.SetName(button, label);
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

    internal void SetDraft(string input)
    {
        _linkDraftRevision++;
        _linkInput.Text = input;
        _linkError.Text = _linkNotice.Text = string.Empty;
        _hasLinkDraft = true;
        _linkButton.IsChecked = true;
        _linkInput.Focus();
        _linkInput.BringIntoView();
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
            var result = await vm.SaveSimpleLinkDetailedAsync(input, Current);
            if (!Current()) return;
            if (result.Error != null) { _linkError.Text = result.Error; return; }

            var restoreFocus = _linkEditor.IsKeyboardFocusWithin;
            _hasLinkDraft = false;
            _linkButton.IsChecked = false;
            _linkNotice.Text = result.Notice ?? Strings.SmpLinkSaved;
            if (restoreFocus) _linkButton.Focus();
        }
        finally
        {
            _savingLink = false;
            _linkProgress.IsVisible = false;
        }
    }

}
