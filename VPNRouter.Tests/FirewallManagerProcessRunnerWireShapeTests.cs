using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 3+ (2026-05-21) IProcessRunner adoption pin for
/// <see cref="FirewallManager"/>. After the netsh callsites moved off
/// direct <c>Process.Start</c> onto the <see cref="IProcessRunner"/>
/// seam, the per-call argv shape became the new invariant the tests
/// must pin — netsh's parser is sensitive to <c>key=value</c> token
/// boundaries and the BR-9 fix lives entirely in the
/// <c>remoteip=&lt;complement&gt;</c> argument shape.
///
/// <para>These tests swap a <c>FakeProcessRunner</c> into the static
/// <see cref="FirewallManager.Runner"/> seam, exercise the public
/// surface, and assert the captured <c>RunCalls</c> shape. They DO
/// NOT spawn real netsh — fast (sub-100ms) + deterministic across CI
/// environments without elevated firewall access.</para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public class FirewallManagerProcessRunnerWireShapeTests
{
    /// <summary>
    /// Helper: swap in a FakeProcessRunner for the duration of a single
    /// test, restoring the production default in a try/finally so other
    /// tests aren't poisoned.
    /// </summary>
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
        // Windows-only: EnableDnsLockdownAsync early-returns on non-Windows
        // (see [SupportedOSPlatform("windows")] + OperatingSystem.IsWindows()
        // guard in FirewallManager.cs). The test exercises the netsh wire
        // shape which is meaningless on Linux/macOS. Linux CI was failing
        // this since 1242b9e — gate clearly per OS.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "EnableDnsLockdownAsync is Windows-only (netsh)");

        // BR-9 Wave 39: the first rule installed is the allow rule scoped
        // to 127.0.0.1 so local DNS proxies on loopback keep working.
        // remoteip=127.0.0.1 must appear in the argv, and protocol must
        // be UDP/53.
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: "172.19.0.1/30");
        });

        // Allow rule is the first call in EnableDnsLockdownAsync.
        Assert.NotEmpty(fake.RunCalls);
        var allow = fake.RunCalls[0];
        Assert.Equal("netsh.exe", allow.ExecutablePath);
        // The original wire shape (pre-Phase-3+) emitted
        // `remoteip=127.0.0.1 remoteport=53 protocol=UDP`. Confirm those
        // three tokens are present in the post-split argv.
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

        // BR-9 r17: the 3 block rules (UDP/53, TCP/53, TCP/853) MUST scope
        // their remoteip to the complement-of-TUN range so they never
        // match TUN-bound DNS traffic. This pins the
        // ComputeBlockExclusionRange output reaching the netsh argv.
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: "172.19.0.1/30");
        });

        // v2.40.0-r10 #6: 7 calls total — allow rule (call 0), 3 IPv4 block
        // rules (1-3), 3 IPv6 block rules (4-6).
        Assert.Equal(7, fake.RunCalls.Count);

        // IPv4 block rules (calls 1-3) MUST carry the complement-of-/30 range.
        // Expected shape `remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255`.
        var expectedRange = "remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";
        var ipv4BlockCalls = fake.RunCalls.Skip(1).Take(3).ToList();
        foreach (var call in ipv4BlockCalls)
        {
            Assert.Equal("netsh.exe", call.ExecutablePath);
            Assert.Contains("action=block", call.Arguments);
            Assert.Contains(expectedRange, call.Arguments);
        }

        // Three distinct IPv4 block targets: UDP/53, TCP/53, TCP/853.
        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=UDP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=53"));
        Assert.Contains(ipv4BlockCalls, c =>
            c.Arguments.Contains("protocol=TCP") && c.Arguments.Contains("remoteport=853"));

        // v2.40.0-r10 #6: three parallel IPv6 block rules (calls 4-6) scoped
        // to public global-unicast (2000::/3) — close the IPv6 DNS race the
        // v4 rules miss. No TUN exclusion (shipping TUN is IPv4-only).
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

        // tunCidr null/empty must fall through to the bundled default
        // 172.19.0.0/30 — same complement range as the explicit
        // 172.19.0.1/30 input.
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            await FirewallManager.EnableDnsLockdownAsync(
                logger: null, tunCidr: null);
        });

        // v2.40.0-r10 #6: 7 calls (allow + 3 IPv4 + 3 IPv6). The bundled-
        // default complement range applies to the IPv4 blocks (calls 1-3).
        Assert.Equal(7, fake.RunCalls.Count);
        var ipv4BlockCalls = fake.RunCalls.Skip(1).Take(3).ToList();
        var expectedRange = "remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";
        foreach (var call in ipv4BlockCalls)
        {
            Assert.Contains(expectedRange, call.Arguments);
        }
        // IPv6 blocks (calls 4-6) carry the fixed public-unicast scope.
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

        // r17 Disable path tears down: the canonical 4 (allow + 3 blocks)
        // PLUS the legacy r12 TUN-allow rules (deleted on best-effort
        // basis for users upgrading from r12..r16) PLUS the r10 #6 IPv6
        // block rules (3). Total = 9 delete calls; each MUST be a
        // `delete rule name=<rule>` shape.
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
            // The split shell-arg helper drops the surrounding quotes
            // around the rule name: token = `name=0_VPNRouter-DnsLockdown-LoopbackAllow`.
            Assert.Contains(call.Arguments,
                arg => arg.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
        }

        // v2.40.0-r10 #6: the three IPv6 rule names must be among the deletes
        // so a disable/cleanup fully tears them down (they also share the
        // VPNRouter-DnsLockdown- prefix, so CleanupOrphanedRules covers them).
        var allArgs = fake.RunCalls.SelectMany(c => c.Arguments).ToList();
        Assert.Contains("name=VPNRouter-DnsLockdown-UDP53-v6", allArgs);
        Assert.Contains("name=VPNRouter-DnsLockdown-TCP53-v6", allArgs);
        Assert.Contains("name=VPNRouter-DnsLockdown-TCP853-v6", allArgs);
    }

    [Fact]
    public void SplitShellArgs_DescriptionWithSpaces_KeptAsSingleToken()
    {
        // Critical wire-shape pin: pre-Phase-3+ the shell-string
        // `description="VPNRouter block_on_vpn_fail"` ended up reaching
        // netsh as a single argv element `description=VPNRouter
        // block_on_vpn_fail` (CommandLineToArgvW honoured the quotes,
        // stripped them, kept the space inside the value).
        //
        // The Phase 3+ helper must preserve this: token = bare value
        // with embedded space, no surrounding quotes. .NET's
        // ProcessStartInfo.ArgumentList re-quotes when it builds the
        // command line for CreateProcess, so the wire-format reaching
        // netsh stays byte-equivalent.
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
        // BR-9 emits `remoteip=0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255`
        // — no whitespace inside the value, so even a naive whitespace
        // split would have worked. The quote-aware split must STILL
        // pass it through as one token.
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
        // The new ctor signature accepts an optional IProcessRunner so
        // tests can inject FakeProcessRunner without going through the
        // static Runner property. Pin that the ctor doesn't ignore the
        // argument and that null falls back to the static default.
        var fake = new FakeProcessRunner();

        // Smoke: ctor doesn't throw on either path.
        using var withFake = new FirewallManager(logger: null, runner: fake);
        using var withDefault = new FirewallManager(logger: null, runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);
    }
}
