using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed record TunnelServerLabel(string Name, string Address, int Port);

internal sealed class AcceptedTunnelInfo
{
    internal string ConfigMode { get; }
    internal string RoutingMode { get; }
    internal TunnelServerLabel Server { get; }
    internal int SubscriptionServerCount { get; }
    private readonly Dictionary<string, TunnelServerLabel> _members = new(StringComparer.Ordinal);
    private readonly (Uri Endpoint, string? Secret)? _controller;
    internal ClashSingBoxApi? OpenController(Serilog.ILogger logger) =>
        _controller is { } local ? ClashSingBoxApi.ForController(local.Endpoint, local.Secret, logger) : null;

    internal bool UsesAutoGroup => _members.Count != 0;
    internal TunnelServerLabel? FindMember(string? tag) =>
        tag != null && _members.TryGetValue(tag, out var member) ? member : null;

    internal AcceptedTunnelInfo(AppSettings settings, string configJson, string serverAddress,
        (Uri Endpoint, string? Secret)? controller)
    {
        _controller = controller;
        ConfigMode = (settings.App.ConfigMode ?? "generated").Trim().ToLowerInvariant();
        RoutingMode = string.Equals(settings.App.RoutingMode, "full", StringComparison.OrdinalIgnoreCase) ? "full" : "split";
        if (ConfigMode == "custom")
        {
            var name = settings.App.ActiveCustomConfig;
            if (string.IsNullOrEmpty(name)) name = settings.App.CustomConfigs.FirstOrDefault()?.Name;
            Server = new(name ?? "", serverAddress, 0);
            return;
        }

        var rows = settings.Vless.GetEffectiveServers();
        var selected = rows.FirstOrDefault(s => string.Equals(s.Name, settings.Vless.ActiveServer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Server, serverAddress, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(s => string.Equals(s.Server, serverAddress, StringComparison.OrdinalIgnoreCase));
        if (ConfigMode == "generated" && selected != null &&
            settings.App.Subscriptions.Any(feed => feed.Enabled && feed.Servers.Contains(selected)))
            ConfigMode = "subscribe";
        SubscriptionServerCount = ConfigMode == "subscribe" ? rows.Count : 0;
        Server = new(selected?.Name ?? "", serverAddress, selected?.Port ?? 0);
        if (ConfigMode != "subscribe" || !settings.Vless.AutoSelectBestServer) return;

        // Runtime member tags are assigned after filtering and may be suffixed for duplicate names.
        // Keep only their display fields from the generated configuration.
        using var document = JsonDocument.Parse(configJson);
        if (!document.RootElement.TryGetProperty("outbounds", out var outbounds)) return;
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outbound in outbounds.EnumerateArray())
            if (outbound.TryGetProperty("tag", out var tag) && tag.GetString() == "proxy" &&
                outbound.TryGetProperty("outbounds", out var group))
                foreach (var item in group.EnumerateArray())
                    if (item.GetString() is { } member) members.Add(member);

        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (!outbound.TryGetProperty("tag", out var tagValue) || tagValue.GetString() is not { } tag ||
                (tag != "proxy" && !members.Contains(tag)) ||
                !outbound.TryGetProperty("server", out var host) || host.GetString() is not { } address) continue;
            var port = outbound.TryGetProperty("server_port", out var portValue) && portValue.TryGetInt32(out var number) ? number : 0;
            var sameEndpoint = rows.Where(s => string.Equals(s.Server, address, StringComparison.OrdinalIgnoreCase) && s.Port == port);
            var row = sameEndpoint.FirstOrDefault(s => string.Equals("vless-" + s.Name, tag, StringComparison.Ordinal))
                ?? sameEndpoint.FirstOrDefault();
            if (tag == "proxy")
            {
                // A group narrowed to one healthy row is emitted as a direct proxy outbound.
                if (row != null) Server = new(row.Name, address, port);
            }
            else _members[tag] = new(row?.Name ?? "", address, port);
        }
    }
}
