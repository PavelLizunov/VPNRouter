using Avalonia.Threading;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    /// <summary>
    /// The system back button (or gesture) inside the app: it closes the topmost popup or overlay and then leaves the
    /// Advanced screens, one step at a time. Returns false when nothing is open, and the system then leaves the app.
    /// </summary>
    internal static bool TryHandleBack()
    {
        if (Avalonia.Application.Current is not AndroidApp app) return false;
        return Dispatcher.UIThread.CheckAccess()
            ? app.CloseTopmostLayer()
            : Dispatcher.UIThread.Invoke(app.CloseTopmostLayer);
    }

    private bool CloseTopmostLayer()
    {
        if (_kebabPopup is { IsOpen: true }) { _kebabPopup.IsOpen = false; return true; }
        if (_aboutOverlay is { IsVisible: true }) { _aboutOverlay.IsVisible = false; return true; }
        if (_logOverlay is { IsVisible: true }) { _logOverlay.IsVisible = false; return true; }
        if (_cfgExportOverlay is { IsVisible: true }) { HideExportOverlay(); return true; }
        if (_cfgImportOverlay is { IsVisible: true }) { HideImportOverlay(); return true; }
        if (_profilesOverlay is { IsVisible: true }) { _profilesOverlay.IsVisible = false; return true; }
        if (CloseSubscriptionEditor()) return true;
        if (_advShellOverlay is { IsVisible: true }) { CloseAdvancedShell(); return true; }
        return false;
    }
}
