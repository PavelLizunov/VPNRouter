namespace VPNRouter.Core.Interfaces;

public interface IFirewallManager : IDisposable
{
    void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true);
    void EnableBlockRules();
    void DisableBlockRules();
    void DeleteAllRules();
}

internal interface ICommittedFirewallConfig
{
    void UpdateCommittedConfig(string configJson, bool enabledForFullTunnel);
}
