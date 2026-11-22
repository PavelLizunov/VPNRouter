using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;
using VPNRouter.Core.Models;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

internal sealed partial class SimpleServerGroup : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisclosureLabel))]
    private bool _isExpanded;

    public SimpleServerGroup(ProtocolGroup category, ServerViewModel[] servers, ServerViewModel? selected, bool isCurrent)
    {
        Category = category;
        Servers = servers;
        SelectedServer = selected;
        IsCurrent = isCurrent;
        Name = MainWindowViewModel.SimpleCategoryName(category);
        Protocols = string.Join(" · ", servers.Select(row =>
        {
            var entry = row.SourceEntry;
            return entry.Awg != null ? "awg" : category is ProtocolGroup.StealthWeb or ProtocolGroup.WebFallback
                ? (entry.Transport?.Type ?? "vless") : (entry.Protocol ?? "vless");
        }).Select(protocol => protocol.ToLowerInvariant()).Distinct(StringComparer.Ordinal));
    }

    public ProtocolGroup Category { get; }
    public string Name { get; }
    public string Protocols { get; }
    public ServerViewModel[] Servers { get; }
    public ServerViewModel? SelectedServer { get; }
    public bool IsCurrent { get; }
    public bool CanAutoSelect => Servers.Any(row => SubscriptionServerPreference.CanAutoSelect(row.SourceEntry));
    public string ManualOnlyText => Strings.SmpManualOnlyCategory;
    public string AutoText => Strings.SmpAutoInCategory;
    public string AutoLabel => $"{Name}. {Strings.SmpSelectCategory}";
    public string DisclosureLabel => $"{(IsExpanded ? Strings.SmpCollapseCategory : Strings.SmpExpandCategory)} · {Name}";
}
