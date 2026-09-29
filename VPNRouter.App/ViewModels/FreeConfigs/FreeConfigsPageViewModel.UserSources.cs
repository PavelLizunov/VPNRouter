using System.Collections.ObjectModel;
using System.Runtime;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigsPageViewModel
{
    [ObservableProperty] private string _newUserSourceName = string.Empty;
    [ObservableProperty] private string _newUserSourceUrl = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<VPNRouter.Core.Models.UserFreeSource> UserSources { get; } = new();

    public void ReloadUserSources()
    {
        UserSources.Clear();
        if (_getSettings?.Invoke() is { } s)
            foreach (var u in s.App.UserFreeSources)
                UserSources.Add(u);
    }

    [RelayCommand]
    private void AddUserSource()
    {
        var url = (NewUserSourceUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = Strings.FcUserSrcEmptyUrl;
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = Strings.FcUserSrcInvalidUrl;
            return;
        }

        var settings = _getSettings?.Invoke();
        if (settings == null) return;

        if (settings.App.UserFreeSources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = Strings.FcUserSrcDuplicate;
            return;
        }

        settings.App.UserFreeSources.Add(new VPNRouter.Core.Models.UserFreeSource
        {
            Name = (NewUserSourceName ?? string.Empty).Trim(),
            Url = url,
            Enabled = true,
            AddedAt = DateTime.UtcNow,
        });

        _settingsStore.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);

        ReloadUserSources();
        NewUserSourceName = string.Empty;
        NewUserSourceUrl = string.Empty;
        StatusText = Strings.FcUserSrcAdded;
    }

    [RelayCommand]
    private void RemoveUserSource(VPNRouter.Core.Models.UserFreeSource src)
    {
        if (src == null) return;
        var settings = _getSettings?.Invoke();
        if (settings == null) return;

        settings.App.UserFreeSources.RemoveAll(s =>
            string.Equals(s.Url, src.Url, StringComparison.OrdinalIgnoreCase));

        _settingsStore.Save(settings, VPNRouter.Core.AppPaths.ConfigYamlPath);
        ReloadUserSources();
        StatusText = Strings.FcUserSrcRemoved;
    }
}
