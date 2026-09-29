using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

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

    public string DisplayName => Strings.GroupDisplayName(Name);

    public AppGroupViewModel() { }

    public AppGroupViewModel(string name, string description, bool isChecked = false)
    {
        Name = name;
        Description = description;
        IsChecked = isChecked;
    }

    public System.Func<System.IDisposable>? BeginBatchUpdate { get; set; }

    partial void OnIsCheckedChanged(bool value)
    {
        using var _ = BeginBatchUpdate?.Invoke();
        foreach (var app in Apps)
            app.IsChecked = value;
    }

    [RelayCommand]
    private void SelectAll()
    {
        IsChecked = true;
        using var _ = BeginBatchUpdate?.Invoke();
        foreach (var app in Apps) app.IsChecked = true;
    }

    [RelayCommand]
    private void ClearAll()
    {
        IsChecked = false;
        using var _ = BeginBatchUpdate?.Invoke();
        foreach (var app in Apps) app.IsChecked = false;
    }

    public void NotifyDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));

    public override string ToString()
    {
        return Apps.Count > 0 ? $"{DisplayName} ({Apps.Count})" : DisplayName;
    }
}
