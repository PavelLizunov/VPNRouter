using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;

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
                var best = byNameMatches.FirstOrDefault(s =>
                    (!string.IsNullOrEmpty(activeUuid) && string.Equals(s.Uuid, activeUuid, System.StringComparison.Ordinal)) ||
                    (!string.IsNullOrEmpty(activeHost) && string.Equals(s.Server, activeHost, System.StringComparison.OrdinalIgnoreCase) && s.Port == activePort))
                    ?? byNameMatches[0];
                return SignatureOf(best.Server, best.Port, best.Uuid, best.Awg?.HeaderProtectionKey);
            }
        }

        if (!string.IsNullOrEmpty(activeHost) && activePort > 0)
        {
            var byEndpoint = list.FirstOrDefault(s =>
                string.Equals(s.Server, activeHost, System.StringComparison.OrdinalIgnoreCase) &&
                s.Port == activePort);
            if (byEndpoint != null) return SignatureOf(byEndpoint.Server, byEndpoint.Port, byEndpoint.Uuid, byEndpoint.Awg?.HeaderProtectionKey);
        }

        if (!string.IsNullOrEmpty(activeUuid))
        {
            var byUuid = list.Where(s => string.Equals(s.Uuid, activeUuid, System.StringComparison.Ordinal)).Take(2).ToList();
            if (byUuid.Count == 1)
                return SignatureOf(byUuid[0].Server, byUuid[0].Port, byUuid[0].Uuid, byUuid[0].Awg?.HeaderProtectionKey);
        }

        return null;
    }
}
