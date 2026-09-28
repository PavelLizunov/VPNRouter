using CommunityToolkit.Mvvm.ComponentModel;

namespace VPNRouter.App.ViewModels;

public partial class AppItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private bool _isCustom;

    public Func<string, bool>? ReadMode { get; set; }

    public Action<string, bool>? WriteMode { get; set; }

    private bool _isCheckedFallback;

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
            var current = IsChecked;
            if (value == current) return;

            if (WriteMode != null && !string.IsNullOrEmpty(ProcessName))
                WriteMode(ProcessName, value);
            else
                _isCheckedFallback = value;

            OnPropertyChanged(nameof(IsChecked));
        }
    }

    public void RaiseIsCheckedChanged() => OnPropertyChanged(nameof(IsChecked));

    public AppItemViewModel() { }

    public AppItemViewModel(string processName, bool isChecked = false, bool isCustom = false)
    {
        ProcessName = processName;
        _isCheckedFallback = isChecked;
        IsCustom = isCustom;
    }
}
