using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FirewallDnsLockdownSpecTests
{
    private const string Range = "0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";

    [Fact]
    public void Specs_AreTheSevenRulesOfTheLockdown_WithTheirPortsProtocolsAndActions()
    {
        var specs = FirewallManager.BuildDnsLockdownSpecs(Range);

        Assert.Equal(7, specs.Count);
        var allow = Assert.Single(specs, s => s.Allow);
        Assert.Equal(("UDP", "53", "127.0.0.1"), (allow.Protocol, allow.RemotePorts, allow.RemoteAddresses));

        var blocks = specs.Where(s => !s.Allow).ToList();
        Assert.Equal(6, blocks.Count);
        Assert.Contains(blocks, s => s.Protocol == "UDP" && s.RemotePorts == "53" && s.RemoteAddresses == Range);
        Assert.Contains(blocks, s => s.Protocol == "TCP" && s.RemotePorts == "53" && s.RemoteAddresses == Range);
        Assert.Contains(blocks, s => s.Protocol == "TCP" && s.RemotePorts == "853" && s.RemoteAddresses == Range);
        Assert.Contains(blocks, s => s.Protocol == "UDP" && s.RemotePorts == "53" && s.RemoteAddresses == "2000::/3");
        Assert.Contains(blocks, s => s.Protocol == "TCP" && s.RemotePorts == "53" && s.RemoteAddresses == "2000::/3");
        Assert.Contains(blocks, s => s.Protocol == "TCP" && s.RemotePorts == "853" && s.RemoteAddresses == "2000::/3");
    }

    [Fact]
    public void Teardown_NamesCoverEverySpecAndTheLegacyTunRules()
    {
        var specNames = FirewallManager.BuildDnsLockdownSpecs(Range).Select(s => s.Name).ToList();

        Assert.Equal(9, FirewallManager.DnsLockdownRuleNames.Length);
        Assert.All(specNames, n => Assert.Contains(n, FirewallManager.DnsLockdownRuleNames));
    }

    [Fact]
    public void NetshArguments_CarryTheSameFieldsAsTheSpec()
    {
        var spec = FirewallManager.BuildDnsLockdownSpecs(Range).First(s => s.Protocol == "TCP" && s.RemotePorts == "853" && s.RemoteAddresses == Range);

        var args = FirewallManager.NetshAddArguments(spec);

        Assert.Contains("action=block", args);
        Assert.Contains("protocol=TCP", args);
        Assert.Contains("remoteport=853", args);
        Assert.Contains("remoteip=" + Range, args);
        Assert.Contains("profile=any", args);
        Assert.Contains($"name=\"{spec.Name}\"", args);
    }

    [Fact]
    public void ResolveNameOnPath_FindsASystemExecutable_WithAndWithoutTheExtension()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PATH search");

        var withExt = ProcessImagePath.ResolveNameOnPath("cmd.exe");
        var without = ProcessImagePath.ResolveNameOnPath("cmd");

        Assert.NotNull(withExt);
        Assert.True(File.Exists(withExt));
        Assert.Equal(withExt, without, ignoreCase: true);
        Assert.Null(ProcessImagePath.ResolveNameOnPath("VPNRouter_no_such_exe_zzq.exe"));
        Assert.Null(ProcessImagePath.ResolveNameOnPath(""));
    }

    [Fact]
    public void ComStore_AddsARuleWithPortsProtocolAndAddressRange_AsSpecified()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Firewall is Windows-only");
        Assert.SkipUnless(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "needs an elevated process");

        var store = ComFirewallRuleStore.TryCreate(Serilog.Log.Logger);
        Assert.NotNull(store);
        var name = "VPNRouter_Block_SpecTest_" + Guid.NewGuid().ToString("N");
        try
        {
            // harmless: allow UDP/59999 to a documentation range
            store!.AddRule(new FirewallRuleSpec(name, Allow: true, "UDP", "59999", "203.0.113.1-203.0.113.9,198.51.100.7", "VPNRouter test"));

            var described = ((ComFirewallRuleStore)store).Describe(name);
            Assert.NotNull(described);
            Assert.Contains("action=1", described);
            Assert.Contains("protocol=17", described);
            Assert.Contains("ports=59999", described);
            Assert.Contains("203.0.113.1", described);
            Assert.Contains("198.51.100.7", described);
            Assert.Contains("enabled=True", described);
            Assert.Contains("direction=2", described);
        }
        finally
        {
            store!.Remove(new[] { name });
        }

        Assert.Null(((ComFirewallRuleStore)store).Describe(name));
    }
}
