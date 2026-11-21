using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal sealed record TunnelServerLabel(string Name, string Address, int Port, string? RowKey = null, string? FeedId = null);

internal sealed class AcceptedTunnelInfo
{
    internal string ConfigMode { get; }
    internal string RoutingMode { get; }
    internal TunnelServerLabel Server { get; }
    internal int SubscriptionServerCount { get; }
    private readonly Dictionary<string, TunnelServerLabel> _members = new(StringComparer.Ordinal);
    private readonly (Uri Endpoint, string? Secret)? _controller;
    private readonly AppRoutePolicy? _appRoutePolicy;
    internal ClashSingBoxApi? OpenController(Serilog.ILogger logger) =>
        _controller is { } local ? ClashSingBoxApi.ForController(local.Endpoint, local.Secret, logger) : null;

    internal bool UsesAutoGroup { get; }
    internal TunnelServerLabel? FindMember(string? tag) =>
        tag != null && _members.TryGetValue(tag, out var member) ? member : null;

    internal AcceptedTunnelInfo(AppSettings settings, string configJson, string serverAddress,
        (Uri Endpoint, string? Secret)? controller, IReadOnlyDictionary<string, VlessServerEntry> proxySources)
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

        // Tag assignment and health/protocol filtering belong to the generator. Two different
        // source rows can produce the same host and port, or even identical wire outbounds.
        var feeds = new Dictionary<VlessServerEntry, string>();
        foreach (var feed in settings.App.Subscriptions ?? [])
        {
            if (feed?.Enabled != true || feed.Servers == null) continue;
            foreach (var row in feed.Servers)
                if (row != null) feeds.TryAdd(row, feed.Id);
        }

        TunnelServerLabel Label(VlessServerEntry row) =>
            new(row.Name, row.Server, row.Port, SubscriptionEntry.RowKey(row), feeds.GetValueOrDefault(row));

        if (proxySources.Count > 0)
            ConfigMode = proxySources.Values.Any(feeds.ContainsKey) ? "subscribe" : "generated";
        SubscriptionServerCount = ConfigMode == "subscribe" ? settings.Vless.GetEffectiveServers().Count : 0;
        Server = proxySources.TryGetValue("proxy", out var direct) ? Label(direct) : new("", serverAddress, 0);

        using var document = JsonDocument.Parse(configJson);
        _appRoutePolicy = AppRoutePolicy.Read(document.RootElement);
        if (!document.RootElement.TryGetProperty("outbounds", out var outbounds)) return;
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (!outbound.TryGetProperty("tag", out var tag) || tag.GetString() != "proxy" ||
                !outbound.TryGetProperty("type", out var type) || type.GetString() != "urltest") continue;
            UsesAutoGroup = true;
            foreach (var child in outbound.GetProperty("outbounds").EnumerateArray())
                if (child.GetString() is { } member && proxySources.TryGetValue(member, out var source))
                    _members.Add(member, Label(source));
            break;
        }
    }

    internal bool RoutesOnlyApp(string processName)
    {
        if (_appRoutePolicy is not { } route || !route.PrimaryProcesses.Contains(processName))
            return false;

        IEnumerable<string> expected = OperatingSystem.IsMacOS()
            ? ConfigGenerator.ExpandMacHelperNames([processName])
            : [processName];
        return route.ProxiedProcesses.IsSubsetOf(expected);
    }

    private sealed record AppRoutePolicy(HashSet<string> PrimaryProcesses, HashSet<string> ProxiedProcesses)
    {
        internal static AppRoutePolicy? Read(JsonElement config)
        {
            if (!config.TryGetProperty("route", out var route) || route.ValueKind != JsonValueKind.Object ||
                !route.TryGetProperty("final", out var final) || final.ValueKind != JsonValueKind.String ||
                final.GetString() != "direct" ||
                !route.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return null;

            var primary = new HashSet<string>(StringComparer.Ordinal);
            var proxied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("outbound", out var outbound))
                    continue;
                if (outbound.ValueKind != JsonValueKind.String) return null;
                var tag = outbound.GetString();
                if (tag == "direct") continue;
                if (tag is not ("proxy" or "proxy-udp") ||
                    !rule.TryGetProperty("process_name", out var names) || names.ValueKind != JsonValueKind.Array ||
                    names.GetArrayLength() == 0)
                    return null;

                foreach (var name in names.EnumerateArray())
                {
                    if (name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 } text)
                        return null;
                    proxied.Add(text);
                    if (tag == "proxy") primary.Add(text);
                }
            }
            return new(primary, proxied);
        }
    }
}
