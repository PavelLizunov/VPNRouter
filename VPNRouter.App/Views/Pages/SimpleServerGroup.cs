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

    [ObservableProperty]
    private ServerViewModel? _selectedServer;

    [ObservableProperty]
    private bool _isCurrent;

    public SimpleServerGroup(ProtocolGroup category, ServerViewModel[] servers)
    {
        Category = category;
        Servers = servers;
        CanAutoSelect = servers.Any(row => SubscriptionServerPreference.CanAutoSelect(row.SourceEntry));
        SelectionHint = CanAutoSelect ? string.Empty : servers.Any(row => row.CanSelectInSimpleMode)
            ? Strings.SmpManualOnlyCategory : Strings.SmpServerUnsupported;
        Name = MainWindowViewModel.SimpleCategoryName(category);
        Protocols = DescribeProtocols(category, servers);
    }

    internal static string DescribeProtocols(ProtocolGroup category, IEnumerable<ServerViewModel> servers) =>
        string.Join(" · ", servers.Select(row =>
        {
            var entry = row.SourceEntry;
            if (entry.Awg != null) return "awg";
            if (entry.Dnstt != null) return "dnstt";
            return category is ProtocolGroup.StealthWeb or ProtocolGroup.WebFallback
                ? entry.Transport?.Type ?? "vless" : entry.Protocol ?? "vless";
        }).Select(protocol => protocol.ToLowerInvariant()).Distinct(StringComparer.Ordinal));

    public ProtocolGroup Category { get; }
    public string Name { get; }
    public string Protocols { get; }
    public ServerViewModel[] Servers { get; }
    public bool CanAutoSelect { get; }
    public string SelectionHint { get; }
    public string AutoText => Strings.SmpAutoInCategory;
    public string AutoLabel => $"{Name}. {Strings.SmpSelectCategory}";
    public string DisclosureLabel => $"{(IsExpanded ? Strings.SmpCollapseCategory : Strings.SmpExpandCategory)} · {Name}";
}
