using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

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

    private const string RouteGetOut =
        "172.19.0.1 dev VPNRouter-TUN src 172.19.0.2 uid 1000 \n    cache \n";

    private FakeProcessRunner BuildFake()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Ok("systemd 255"));
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("route"), Ok(RouteGetOut));
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("link"), Ok("[{\"linkinfo\":{\"info_kind\":\"tun\"}}]"));
        fake.OnRun(r => r.ExecutablePath == "resolvectl", Ok());
        return fake;
    }

    [Fact]
    public void Apply_pins_tun_link_dns_via_resolvectl()
    {
        var fake = BuildFake();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        var dns = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "resolvectl" && c.Arguments.Contains("dns"));
        Assert.NotNull(dns);
        Assert.Equal(new[] { "--no-ask-password", "dns", "VPNRouter-TUN", "172.19.0.1" }, dns!.Arguments.ToArray());
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
        Assert.Equal(new[] { "--no-ask-password", "domain", "VPNRouter-TUN", "~." }, domain!.Arguments.ToArray());
    }

    [Fact]
    public void Apply_empty_target_is_noop()
    {
        var fake = new FakeProcessRunner();
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("", null);

        Assert.Empty(fake.RunCalls);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Apply_waits_for_replacement_tun_without_mutating_physical_dns()
    {
        var fake = new FakeProcessRunner();
        var routes = 0;
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("route"),
            _ => Task.FromResult(Ok(++routes < 3 ? "172.19.0.2 via 192.168.0.1 dev ens18" : RouteGetOut)));
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("link"),
            r => Task.FromResult(Ok(r.Arguments.Contains("ens18")
                ? "[{\"linkinfo\":{\"info_kind\":\"ether\"}}]"
                : "[{\"linkinfo\":{\"info_kind\":\"tun\"}}]")));
        fake.OnRun(r => r.ExecutablePath == "resolvectl", Ok());
        File.WriteAllText(_statePath, "{\"Interface\":\"ens18\"}");

        new LinuxDnsHardening(fake, _statePath).Apply("172.19.0.2", null);

        Assert.Equal(3, routes);
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("dns") && c.Arguments.Contains("VPNRouter-TUN"));
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "resolvectl" && c.Arguments.Contains("ens18"));
        Assert.Contains("VPNRouter-TUN", File.ReadAllText(_statePath));
    }

    [Fact]
    public void Apply_never_pins_dns_to_a_physical_link_when_tun_does_not_arrive()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl", Ok());
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("route"), Ok("172.19.0.2 via 192.168.0.1 dev ens18"));
        fake.OnRun(r => r.ExecutablePath == "ip" && r.Arguments.Contains("link"), Ok("[{\"linkinfo\":{\"info_kind\":\"ether\"}}]"));

        new LinuxDnsHardening(fake, _statePath).Apply("172.19.0.2", null);

        Assert.Equal(20, fake.RunCalls.Count(c => c.ExecutablePath == "ip" && c.Arguments.Contains("route")));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns") || c.Arguments.Contains("domain"));
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Apply_resolvectl_unavailable_is_failopen_noop()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Fail());
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.False(File.Exists(_statePath));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns"));
    }

    [Fact]
    public void Apply_no_tun_interface_is_failopen_noop()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl" && r.Arguments[0] == "--version", Ok());
        fake.OnRun(r => r.ExecutablePath == "ip", Ok(""));
        var sut = new LinuxDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.False(File.Exists(_statePath));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns"));
    }

    [Fact]
    public void Apply_local_address_never_changes_loopback_dns()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "resolvectl", Ok());
        fake.OnRun(r => r.ExecutablePath == "ip", Ok("local 172.19.0.1 dev lo table local"));
        new LinuxDnsHardening(fake, _statePath).Apply("172.19.0.1", null);
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("dns"));
        Assert.False(File.Exists(_statePath));
    }

    [Theory]
    [InlineData("172.19.0.1/30", "172.19.0.2")]
    [InlineData("10.7.0.1/24", "10.7.0.2")]
    [InlineData("172.19.0.2/30", null)]
    [InlineData("172.19.0.1/32", null)]
    [InlineData("invalid", null)]
    [InlineData("::1/64", null)]
    public void Dns_target_is_the_nonlocal_tun_peer(string cidr, string? target)
        => Assert.Equal(target, LinuxDnsHardening.DeriveDnsTarget(cidr));

    [Theory]
    [InlineData("full", "include", false, true, "generated", true)]
    [InlineData("full", "include", false, true, "subscribe", true)]
    [InlineData("split", "include", false, true, "generated", false)]
    [InlineData("split", "include", true, true, "generated", true)]
    [InlineData("split", "exclude", false, true, "generated", true)]
    [InlineData("full", "include", false, false, "generated", false)]
    [InlineData("full", "include", false, true, "custom", false)]
    public void Resolver_steering_follows_generated_routing_policy(
        string mode, string appsMode, bool strict, bool autoRoute, string configMode, bool expected)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = mode;
        settings.App.RoutingAppsMode = appsMode;
        settings.App.StrictDns = strict;
        settings.App.ConfigMode = configMode;
        settings.Tun.AutoRoute = autoRoute;
        Assert.Equal(expected, LinuxDnsHardening.ShouldSteerResolver(settings));
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
        Assert.Equal(new[] { "--no-ask-password", "revert", "VPNRouter-TUN" }, revert!.Arguments.ToArray());
        Assert.False(File.Exists(_statePath));
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
        var fake = new FakeProcessRunner();
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
