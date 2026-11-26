#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Checks the active routing configuration against the capabilities of a legacy
/// rollback target. Inactive subscriptions and rows are deliberately not inspected.
/// </summary>
public static class RollbackConfigCompatibility
{
    public const string LegacyTelegramAutostart = "legacy-telegram-autostart";
    public const string ActiveConfigMissing = "active-config-missing";
    public const string ActiveDnstt = "active-dnstt";
    public const string ActiveTurnRelay = "active-turnrelay";
    public const string ActiveVmess = "active-vmess";
    public const string ActiveSingleOutbound = "active-single-outbound";
    public const string ActiveRowMissing = "active-row-missing";
    public const string ActiveRowAmbiguous = "active-row-ambiguous";
    public const string ActiveSubscriptionMissing = "active-subscription-missing";
    public const string ActiveSubscriptionDisabled = "active-subscription-disabled";
    public const string ActiveSubscriptionAmbiguous = "active-subscription-ambiguous";
    public const string ActiveChainAmbiguous = "active-chain-ambiguous";
    public const string ActiveCustomMissing = "active-custom-missing";
    public const string ActiveCustomUnreadable = "active-custom-unreadable";
    public const string ActiveCustomUnverified = "active-custom-unverified";

    public static string? CheckActiveConfig(AppSettings? settings, string? targetVersion) =>
        CheckActiveConfig(settings, targetVersion, () => TgProxyUpdater.HasRuntimeFiles() && !TgProxyUpdater.IsInstalled());

