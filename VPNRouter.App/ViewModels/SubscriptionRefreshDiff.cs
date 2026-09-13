using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// G3 (2026-06-27): pure helpers for the subscription auto-refresh reconnect
/// decision. Extracted out of <see cref="MainWindowViewModel"/> so the logic is
/// unit-testable without constructing the VM (and so it doesn't move the
/// characterization public-surface hash). A refresh must reconnect ONLY when the
/// ACTIVE server's identity (host|port|uuid) changed — a rotation of some OTHER
/// server in the pool must not drop the tunnel.
/// </summary>
internal static class SubscriptionRefreshDiff
{
    /// <summary>Identity signature of a server: host|port|uuid[|hpk].</summary>
    public static string SignatureOf(string? server, int port, string? uuid, string? hpk = null)
        => string.IsNullOrEmpty(hpk) ? $"{server}|{port}|{uuid}" : $"{server}|{port}|{uuid}|{hpk}";

    /// <summary>
    /// Signature of the ACTIVE server (matched by name, endpoint, or uuid) within a server set,
    /// or null if it isn't present. Prioritizes name/endpoint over shared uuid to prevent
    /// collapsing to server 0 when all subscription nodes share a single user-level UUID.
    /// </summary>
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

        // 1. Primary: Match by Name (and verify UUID or Host/Port if multiple match)
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

        // 2. Secondary: If name changed (provider renamed node), match by Host and Port
        if (!string.IsNullOrEmpty(activeHost) && activePort > 0)
        {
            var byEndpoint = list.FirstOrDefault(s =>
                string.Equals(s.Server, activeHost, System.StringComparison.OrdinalIgnoreCase) &&
                s.Port == activePort);
            if (byEndpoint != null) return SignatureOf(byEndpoint.Server, byEndpoint.Port, byEndpoint.Uuid, byEndpoint.Awg?.HeaderProtectionKey);
        }

        // 3. Tertiary: Match by UUID ONLY if the UUID uniquely identifies a single server in the pool
        if (!string.IsNullOrEmpty(activeUuid))
        {
            var byUuid = list.Where(s => string.Equals(s.Uuid, activeUuid, System.StringComparison.Ordinal)).Take(2).ToList();
            if (byUuid.Count == 1)
                return SignatureOf(byUuid[0].Server, byUuid[0].Port, byUuid[0].Uuid, byUuid[0].Awg?.HeaderProtectionKey);
        }

        return null;
    }
}
