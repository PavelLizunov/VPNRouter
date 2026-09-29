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
}