    internal static string? CheckActiveConfig(AppSettings? settings, string? targetVersion, Func<bool> hasOutdatedTelegramRuntime)
    {
        if (!IsLegacyTarget(targetVersion))
        {
            if (settings?.App == null || !UpdateChecker.TryParseSemVer(targetVersion, out var target) ||
                target.CompareTo(new UpdateChecker.SemVer(new Version(2, 51, 1), 4)) > 0)
                return null;
            if (string.Equals(settings.App.ConfigMode?.Trim(), "custom", StringComparison.OrdinalIgnoreCase))
                return CheckCustom(settings, turnRelayOnly: true) == ActiveTurnRelay ? ActiveTurnRelay : null;

            var snapshot = VpnEngine.CopySessionSettings(settings);
            VlessServersResolver.Resolve(snapshot);
            return VlessServersResolver.GetActiveServers(snapshot).Any(row => row.IsTurnRelay) ? ActiveTurnRelay : null;
        }
        if (settings?.App == null)
            return ActiveConfigMissing;

        if (settings.App.AutostartTgProxy && hasOutdatedTelegramRuntime())
            return LegacyTelegramAutostart;

        var mode = (settings.App.ConfigMode ?? string.Empty).Trim();
        if (mode.Equals("custom", StringComparison.OrdinalIgnoreCase))
            return CheckCustom(settings);
        if (mode.Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            return CheckSubscription(settings);
        return CheckGenerated(settings);
    }

    internal static bool IsLegacyTarget(string? targetVersion) =>
        UpdateChecker.TryParseSemVer(targetVersion, out var target) &&
        UpdateChecker.TryParseSemVer("2.50.5", out var ceiling) &&
        target.CompareTo(ceiling) <= 0;

    private static string? CheckGenerated(AppSettings settings)
    {
        if (settings.Vless is not { } vless) return ActiveConfigMissing;
        var rows = vless.GetEffectiveServers();
        if (rows.Count == 0) return ActiveConfigMissing;
        var selected = rows.FirstOrDefault(row => !string.IsNullOrEmpty(row.Name) &&
            string.Equals(row.Name, vless.ActiveServer, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => !row.IsTurnRelay) ?? rows[0];
        return CheckLegacyPool(selected, rows, vless.AutoSelectBestServer);
    }

    private static string? CheckServers(IEnumerable<VlessServerEntry>? servers)
    {
        var active = servers?.Where(item => item != null).ToList() ?? [];
        if (active.Count == 0)
            return ActiveConfigMissing;
        foreach (var server in active)
        {
            if (CheckServer(server) is { } issue)
                return issue;
        }
        return null;
    }

    private static string? CheckServer(VlessServerEntry? server)
    {
        if (server == null)
            return null;

        var protocol = (server.Protocol ?? string.Empty).Trim();
        if (server.IsTurnRelay || protocol.Equals("turnrelay", StringComparison.OrdinalIgnoreCase))
            return ActiveTurnRelay;

        if (server.IsDnsTunnel ||
            server.Dnstt != null ||
            protocol.Equals("dnstt", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("dns-tunnel", StringComparison.OrdinalIgnoreCase))
            return ActiveDnstt;

        if (protocol.Equals("vmess", StringComparison.OrdinalIgnoreCase) ||
            server.VmessSecurity != null ||
            server.AlterId != 0)
            return ActiveVmess;

        if (server.UseAsSingleOutbound)
            return ActiveSingleOutbound;

        return null;
    }

    private static string? CheckSubscription(AppSettings settings)
    {
        var app = settings.App;
        var subscriptions = (app.Subscriptions ?? [])
            .Where(item => item is { Servers: not null })
            .ToList();
        var activeName = app.ActiveSubscriptionServer ?? string.Empty;

        SubscriptionEntry? feed = null;
        if (!string.IsNullOrWhiteSpace(app.ActiveSubscriptionId))
            feed = subscriptions.FirstOrDefault(item =>
                string.Equals(item.Id, app.ActiveSubscriptionId, StringComparison.Ordinal));

        if (feed is { Enabled: false })
            return ActiveSubscriptionDisabled;

        if (feed == null)
        {
            if (string.IsNullOrWhiteSpace(activeName))
            {
                var enabled = subscriptions.Where(item => item.Enabled).ToList();
                if (enabled.Count == 1)
                    feed = enabled[0];
                else
                    return ActiveSubscriptionAmbiguous;
            }
            else
            {
                var matches = subscriptions
                    .Where(item => item.Enabled && item.Servers.Any(server =>
                        string.Equals(server.Name, activeName, StringComparison.OrdinalIgnoreCase)))
                    .Take(2)
                    .ToList();
                if (matches.Count == 1)
                    feed = matches[0];
                else if (matches.Count > 1)
                    return ActiveSubscriptionAmbiguous;
            }
        }

        if (feed == null)
            return ActiveSubscriptionMissing;
        if (!feed.Enabled)
            return ActiveSubscriptionDisabled;

        var pooled = subscriptions
            .Where(item => item.Enabled)
            .SelectMany(item => item.Servers)
            .ToList();
        if (pooled.Count == 0)
            return ActiveConfigMissing;

        VlessServerEntry? active = null;
        if (!string.IsNullOrWhiteSpace(activeName))
        {
            if (!string.IsNullOrWhiteSpace(app.ActiveSubscriptionRow))
            {
                active = feed.FindExactRow(activeName, app.ActiveSubscriptionRow);
                if (active == null)
                    return ActiveRowMissing;

                var preceding = pooled.TakeWhile(item => !ReferenceEquals(item, active));
                if (preceding.Any(item =>
                        string.Equals(item.Name, activeName, StringComparison.OrdinalIgnoreCase)))
                    return ActiveRowAmbiguous;
            }
            else
            {
                active = pooled.FirstOrDefault(item =>
                    string.Equals(item.Name, activeName, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            active = pooled.FirstOrDefault(row => !row.IsTurnRelay) ?? pooled[0];
        }

        if (active == null)
            return ActiveRowMissing;

        return CheckLegacyPool(active, pooled, settings.Vless.AutoSelectBestServer);
    }

    private static string? CheckLegacyPool(VlessServerEntry active, List<VlessServerEntry> rows, bool autoSelect)
    {
        if (CheckServer(active) is { } issue) return issue;
        if (!string.IsNullOrEmpty(active.DetourVia))
        {
            var upstreams = rows.Where(row => string.IsNullOrEmpty(row.DetourVia) &&
                string.Equals(row.OutboundId, active.DetourVia, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return upstreams.Count == 1 ? CheckServer(upstreams[0]) : ActiveChainAmbiguous;
        }
        if (autoSelect)
        {
            var pool = new VlessConfig { ActiveServer = active.Name, Servers = rows, AutoSelectBestServer = true };
            return CheckServers(pool.GetActiveServers());
        }
        // 2.50.5 pools every direct row with the same server, including newer protocols.
        return CheckServers(rows.Where(row => row.Server == active.Server && string.IsNullOrEmpty(row.DetourVia)));
    }

    private static string? CheckCustom(AppSettings settings, bool turnRelayOnly = false)
    {
        var path = VpnEngine.ResolveCustomConfigPath(settings);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ActiveCustomMissing;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            return ActiveCustomUnreadable;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return ActiveCustomUnverified;
            if (turnRelayOnly)
                return HasTurnRelay(root["outbounds"]) || HasTurnRelay(root["endpoints"]) ? ActiveTurnRelay : null;
            if (CustomConfigInjector.HasDnsttSshRoute(json))
                return ActiveDnstt;

            if (root["outbounds"] is JsonArray outbounds &&
                outbounds.OfType<JsonObject>().Any(IsNewOnlyOutbound))
                return ActiveDnsttOrVmess(outbounds);
            if (root["endpoints"] is JsonArray endpoints &&
                endpoints.OfType<JsonObject>().Any(IsNewOnlyOutbound))
                return ActiveDnsttOrVmess(endpoints);

            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return ActiveCustomUnverified;
        }

        static bool HasTurnRelay(JsonNode? node) =>
            node is JsonArray entries && entries.OfType<JsonObject>().Any(item =>
                item["type"] is JsonValue value && value.TryGetValue<string>(out var type) &&
                string.Equals(type, "turnrelay", StringComparison.OrdinalIgnoreCase));

        static bool IsNewOnlyOutbound(JsonObject item)
        {
            var type = item["type"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : string.Empty;
            return type.Equals("dnstt", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("dns-tunnel", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("turnrelay", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("vmess", StringComparison.OrdinalIgnoreCase);
        }

        static string ActiveDnsttOrVmess(JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                var type = item["type"] is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : string.Empty;
                if (type.Equals("turnrelay", StringComparison.OrdinalIgnoreCase))
                    return ActiveTurnRelay;
                if (type.Equals("vmess", StringComparison.OrdinalIgnoreCase))
                    return ActiveVmess;
                if (type.Equals("dnstt", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("dns-tunnel", StringComparison.OrdinalIgnoreCase))
                    return ActiveDnstt;
            }
            return ActiveCustomUnverified;
        }
    }
}
