using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FirewallManagerRuleStoreTests
{
    private sealed class FakeStore : IFirewallRuleStore
    {
        public List<(string Name, string Path, bool Enabled)> Added { get; } = new();
        public List<(IReadOnlyCollection<string> Names, bool Enabled)> Toggled { get; } = new();
        public List<IReadOnlyCollection<string>> Removed { get; } = new();
        public List<string> Existing { get; } = new();
        public bool Throw { get; set; }

        public bool AddOutboundBlockRule(string name, string programPath, bool enabled, string description)
        {
            if (Throw) throw new InvalidOperationException("com down");
            Added.Add((name, programPath, enabled));
            return true;
        }

        public List<FirewallRuleSpec> Specs { get; } = new();

        public bool AddRule(FirewallRuleSpec spec)
        {
            if (Throw) throw new InvalidOperationException("com down");
            Specs.Add(spec);
            return true;
        }

        public int SetEnabled(IReadOnlyCollection<string> names, bool enabled)
        {
            if (Throw) throw new InvalidOperationException("com down");
            Toggled.Add((names.ToList(), enabled));
            return names.Count;
        }

        public int Remove(IReadOnlyCollection<string> names)
        {
            if (Throw) throw new InvalidOperationException("com down");
            Removed.Add(names.ToList());
            return names.Count;
        }

        public List<string> FindByPrefixes(IReadOnlyList<string> prefixes)
        {
            if (Throw) throw new InvalidOperationException("com down");
            return Existing.Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        }
    }

    private static string SelfExe()
    {
        using var self = Process.GetCurrentProcess();
        return self.ProcessName + ".exe";
    }

    private static FakeProcessRunner OkRunner()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(2), false));
        return fake;
    }

    [Fact]
    public void WithAStore_RulesAreAddedTogglesAndRemovedWithoutStartingNetsh()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager is Windows-only");
        var store = new FakeStore();
        var runner = OkRunner();

        using var fw = new FirewallManager(null, runner, store);
        fw.CreateBlockRules(new[] { SelfExe() });
        fw.EnableBlockRules();
        fw.DisableBlockRules();
        fw.DeleteAllRules();

        var added = Assert.Single(store.Added);
        Assert.False(added.Enabled);
        Assert.StartsWith("VPNRouter_Block_", added.Name);
        Assert.Equal(2, store.Toggled.Count);
        Assert.True(store.Toggled[0].Enabled);
        Assert.False(store.Toggled[1].Enabled);
        Assert.Equal(new[] { added.Name }, store.Toggled[0].Names);
        Assert.Equal(new[] { added.Name }, Assert.Single(store.Removed));
        Assert.DoesNotContain(runner.RunCalls, c => c.Arguments.Contains("add") || c.Arguments.Contains("set") || c.Arguments.Contains("delete"));
    }

    [Fact]
    public void WithAStore_OrphanedRulesAreFoundAndRemovedThroughIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager is Windows-only");
        var store = new FakeStore();
        store.Existing.AddRange(new[] { "VPNRouter_Block_old", "Unrelated rule", "0_VPNRouter-DnsLockdown-LoopbackAllow" });

        using var fw = new FirewallManager(null, OkRunner(), store);
        fw.CleanupOrphanedRules();

        Assert.Equal(new[] { "VPNRouter_Block_old", "0_VPNRouter-DnsLockdown-LoopbackAllow" }, Assert.Single(store.Removed));
    }

    [Fact]
    public void WhenTheStoreFails_TheManagerFallsBackToNetshAndKeepsWorking()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager is Windows-only");
        var store = new FakeStore { Throw = true };
        var runner = OkRunner();

        using var fw = new FirewallManager(null, runner, store);
        fw.CreateBlockRules(new[] { SelfExe() });
        fw.DisableBlockRules();
        fw.DeleteAllRules();

        Assert.Empty(store.Added);
        Assert.Contains(runner.RunCalls, c => c.Arguments.Contains("add") && c.Arguments.Any(a => a.StartsWith("program=", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(runner.RunCalls, c => c.Arguments.Contains("delete"));
    }

    [Fact]
    public void ComStore_AddsTogglesFindsAndRemovesARealRule()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Firewall is Windows-only");
        Assert.SkipUnless(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), "needs an elevated process");

        var store = ComFirewallRuleStore.TryCreate(Serilog.Log.Logger);
        Assert.NotNull(store);
        var name = "VPNRouter_Block_TestRule_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(store!.AddOutboundBlockRule(name, @"C:\Windows\System32\notepad.exe", enabled: false, "VPNRouter test"));
            Assert.Contains(name, store.FindByPrefixes(new[] { "VPNRouter_Block_TestRule_" }));
            Assert.Equal(1, store.SetEnabled(new[] { name }, true));
            Assert.Equal(1, store.SetEnabled(new[] { name }, false));
        }
        finally
        {
            store!.Remove(new[] { name });
        }

        Assert.DoesNotContain(name, store.FindByPrefixes(new[] { "VPNRouter_Block_TestRule_" }));
    }
}
