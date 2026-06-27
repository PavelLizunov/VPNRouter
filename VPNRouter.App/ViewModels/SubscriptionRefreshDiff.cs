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
    /// <summary>Identity signature of a server: host|port|uuid.</summary>
    public static string SignatureOf(string? server, int port, string? uuid)
        => $"{server}|{port}|{uuid}";

    /// <summary>
    /// Signature of the ACTIVE server (matched by name) within a server set,
    /// or null if it isn't present.
    /// </summary>
    public static string? ActiveServerSignature(IEnumerable<VlessServerEntry>? servers, string? activeName)
        => servers?
            .Where(s => s != null && string.Equals(s.Name, activeName, System.StringComparison.Ordinal))
            .Select(s => SignatureOf(s.Server, s.Port, s.Uuid))
            .FirstOrDefault();
}
