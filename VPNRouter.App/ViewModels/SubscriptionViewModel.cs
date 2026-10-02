using System;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

public partial class SubscriptionViewModel : ObservableObject
{
    private readonly SubscriptionEntry _entry;

    public string Id => _entry.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _url;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRefreshedDisplay))]
    [NotifyPropertyChangedFor(nameof(UserInfoDisplay))]
    [NotifyPropertyChangedFor(nameof(HasUserInfo))]
    private DateTimeOffset? _lastRefreshedAt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerCountText))]
    private int _lastServerCount;

    public string ServerCountText => string.Format(VPNRouter.Core.Localization.Strings.SubsServersFormat, LastServerCount);
    [ObservableProperty] private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadge))]
    private bool _lastRefreshFailed;

    public int CachedServerCount => _entry.Servers?.Count ?? 0;

    public string StatusBadge =>
        !LastRefreshFailed ? string.Empty
        : CachedServerCount > 0 ? VPNRouter.Core.Localization.Strings.SubRefreshFailedCached
        : VPNRouter.Core.Localization.Strings.SubRefreshFailedEmpty;

    public string LastRefreshedDisplay
    {
        get
        {
            if (LastRefreshedAt == null || LastRefreshedAt.Value.Year < 2000)
                return "—";
            return LastRefreshedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }

    public string UserInfoDisplay
    {
        get
        {
            var ui = VPNRouter.Core.Services.SubscriptionUserInfo.Parse(_entry.UserInfo);
            return ui?.FormatSummary(DateTimeOffset.UtcNow) ?? string.Empty;
        }
    }

    public bool HasUserInfo => UserInfoDisplay.Length > 0;

    public SubscriptionViewModel(SubscriptionEntry entry)
    {
        _entry = entry;
        _name = entry.Name;
        _url = entry.Url;
        _enabled = entry.Enabled;
        _lastRefreshedAt = entry.LastRefreshedAt;
        _lastServerCount = entry.LastServerCount;
    }

    public SubscriptionEntry ToEntry()
    {
        _entry.Name = Name;
        _entry.Url = Url;
        _entry.Enabled = Enabled;
        _entry.LastRefreshedAt = LastRefreshedAt;
        _entry.LastServerCount = LastServerCount;
        return _entry;
    }

    public SubscriptionEntry UnderlyingEntry => _entry;

    public override string ToString()
    {
        var enabledTag = Enabled ? string.Empty : " (off)";
        var countTag = LastServerCount > 0 ? $" — {LastServerCount} servers" : string.Empty;
        return $"{Name}{enabledTag}{countTag}";
    }
}
