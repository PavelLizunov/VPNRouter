using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            ApplyCompactTabs();
            if (this.FindControl<ListBox>("MainTabs") is { } tabs)
                tabs.SizeChanged += (_, _) => ApplyCompactTabs();
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

    // When the main tabs do not fit with their labels (a narrow window, or longer Russian labels), only the selected one keeps its label and the
    // others show their icon (the label is their tooltip), so all of them stay reachable without sideways scrolling.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
            ApplyCompactTabs();
    }

    private void ApplyCompactTabs()
    {
        if (this.FindControl<ListBox>("MainTabs") is not { } tabs) return;
        var available = ClientSize.Width;
        if (available <= 0) return;

        tabs.Classes.Set("compact", false);
        tabs.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tabs.Classes.Set("compact", tabs.DesiredSize.Width > available);
    }
}
