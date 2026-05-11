using CommunityToolkit.Mvvm.ComponentModel;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a single app in the app selection list.
///
/// <para>v2.32.2 (AM-3, 2026-05-12): the <see cref="IsChecked"/> property is
/// now a bridge into <see cref="MainWindowViewModel"/>'s mode-aware
/// helpers. When the user toggles the checkbox we write into
/// <see cref="Models.AppConfig.RoutingAppsInclude"/> or
/// <see cref="Models.AppConfig.RoutingAppsExclude"/> based on the active
/// <see cref="Models.AppConfig.RoutingAppsMode"/>; when the mode flips we
/// re-read from the now-active list. Both lists hold their own state
/// independently — toggling between modes never copies or wipes the
/// inactive list. See <c>plans/r10-stas-confirmed-and-apps-2mode.md</c>
/// §3 (AM-3 acceptance).</para>
///
/// <para>Bridges are opt-in via <see cref="ReadMode"/> / <see cref="WriteMode"/>
/// callbacks set by the host VM during <c>LoadApps</c>. When no bridge is
/// attached (e.g. unit tests that construct items directly) the property
/// falls back to a plain backing field — preserving the legacy ctor
/// behaviour for any caller that doesn't yet wire a host.</para>
/// </summary>
public partial class AppItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private bool _isCustom;

    // ─── Mode-aware bridge (AM-3) ───────────────────────────────────────

    /// <summary>
    /// Callback that reads the checked state for this app from the
    /// currently-active mode list. When null the item uses
    /// <see cref="_isCheckedFallback"/>.
    /// </summary>
    public Func<string, bool>? ReadMode { get; set; }

    /// <summary>
    /// Callback that writes the checked state for this app into the
    /// currently-active mode list. When null the setter only updates
    /// <see cref="_isCheckedFallback"/>.
    /// </summary>
    public Action<string, bool>? WriteMode { get; set; }

    /// <summary>
    /// Backing for <see cref="IsChecked"/> when no <see cref="ReadMode"/>
    /// / <see cref="WriteMode"/> bridge is attached. Keeps the type usable
    /// from tests and any caller that hasn't migrated to the bridged
    /// flow.
    /// </summary>
    private bool _isCheckedFallback;

    /// <summary>
    /// Bridged checked state. Reads from / writes to the active mode
    /// list (RoutingAppsInclude / RoutingAppsExclude) when wired; falls
    /// back to a local field otherwise.
    /// </summary>
    public bool IsChecked
    {
        get
        {
            if (ReadMode != null && !string.IsNullOrEmpty(ProcessName))
                return ReadMode(ProcessName);
            return _isCheckedFallback;
        }
        set
        {
            // Compare against current effective value so the setter is
            // idempotent and CommunityToolkit's PropertyChanged firing
            // policy (only on real change) is preserved end-to-end.
            var current = IsChecked;
            if (value == current) return;

            if (WriteMode != null && !string.IsNullOrEmpty(ProcessName))
                WriteMode(ProcessName, value);
            else
                _isCheckedFallback = value;

            OnPropertyChanged(nameof(IsChecked));
        }
    }

    /// <summary>
    /// Notify subscribers that <see cref="IsChecked"/> may have changed
    /// (e.g. after a mode flip). Called by <see cref="MainWindowViewModel"/>
    /// during <c>RefreshAppCheckboxes</c>.
    /// </summary>
    public void RaiseIsCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    public AppItemViewModel() { }

    public AppItemViewModel(string processName, bool isChecked = false, bool isCustom = false)
    {
        ProcessName = processName;
        _isCheckedFallback = isChecked;
        IsCustom = isCustom;
    }
}
