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
