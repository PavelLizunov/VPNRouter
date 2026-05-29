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

    // v2.38.0-r7 — set true when the most recent refresh fetch failed / returned
    // 0 (network down, provider DPI-blocked, transient). The cached servers are
    // preserved (RefreshEntryAsync keeps them on empty), so this drives an honest
    // "couldn't refresh — showing cached" badge instead of letting the card read
    // as "configs lost / banned". See Z:\surito 2026-05-29.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadge))]
    private bool _lastRefreshFailed;

    /// <summary>The actually-cached server count (survives a failed refresh +
    /// app restart — persisted via SubscriptionEntry.Servers YAML alias).</summary>
    public int CachedServerCount => _entry.Servers?.Count ?? 0;

    /// <summary>v2.38.0-r7 — honest one-line badge for the card. Empty on the
    /// happy path (the normal "URL · Ns · time" line shows). On a failed refresh
    /// it explains WHY (cached vs provider-unreachable) so a DPI-flap doesn't
    /// look like data loss.</summary>
    public string StatusBadge =>
        !LastRefreshFailed ? string.Empty
        : CachedServerCount > 0 ? VPNRouter.Core.Localization.Strings.SubRefreshFailedCached
        : VPNRouter.Core.Localization.Strings.SubRefreshFailedEmpty;

    public string LastRefreshedDisplay
    {
        get
        {
            // Treat null and MinValue (YamlDotNet default for missing nullable) as "never"
            if (LastRefreshedAt == null || LastRefreshedAt.Value.Year < 2000)
                return "—";
            return LastRefreshedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }

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

    /// <summary>v2.30.7-r2 — accessible name for UIA/screen readers (was
    /// leaking "VPNRouter.App.ViewModels.SubscriptionViewModel").</summary>
    public override string ToString()
    {
        var enabledTag = Enabled ? string.Empty : " (off)";
        var countTag = LastServerCount > 0 ? $" — {LastServerCount} servers" : string.Empty;
        return $"{Name}{enabledTag}{countTag}";
    }
}
