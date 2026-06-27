namespace VPNRouter.App.ViewModels;

/// <summary>
/// G2 (2026-06-27): pure decision for the connected-status (name, ip) when
/// AutoSelectBestServer builds a urltest "proxy" group — sing-box picks the
/// fastest member at runtime, so the status must show the REAL selected node
/// (resolved from clash_api), not the user's nominal pick (the "Iceland ·
/// German-IP" mismatch). Extracted from MainWindowViewModel so it's
/// unit-testable and doesn't move the characterization public-surface hash.
/// </summary>
internal static class AutoSelectStatus
{
    /// <summary>
    /// Resolve the (name, ip) shown for a subscribe-mode connection.
    /// <list type="bullet">
    ///   <item>auto-select on + the real node is known → that node's (name, ip)</item>
    ///   <item>auto-select on + node not yet resolved → a generic "auto-select"
    ///   label with no ip (avoid asserting a stale server)</item>
    ///   <item>auto-select off → the user's nominal pick</item>
    /// </list>
    /// </summary>
    public static (string? name, string? ip) ResolveSubscribeLabel(
        bool autoSelectOn,
        bool hasAutoNode,
        string? autoName,
        string? autoIp,
        string autoLabel,
        string? nominalName,
        string? nominalIp)
    {
        if (autoSelectOn)
            return hasAutoNode ? (autoName, autoIp) : (autoLabel, (string?)null);
        return (nominalName, nominalIp);
    }
}
