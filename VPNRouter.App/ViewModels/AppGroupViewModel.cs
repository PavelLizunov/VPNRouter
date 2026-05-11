using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a profile group in the Applications tab.
/// Contains a list of apps that belong to this profile.
///
/// <para>v2.32.2 (AM-3, 2026-05-12): when the group-level master
/// CheckBox is toggled, the cascade flips every app's
/// <see cref="AppItemViewModel.IsChecked"/> — and since AppItem's
/// IsChecked is now bridged to the active mode list
/// (RoutingAppsInclude / RoutingAppsExclude), every app's write goes
/// straight into the right list. The group-level checked state itself
/// is NOT mode-aware; it represents "is this profile active" and is
/// persisted into <see cref="Models.AppSettings.ActiveProfile"/> /
/// <see cref="Models.CustomCategory.Enabled"/> regardless of routing
/// mode.</para>
/// </summary>
public partial class AppGroupViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = string.Empty;

    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isCustomGroup;
    [ObservableProperty] private bool _isCustomCategory;

    public ObservableCollection<AppItemViewModel> Apps { get; } = new();

    /// <summary>Localized display name derived from Name (internal ID stays in config.yaml).</summary>
    public string DisplayName => Strings.GroupDisplayName(Name);

    public AppGroupViewModel() { }

    public AppGroupViewModel(string name, string description, bool isChecked = false)
    {
        Name = name;
        Description = description;
        IsChecked = isChecked;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        // Cascade to every app in the group. Each AppItem.IsChecked
        // setter is now mode-aware (AM-3) — it writes to the active
        // RoutingAppsInclude / RoutingAppsExclude list via the bridge
        // callbacks wired by MainWindowViewModel.LoadApps. N writes in
        // a row is acceptable: SaveSettings is sub-millisecond and the
        // host VM debounces nothing here, matching the legacy cascade
        // behaviour that Bug-r9-I's auto-save piggybacks on.
        foreach (var app in Apps)
            app.IsChecked = value;
    }

    /// <summary>Force DisplayName to re-evaluate (call after Strings.Lang changes).</summary>
    public void NotifyDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));

    /// <summary>v2.30.7-r2 — accessible name for UIA/screen readers + automation tools
    /// (was leaking "VPNRouter.App.ViewModels.AppGroupViewModel").</summary>
    public override string ToString()
    {
        return Apps.Count > 0 ? $"{DisplayName} ({Apps.Count})" : DisplayName;
    }
}
