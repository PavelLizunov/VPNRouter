using System.Runtime.Versioning;
using Serilog;

namespace VPNRouter.Core.Services;

// Windows Firewall rule operations through the in-process COM API (HNetCfg.FwPolicy2) instead of one netsh.exe per rule.
// A netsh process costs 50-200 ms (more with antivirus), and a split-tunnel connect handles about a hundred rules: that was 24 s to
// connect and 41 s to stop on the tester's machine. COM does the same in milliseconds.
// One outbound rule that is not tied to a program (the DNS leak lockdown): action, protocol, remote ports and remote addresses as netsh writes them.
internal sealed record FirewallRuleSpec(string Name, bool Allow, string Protocol, string RemotePorts, string RemoteAddresses, string Description);

internal interface IFirewallRuleStore
{
    bool AddOutboundBlockRule(string name, string programPath, bool enabled, string description);

    bool AddRule(FirewallRuleSpec spec);

    int SetEnabled(IReadOnlyCollection<string> names, bool enabled);

    int Remove(IReadOnlyCollection<string> names);

    List<string> FindByPrefixes(IReadOnlyList<string> prefixes);
}

[SupportedOSPlatform("windows")]
internal sealed class ComFirewallRuleStore : IFirewallRuleStore
{
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int DirectionOut = 2;
    private const int ProfilesAll = 0x7FFFFFFF;

    private readonly dynamic _policy;
    private readonly Type _ruleType;

    private ComFirewallRuleStore(dynamic policy, Type ruleType)
    {
        _policy = policy;
        _ruleType = ruleType;
    }

    internal static IFirewallRuleStore? TryCreate(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (policyType is null || ruleType is null) return null;
            var policy = Activator.CreateInstance(policyType);
            return policy is null ? null : new ComFirewallRuleStore(policy, ruleType);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[Firewall] COM firewall API unavailable - using netsh");
            return null;
        }
    }

    public bool AddOutboundBlockRule(string name, string programPath, bool enabled, string description)
    {
        dynamic rule = Activator.CreateInstance(_ruleType)!;
        rule.Name = name;
        rule.ApplicationName = programPath;
        rule.Direction = DirectionOut;
        rule.Action = ActionBlock;
        rule.Profiles = ProfilesAll;
        rule.Description = description;
        rule.Enabled = enabled;
        _policy.Rules.Add(rule);
        return true;
    }

    public bool AddRule(FirewallRuleSpec spec)
    {
        dynamic rule = Activator.CreateInstance(_ruleType)!;
        rule.Name = spec.Name;
        rule.Direction = DirectionOut;
        rule.Action = spec.Allow ? ActionAllow : ActionBlock;
        rule.Protocol = spec.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? 6 : 17;
        rule.RemotePorts = spec.RemotePorts;
        rule.RemoteAddresses = spec.RemoteAddresses;
        rule.Profiles = ProfilesAll;
        rule.Description = spec.Description;
        rule.Enabled = true;
        _policy.Rules.Add(rule);
        return true;
    }

    // What the firewall holds for a rule (for tests and diagnostics): action, protocol, ports, addresses, enabled.
    internal string? Describe(string name)
    {
        foreach (dynamic rule in (System.Collections.IEnumerable)_policy.Rules)
        {
            string n = rule.Name;
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
            return $"action={(int)rule.Action} protocol={(int)rule.Protocol} ports={(string)rule.RemotePorts} addresses={(string)rule.RemoteAddresses} enabled={(bool)rule.Enabled} direction={(int)rule.Direction}";
        }
        return null;
    }

    public int SetEnabled(IReadOnlyCollection<string> names, bool enabled)
    {
        if (names.Count == 0) return 0;
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic rule in (System.Collections.IEnumerable)_policy.Rules)
        {
            string name = rule.Name;
            if (!wanted.Contains(name)) continue;
            rule.Enabled = enabled;
            done.Add(name);
        }
        return done.Count;
    }

    public int Remove(IReadOnlyCollection<string> names)
    {
        var removed = 0;
        foreach (var name in names)
        {
            try
            {
                _policy.Rules.Remove(name);
                removed++;
            }
            catch
            {
            }
        }
        return removed;
    }

    public List<string> FindByPrefixes(IReadOnlyList<string> prefixes)
    {
        var found = new List<string>();
        foreach (dynamic rule in (System.Collections.IEnumerable)_policy.Rules)
        {
            string name = rule.Name;
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(name);
                    break;
                }
            }
        }
        return found;
    }
}
