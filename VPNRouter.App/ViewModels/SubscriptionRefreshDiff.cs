using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

internal static class SubscriptionRefreshDiff
{
    public static string SignatureOf(string? server, int port, string? uuid, string? hpk = null)
        => string.IsNullOrEmpty(hpk) ? $"{server}|{port}|{uuid}" : $"{server}|{port}|{uuid}|{hpk}";

    public static string? ActiveServerSignature(
        IEnumerable<VlessServerEntry>? servers,
        string? activeName,
        string? activeUuid = null,
        string? activeHost = null,
        int activePort = 0,
        string? activeHpk = null)
    {
        var selected = FindActiveServer(servers, activeName, activeUuid, activeHost, activePort);
        return selected == null ? null : SignatureOf(selected.Server, selected.Port, selected.Uuid, selected.Awg?.HeaderProtectionKey);
    }

    internal static VlessServerEntry? FindRegisteredServer(SubscriptionEntry? source, IEnumerable<VlessServerEntry> all,
        string? name, string? key, VlessServerEntry? previous, string? previousRoute)
    {
        if (source != null && previous != null && (previous.Dnstt != null || !string.IsNullOrEmpty(previous.DetourVia)))
            return new SubscriptionChainSelection(source.Id, source.Url, previous, previousRoute).Match(source, all);
        var pool = source?.Servers ?? all;
        var exact = source?.FindExactRow(name, key);
        if (exact != null) return exact;
        if (source != null && previous != null && previousRoute != null)
        {
            var equivalent = source.Servers.Where(s => s.Port == previous.Port && s.Server == previous.Server &&
                SubscriptionEntry.RouteKey(s, source.Servers) == previousRoute).ToList();
            if (equivalent.Count > 0) return equivalent.FirstOrDefault(s => s.Name == name) ?? equivalent[0];
        }
        return FindActiveServer(pool, name, previous?.Uuid, previous?.Server, previous?.Port ?? 0)
            ?? pool.FirstOrDefault(s => !string.IsNullOrEmpty(name) && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? pool.FirstOrDefault();
    }

    internal static VlessServerEntry? FindActiveServer(IEnumerable<VlessServerEntry>? servers,
        string? activeName, string? activeUuid, string? activeHost, int activePort)
    {
        if (servers == null) return null;
        var list = servers.Where(s => s != null).ToList();
        if (list.Count == 0) return null;

        if (!string.IsNullOrEmpty(activeName))
        {
            var byNameMatches = list
                .Where(s => string.Equals(s.Name, activeName, System.StringComparison.Ordinal))
                .ToList();

            if (byNameMatches.Count > 0)
            {
                bool SameEndpoint(VlessServerEntry s) => !string.IsNullOrEmpty(activeHost) && s.Port == activePort &&
                    string.Equals(s.Server, activeHost, System.StringComparison.OrdinalIgnoreCase);
                bool SameUuid(VlessServerEntry s) => !string.IsNullOrEmpty(activeUuid) && s.Uuid == activeUuid;
                return byNameMatches.FirstOrDefault(s => SameEndpoint(s) && SameUuid(s))
                    ?? byNameMatches.FirstOrDefault(SameEndpoint)
                    ?? byNameMatches.FirstOrDefault(SameUuid)
                    ?? byNameMatches[0];
            }
        }

        if (!string.IsNullOrEmpty(activeHost) && activePort > 0)
        {
            var byEndpoint = list.FirstOrDefault(s =>
                string.Equals(s.Server, activeHost, System.StringComparison.OrdinalIgnoreCase) &&
                s.Port == activePort);
            if (byEndpoint != null) return byEndpoint;
        }

        if (!string.IsNullOrEmpty(activeUuid))
        {
            var byUuid = list.Where(s => string.Equals(s.Uuid, activeUuid, System.StringComparison.Ordinal)).Take(2).ToList();
            if (byUuid.Count == 1)
                return byUuid[0];
        }

        return null;
    }
}
