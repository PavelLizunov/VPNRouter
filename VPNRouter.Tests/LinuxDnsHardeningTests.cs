using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Wire-shape + crash-safety coverage for the Linux DNS-hardening orchestrator
/// (systemd-resolved). LinuxDnsHardening is pure IProcessRunner orchestration (no
/// Linux APIs), so the exact resolvectl/ip command args — where a wrong token
/// would silently break the user's DNS — are pinned here on the Windows build.
/// The live runtime effect (DNS actually entering the tunnel) is verified on a
/// Linux host separately. Fail-open is asserted: a missing resolvectl / unresolved
/// TUN degrades to "DNS not hardened", never a throw and never a sentinel.
/// </summary>
public class LinuxDnsHardeningTests : IDisposable
{
    private readonly string _statePath =
        Path.Combine(Path.GetTempPath(), "vpnrouter-linux-dns-state-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { }
    }

    private static ProcessResult Ok(string stdout = "") => new ProcessResult(0, stdout, "", TimeSpan.Zero, false);
    private static ProcessResult Fail() => new ProcessResult(1, "", "err", TimeSpan.Zero, false);

    // `ip -o route get 172.19.0.1` to the /30 TUN gateway resolves to the TUN dev.
    private const string RouteGetOut =
        "172.19.0.1 dev VPNRouter-TUN src 172.19.0.2 uid 1000 \n    cache \n";

    /// <summary>Happy path: resolvectl present, ip resolves the TUN, all mutations succeed.</summary>
    private FakeProcessRunner BuildFake()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Ok("systemd 255"));
        fake.OnRun(r => r.ExecutablePath == "ip", Ok(RouteGetOut));
        // dns / domain / flush-caches / revert all succeed (registered last; the
        // --version matcher above wins for the version probe).
        fake.OnRun(r => r.ExecutablePath == "resolvectl", Ok());
        return fake;
    }

    [Fact]
    public void Apply_pins_tun_link_dns_via_resolvectl()
    {
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        // The critical command: resolvectl dns VPNRouter-TUN 172.19.0.1
        var dns = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "resolvectl" && c.Arguments.Contains("dns"));
        Assert.NotNull(dns);
        Assert.Equal(new[] { "dns", "VPNRouter-TUN", "172.19.0.1" }, dns!.Arguments.ToArray());
    }

    [Fact]
    public void Apply_sets_default_routing_domain_on_tun_link()
    {
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        var domain = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "resolvectl" && c.Arguments.Contains("domain"));
        Assert.NotNull(domain);
        Assert.Equal(new[] { "domain", "VPNRouter-TUN", "~." }, domain!.Arguments.ToArray());
    }

    [Fact]
    public void Apply_saves_sentinel_with_interface()
    {
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.True(File.Exists(_statePath));
        Assert.Contains("VPNRouter-TUN", File.ReadAllText(_statePath));
    }

    [Fact]
    public void Apply_empty_target_is_noop()
    {
        var fake = new FakeProcessRunner(); // no matchers — any run would throw
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("", null);

        Assert.Empty(fake.RunCalls);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Apply_resolvectl_unavailable_is_failopen_noop()
    {
        var fake = new FakeProcessRunner();
        // --version fails → resolved/resolvectl unavailable → Apply returns before
        // touching ip or the dns/domain mutations.
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Fail());
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.False(File.Exists(_statePath)); // no hardening attempted
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns"));
    }

    [Fact]
    public void Apply_no_tun_interface_is_failopen_noop()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Ok());
        fake.OnRun(r => r.ExecutablePath == "ip", Ok("")); // no `dev` token → can't resolve TUN
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.False(File.Exists(_statePath));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns"));
    }

    [Fact]
    public void Restore_reverts_interface_and_deletes_sentinel()
    {
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);
        Assert.True(File.Exists(_statePath));

        sut.Restore(null);

        var revert = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "resolvectl" && c.Arguments.Contains("revert"));
        Assert.NotNull(revert);
        Assert.Equal(new[] { "revert", "VPNRouter-TUN" }, revert!.Arguments.ToArray());
        Assert.False(File.Exists(_statePath)); // cleared after a confirmed revert
    }

    [Fact]
    public void RestoreStrandedIfAny_reverts_when_sentinel_present()
    {
        File.WriteAllText(_statePath, "{\"Interface\":\"VPNRouter-TUN\"}");
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.RestoreStrandedIfAny(null);

        Assert.Contains(fake.RunCalls, c =>
            c.ExecutablePath == "resolvectl" && c.Arguments.Contains("revert"));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void RestoreStrandedIfAny_noop_when_no_sentinel()
    {
        var fake = new FakeProcessRunner(); // no matchers — any run would throw
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.RestoreStrandedIfAny(null);

        Assert.Empty(fake.RunCalls);
    }

    [Theory]
    [InlineData("172.19.0.1 dev VPNRouter-TUN src 172.19.0.2 uid 1000 \n cache", "VPNRouter-TUN")]
    [InlineData("172.19.0.1 dev tun0 src 1.2.3.4", "tun0")]
    [InlineData("", null)]
    [InlineData("blah no device token here", null)]
    public void ParseRouteGetDevice_extracts_dev(string input, string? expected)
        => Assert.Equal(expected, LinuxDnsHardening.ParseRouteGetDevice(input));
}
