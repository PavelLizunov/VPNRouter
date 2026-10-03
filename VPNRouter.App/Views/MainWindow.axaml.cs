using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class MainWindow : Window
{
    // Set after InitializeComponent: the window base class raises ClientSize changes from its own constructor, before the name scope exists.
    private ListBox? _mainTabs;

    public MainWindow()
    {
        InitializeComponent();
        _mainTabs = this.FindControl<ListBox>("MainTabs");

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
                try { vm.Dispose(); }
                catch { }
            }
        };
    }

    // The home screen needs about 760 px of height; on a smaller screen the window is shortened to what the work area allows (title bar and margin
    // included) and kept on screen.
    internal const double PreferredHeight = 760;
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
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRussian))
            ApplyCompactTabs();
    }

    private void ApplyCompactTabs()
    {
        if (_mainTabs is not { } tabs) return;
        var russian = (DataContext as MainWindowViewModel)?.IsRussian == true;
        tabs.Classes.Set("compact", ShouldCompactTabs(ClientSize.Width, russian));
    }
}
