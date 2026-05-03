using Avalonia.Controls;
using Avalonia.Platform;
using VPNRouter.App.ViewModels;

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
        //
        // v2.21.9: use the tile variant (penguin on white background)
        // instead of the transparent lineart. Linux window managers /
        // taskbars don't stylise the icon like Windows or macOS do, so
        // a bare transparent lineart looks bolted-on rather than like a
        // proper app icon. The tile matches the visual weight of
        // Windows + macOS taskbar icons.
        if (System.OperatingSystem.IsLinux())
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new System.Uri("avares://VPNRouter.App/Assets/penguin_mascot_tile.png"));
                this.Icon = new WindowIcon(stream);
            }
            catch { /* no icon is better than crashing */ }
        }

        // v2.31.6-r12 (Phase H, iter#4 audit): wire the VM's IDisposable
        // surface to the window's Closed event so timer/event leaks
        // can't survive an X-button close. Quit() also calls Dispose
        // (defensive double-call is idempotent), so explicit-quit and
        // window-close paths both clean up. If the user opens a future
        // ReloadMainWindowForLocalization-style window rebuild, the
        // outgoing window's Closed will fire and dispose the OLD VM —
        // the new window must use a fresh VM instance (per-window
        // ownership).
        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                try { vm.Dispose(); }
                catch { /* avoid raising on the closed-event path */ }
            }
        };
    }
}
