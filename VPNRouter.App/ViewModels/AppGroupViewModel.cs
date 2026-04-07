using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a profile group in the Applications tab.
/// Contains a list of apps that belong to this profile.
/// </summary>
public partial class AppGroupViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isCustomGroup;

    public ObservableCollection<AppItemViewModel> Apps { get; } = new();

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
}
