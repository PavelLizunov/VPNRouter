using Avalonia.Controls;
using Avalonia.Platform;

namespace VPNRouter.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // v2.21.8: Linux needs a window-level icon or the window manager
        // falls back to a generic cogwheel in the taskbar. On macOS we
        // deliberately skip this — the OS chrome should stay bare. On
        // Windows the icon comes from the embedded Win32 resource
        // (ApplicationIcon in the csproj), so Avalonia's Window.Icon is
        // redundant there too.
        if (System.OperatingSystem.IsLinux())
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new System.Uri("avares://VPNRouter.App/Assets/penguin_mascot.png"));
                this.Icon = new WindowIcon(stream);
            }
            catch { /* no icon is better than crashing */ }
        }
    }
}
