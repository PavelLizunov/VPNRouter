using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a profile group in the Applications tab.
/// Contains a list of apps that belong to this profile.
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
        foreach (var app in Apps)
            app.IsChecked = value;
    }

    /// <summary>Force DisplayName to re-evaluate (call after Strings.Lang changes).</summary>
    public void NotifyDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));
}
