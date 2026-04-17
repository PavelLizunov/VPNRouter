using System;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// UI wrapper around SubscriptionEntry with observable properties and display helpers.
/// </summary>
public partial class SubscriptionViewModel : ObservableObject
{
    private readonly SubscriptionEntry _entry;

    public string Id => _entry.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _url;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRefreshedDisplay))]
    private DateTimeOffset? _lastRefreshedAt;
    [ObservableProperty] private int _lastServerCount;
    [ObservableProperty] private bool _isRefreshing;

    public string LastRefreshedDisplay => LastRefreshedAt == null
        ? "—"
        : LastRefreshedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public SubscriptionViewModel(SubscriptionEntry entry)
    {
        _entry = entry;
        _name = entry.Name;
        _url = entry.Url;
        _enabled = entry.Enabled;
        _lastRefreshedAt = entry.LastRefreshedAt;
        _lastServerCount = entry.LastServerCount;
    }

    /// <summary>Sync UI values back to the underlying model before save.</summary>
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
}
