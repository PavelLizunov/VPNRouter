namespace VPNRouter.Core.Interfaces;

/// <summary>
/// Platform-agnostic firewall manager for block_on_vpn_fail leak protection.
/// Windows: netsh.exe rules. macOS: pfctl anchor rules. Null: no-op.
/// </summary>
public interface IFirewallManager : IDisposable
{
    /// <param name="processNames">Apps to protect (Windows per-process rules).
    /// On Linux/macOS the list is advisory only — those managers block globally.</param>
    /// <param name="isFullTunnel">P1 (2026-07-10): EXPLICIT routing intent. The
    /// Linux/macOS global kill-switch arms ONLY when this is true. Pre-fix they
    /// inferred full-tunnel from an EMPTY <paramref name="processNames"/> — so a
    /// SPLIT-tunnel user whose process scan timed out (empty list) had the whole
    /// host's egress dropped on a crash. Windows ignores it (per-process rules).
    /// Defaults true for the many Windows/HealthMonitor/VpnEngine test call sites
    /// where it's irrelevant; the one production caller passes the real mode.</param>
    void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true);
    void EnableBlockRules();
    void DisableBlockRules();
    void DeleteAllRules();
}

/// <summary>
/// Optional capability interface for firewall managers that support runtime
/// configuration updates from committed sing-box JSON (Linux nftables, macOS pf).
/// </summary>
internal interface ICommittedFirewallConfig
{
    void UpdateCommittedConfig(string configJson, bool enabledForFullTunnel);
}
