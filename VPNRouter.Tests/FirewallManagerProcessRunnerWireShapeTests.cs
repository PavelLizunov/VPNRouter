using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class FirewallManagerProcessRunnerWireShapeTests
{
    private static async Task WithFakeRunnerAsync(
        FakeProcessRunner fake,
        Func<Task> body)
    {
        var previous = FirewallManager.Runner;
        FirewallManager.Runner = fake;
        try { await body(); }
        finally { FirewallManager.Runner = previous; }
    }

    private static void WithFakeRunner(FakeProcessRunner fake, Action body)
    {
        var previous = FirewallManager.Runner;
        FirewallManager.Runner = fake;
        try { body(); }
        finally { FirewallManager.Runner = previous; }
    }

    [Fact]
    public async Task EnableDnsLockdownAsync_EmitsAllowRuleWithLoopbackRemoteIp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "EnableDnsLockdownAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: "172.19.0.1/30");
        });

        Assert.NotEmpty(fake.RunCalls);
        var allow = fake.RunCalls[0];
        Assert.Equal("netsh.exe", allow.ExecutablePath);
        Assert.Contains("remoteip=127.0.0.1", allow.Arguments);
        Assert.Contains("remoteport=53", allow.Arguments);
        Assert.Contains("protocol=UDP", allow.Arguments);
        Assert.Contains("action=allow", allow.Arguments);
    }

    [Fact]
    public async Task EnableDnsLockdownAsync_BlockRulesUseComplementRemoteIp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "EnableDnsLockdownAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: "172.19.0.1/30");
        });

        Assert.Equal(7, fake.RunCalls.Count);

        var expectedRange = "remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";
        var ipv4BlockCalls = fake.RunCalls.Skip(1).Take(3).ToList();
        foreach (var call in ipv4BlockCalls)
        {
            Assert.Equal("netsh.exe", call.ExecutablePath);
            Assert.Contains("action=block", call.Arguments);
            Assert.Contains(expectedRange, call.Arguments);
        }

        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=UDP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=853"));

        var ipv6BlockCalls = fake.RunCalls.Skip(4).Take(3).ToList();
        foreach (var call in ipv6BlockCalls)
        {
            Assert.Equal("netsh.exe", call.ExecutablePath);
            Assert.Contains("action=block", call.Arguments);
            Assert.Contains("remoteip=2000::/3", call.Arguments);
        }
        Assert.Contains(ipv6BlockCalls, c =>
            c.Arguments.Contains("protocol=UDP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv6BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv6BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=853"));
    }

    [Fact]
    public async Task EnableDnsLockdownAsync_FallbackCidr_UsesBundledDefault()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "EnableDnsLockdownAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: null);
        });

        Assert.Equal(7, fake.RunCalls.Count);
        var ipv4BlockCalls = fake.RunCalls.Skip(1).Take(3).ToList();
        var expectedRange = "remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";
        foreach (var call in ipv4BlockCalls)
        {
            Assert.Contains(expectedRange, call.Arguments);
        }
        foreach (var call in fake.RunCalls.Skip(4).Take(3))
        {
            Assert.Contains("remoteip=2000::/3", call.Arguments);
        }
    }

    [Fact]
    public async Task DisableDnsLockdownAsync_DeletesAllNineRuleNames()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "DisableDnsLockdownAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.DisableDnsLockdownAsync(logger: null);
        });

        Assert.Equal(9, fake.RunCalls.Count);
        foreach (var call in fake.RunCalls)
        {
            Assert.Equal("netsh.exe", call.ExecutablePath);
            Assert.Contains("delete", call.Arguments);
            Assert.Contains("rule", call.Arguments);
            Assert.Contains(call.Arguments,
                arg => arg.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
        }

        var allArgs = fake.RunCalls.SelectMany(c => c.Arguments).ToList();
        Assert.Contains("name=VPNRouter-DnsLockdown-UDP53-v6", allArgs);
        Assert.Contains("name=VPNRouter-DnsLockdown-TCP53-v6", allArgs);
        Assert.Contains("name=VPNRouter-DnsLockdown-TCP853-v6", allArgs);
    }

    [Fact]
    public void SplitShellArgs_DescriptionWithSpaces_KeptAsSingleToken()
    {
        var argv = FirewallManager.SplitShellArgs(
            "advfirewall firewall add rule " +
            "name=\"VPNRouter_Block_Discord\" " +
            "description=\"VPNRouter block_on_vpn_fail\"");

        Assert.Equal(new[]
        {
            "advfirewall", "firewall", "add", "rule",
            "name=VPNRouter_Block_Discord",
            "description=VPNRouter block_on_vpn_fail",
        }, argv);
    }

    [Fact]
    public void SplitShellArgs_BR9RemoteIp_CommaListStaysIntact()
    {
        var argv = FirewallManager.SplitShellArgs(
            "remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255");

        Assert.Single(argv);
        Assert.Equal("remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255", argv[0]);
    }

    [Fact]
    public void SplitShellArgs_EmptyAndWhitespace_ReturnsEmptyArray()
    {
        Assert.Empty(FirewallManager.SplitShellArgs(""));
        Assert.Empty(FirewallManager.SplitShellArgs("   \t  "));
    }

    [Fact]
    public void Constructor_AcceptsCustomRunner_WiresUpInjection()
    {
        var fake = new FakeProcessRunner();

        using var withFake = new FirewallManager(logger: null, runner: fake);
        using var withDefault = new FirewallManager(logger: null, runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);
    }
}
