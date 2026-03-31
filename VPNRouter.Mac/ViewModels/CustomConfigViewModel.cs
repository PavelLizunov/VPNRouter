using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;

namespace VPNRouter.Mac.ViewModels;

/// <summary>
/// ViewModel for a custom sing-box config entry.
/// </summary>
public partial class CustomConfigViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _protocols = "?";
    [ObservableProperty] private string _server = "?";
    [ObservableProperty] private bool _isActive;

    public CustomConfigViewModel() { }

    public CustomConfigViewModel(CustomConfigEntry entry, bool isActive = false)
    {
        Name = entry.Name;
        Path = entry.Path;
        IsActive = isActive;

        // Parse config info
        var resolvedPath = Environment.ExpandEnvironmentVariables(entry.Path);
        if (File.Exists(resolvedPath))
        {
            try
            {
                var json = File.ReadAllText(resolvedPath);
                var (protocols, server) = Core.Services.CustomConfigInjector.ParseConfigInfo(json);
                Protocols = protocols;
                Server = server;
            }
            catch { }
        }
    }

    public CustomConfigEntry ToEntry() => new()
    {
        Name = Name,
        Path = Path
    };
}
