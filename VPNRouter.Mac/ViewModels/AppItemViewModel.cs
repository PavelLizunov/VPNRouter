using CommunityToolkit.Mvvm.ComponentModel;

namespace VPNRouter.Mac.ViewModels;

/// <summary>
/// ViewModel for a single app in the app selection list.
/// </summary>
public partial class AppItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isCustom;

    public AppItemViewModel() { }

    public AppItemViewModel(string processName, bool isChecked = false, bool isCustom = false)
    {
        ProcessName = processName;
        IsChecked = isChecked;
        IsCustom = isCustom;
    }
}
