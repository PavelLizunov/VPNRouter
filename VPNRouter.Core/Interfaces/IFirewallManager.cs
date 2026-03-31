namespace VPNRouter.Core.Interfaces;

/// <summary>
/// Platform-agnostic firewall manager for block_on_vpn_fail leak protection.
/// Windows: netsh.exe rules. macOS: pfctl anchor rules. Null: no-op.
/// </summary>
public interface IFirewallManager : IDisposable
{
    void CreateBlockRules(IEnumerable<string> processNames);
    void EnableBlockRules();
    void DisableBlockRules();
    void DeleteAllRules();
}
