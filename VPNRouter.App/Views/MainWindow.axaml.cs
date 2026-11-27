using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class MainWindow : Window
{
    // Set after InitializeComponent: the window base class raises ClientSize changes from its own constructor, before the name scope exists.
    private ListBox? _mainTabs;
    private int _quickAddRevision;
    private bool _quickAddSaving;

    public MainWindow()
    {
        InitializeComponent();
        _mainTabs = this.FindControl<ListBox>("MainTabs");
        if (QuickAddButton?.Flyout is { } quickAddFlyout)
        {
            quickAddFlyout.Opened += OnQuickAddFlyoutOpened;
            quickAddFlyout.Closed += OnQuickAddFlyoutClosed;
        }
        if (QuickAddInput != null)
        {
            QuickAddInput.TextChanged += (_, _) =>
            {
                _quickAddRevision++;
                if (QuickAddError != null)
                {
                    QuickAddError.Text = string.Empty;
                    QuickAddError.IsVisible = false;
                }
                if (QuickAddNotice != null)
                {
                    QuickAddNotice.Text = string.Empty;
                    QuickAddNotice.IsVisible = false;
                }
            };
        }
        RefreshMenuLabels();

        Opened += (_, _) =>
        {
            FitToScreen();
            ApplyCompactTabs();
            try { LaunchFailureCounter.MarkStable(); }
            catch { }
        };

        if (System.OperatingSystem.IsLinux())
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new System.Uri("avares://VPNRouter.App/Assets/penguin_mascot_tile.png"));
                this.Icon = new WindowIcon(stream);
            }
            catch { }
        }

        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged -= OnViewModelPropertyChanged;
                try { vm.Dispose(); }
                catch { }
            }
        };
    }

    // The home screen needs about 820 px of height; on a smaller screen the window is shortened to what the work area allows (title bar and margin
    // included) and kept on screen.
    internal const double PreferredHeight = 820;
    private const double FrameAllowance = 48;

    internal static double FittedHeight(double preferred, double workAreaHeightDip, double minimum) =>
        Math.Max(minimum, Math.Min(preferred, workAreaHeightDip - FrameAllowance));

    // Back to the default size (width 520, the tall default fitted to the screen) after the window was squeezed or stretched; bound to a
    // double click on the brand tile and to a menu item.
    internal void ResetWindowSize()
    {
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Width = 520;
        Height = PreferredHeight;
        FitToScreen();
    }

    private void OnBrandTileDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ResetWindowSize();
        e.Handled = true;
    }

    private void OnResetWindowSizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ResetWindowSize();

    private void OnOpenVersionHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.FindControl<Button>("OverflowMenuButton")?.Flyout is { } flyout)
            flyout.Hide();
        if (DataContext is MainWindowViewModel vm)
            vm.OpenVersionHistoryFromMenu();
    }

    private void RefreshMenuLabels()
    {
        if (MenuAppSectionLabel != null)
            MenuAppSectionLabel.Text = VPNRouter.Core.Localization.Strings.SmpMenuAppSection;
        if (MenuVersionHistoryButton != null)
            MenuVersionHistoryButton.Content = VPNRouter.Core.Localization.Strings.SmpMenuVersionHistory;

        var russian = (DataContext as MainWindowViewModel)?.IsRussian ?? VPNRouter.Core.Localization.Strings.Lang == "ru";
        var addTitle = russian ? "Добавить подписку или сервер" : "Add subscription or server";
        if (QuickAddButton != null)
        {
            ToolTip.SetTip(QuickAddButton, addTitle);
            AutomationProperties.SetName(QuickAddButton, addTitle);
        }
        if (QuickAddTitle != null)
            QuickAddTitle.Text = addTitle;
        if (QuickAddInput != null)
            AutomationProperties.SetName(QuickAddInput, addTitle);
        if (QuickAddPasteButton != null)
            QuickAddPasteButton.Content = russian ? "Вставить" : "Paste";
        if (QuickAddCancelButton != null)
            QuickAddCancelButton.Content = VPNRouter.Core.Localization.Strings.CommonCancel;
        if (QuickAddSaveButton != null)
            QuickAddSaveButton.Content = russian ? "Добавить" : "Add";
    }

    private void OnQuickAddFlyoutOpened(object? sender, EventArgs e)
    {
        _quickAddRevision++;
        RefreshMenuLabels();
        if (QuickAddInput != null)
        {
            QuickAddInput.Text = string.Empty;
            QuickAddInput.IsEnabled = !_quickAddSaving;
        }
        if (QuickAddError != null)
        {
            QuickAddError.Text = string.Empty;
            QuickAddError.IsVisible = false;
        }
        if (QuickAddNotice != null)
        {
            QuickAddNotice.Text = string.Empty;
            QuickAddNotice.IsVisible = false;
        }
        if (QuickAddProgress != null)
            QuickAddProgress.IsVisible = _quickAddSaving;
        QuickAddInput?.Focus();
    }

    private void OnQuickAddFlyoutClosed(object? sender, EventArgs e) => _quickAddRevision++;

    private void OnQuickAddPasteClick(object? sender, RoutedEventArgs e)
    {
        if (QuickAddInput == null || _quickAddSaving) return;
        QuickAddInput.Focus();
        QuickAddInput.SelectAll();
        try { QuickAddInput.Paste(); }
        catch { }
    }

    private void OnQuickAddCancelClick(object? sender, RoutedEventArgs e)
    {
        _quickAddRevision++;
        QuickAddButton?.Flyout?.Hide();
    }

    private void OnQuickAddInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _quickAddRevision++;
            QuickAddButton?.Flyout?.Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = SaveQuickAddAsync();
        }
    }

    private void OnQuickAddSaveClick(object? sender, RoutedEventArgs e) => _ = SaveQuickAddAsync();

    private async Task SaveQuickAddAsync()
    {
        if (_quickAddSaving || DataContext is not MainWindowViewModel vm || QuickAddInput == null) return;

        var input = QuickAddInput.Text ?? string.Empty;
        var revision = _quickAddRevision;
        _quickAddSaving = true;
        SetQuickAddBusy(true);
        try
        {
            var result = await vm.SaveSimpleLinkDetailedAsync(
                input,
                () => _quickAddRevision == revision && ReferenceEquals(DataContext, vm));
            if (_quickAddRevision != revision || !ReferenceEquals(DataContext, vm)) return;

            if (result.Error != null)
            {
                if (QuickAddError != null)
                {
                    QuickAddError.Text = result.Error;
                    QuickAddError.IsVisible = true;
                }
                QuickAddInput.Focus();
                return;
            }

            if (result.Notice == VPNRouter.Core.Localization.Strings.SmpLinkAlreadySaved)
            {
                if (QuickAddNotice != null)
                {
                    QuickAddNotice.Text = result.Notice;
                    QuickAddNotice.IsVisible = true;
                }
                return;
            }

            QuickAddInput.Text = string.Empty;
            QuickAddButton?.Flyout?.Hide();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[MainWindow] QuickAdd save failed");
            if (_quickAddRevision == revision && QuickAddError != null)
            {
                QuickAddError.Text = VPNRouter.Core.Localization.Strings.SmpInvalidServerLink;
                QuickAddError.IsVisible = true;
            }
        }
        finally
        {
            _quickAddSaving = false;
            SetQuickAddBusy(false);
        }
    }

    private void SetQuickAddBusy(bool busy)
    {
        if (QuickAddProgress != null) QuickAddProgress.IsVisible = busy;
        if (QuickAddInput != null) QuickAddInput.IsEnabled = !busy;
        if (QuickAddPasteButton != null) QuickAddPasteButton.IsEnabled = !busy;
        if (QuickAddSaveButton != null) QuickAddSaveButton.IsEnabled = !busy;
    }

    private void FitToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            var workArea = screen.WorkingArea;
            var fitted = FittedHeight(Height, workArea.Height / screen.Scaling, MinHeight);
            if (fitted < Height) Height = fitted;
            if (Position.Y < workArea.Y) Position = new PixelPoint(Position.X, workArea.Y);
        }
        catch
        {
            // a window that could not be fitted is still usable
        }
    }

    // Widths at which the six main tabs still fit with their labels (measured on the real labels: about 500 px in English and about 630 px in Russian,
    // with the Tools tab). Below them only the selected tab keeps its label and the others show their icon (the label is their tooltip), so all of them stay
    // reachable without sideways scrolling. A fixed rule on purpose: deciding by measuring the strip from inside layout left stale item widths, and
    // deciding from a posted job could loop.
    internal const double CompactTabsBelowEnglish = 510;
    internal const double CompactTabsBelowRussian = 650;

    internal static bool ShouldCompactTabs(double width, bool russian) =>
        width > 0 && width < (russian ? CompactTabsBelowRussian : CompactTabsBelowEnglish);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty || change.Property == DataContextProperty)
        {
            if (change.Property == DataContextProperty)
            {
                if (change.OldValue is MainWindowViewModel old) old.PropertyChanged -= OnViewModelPropertyChanged;
                if (change.NewValue is MainWindowViewModel current) current.PropertyChanged += OnViewModelPropertyChanged;
            }
            ApplyCompactTabs();
            RefreshMenuLabels();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRussian))
        {
            ApplyCompactTabs();
            RefreshMenuLabels();
        }
    }

    private void ApplyCompactTabs()
    {
        if (_mainTabs is not { } tabs) return;
        var russian = (DataContext as MainWindowViewModel)?.IsRussian == true;
        tabs.Classes.Set("compact", ShouldCompactTabs(ClientSize.Width, russian));
    }

    internal int ReleaseInactivePages()
    {
        var seen = new HashSet<object?>(ReferenceEqualityComparer.Instance);
        var toUnload = new List<DeferredPage>();

        void Consider(DeferredPage page)
        {
            if (seen.Add(page) && !page.IsActive && page.Content != null)
                toUnload.Add(page);
        }

        foreach (var page in this.GetLogicalDescendants().OfType<DeferredPage>())
            Consider(page);

        foreach (var page in this.GetVisualDescendants().OfType<DeferredPage>())
            Consider(page);

        var released = 0;
        foreach (var page in toUnload)
        {
            if (!page.IsActive && page.Content != null)
            {
                page.UnloadIfInactive();
                if (page.Content == null)
                    released++;
            }
        }

        return released;
    }
}
