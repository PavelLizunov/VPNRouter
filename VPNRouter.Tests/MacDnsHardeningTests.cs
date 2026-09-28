using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class MacDnsHardeningTests : IDisposable
{
    private readonly string _statePath =
        Path.Combine(Path.GetTempPath(), "vpnrouter-dns-state-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { }
    }

    private static ProcessResult Ok(string stdout = "") =>
        new ProcessResult(0, stdout, "", TimeSpan.Zero, false);

    private const string RouteOut = "   route to: default\n    gateway: 192.168.0.1\n  interface: en0\n";
    private const string ListOrderOut =
        "(1) Wi-Fi\n(Hardware Port: Wi-Fi, Device: en0)\n\n(2) Ethernet\n(Hardware Port: Ethernet, Device: en1)\n";

    private FakeProcessRunner BuildFake(string getDnsOut)
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/sbin/route", Ok(RouteOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/sbin/networksetup" && r.Arguments[0] == "-listnetworkserviceorder",
            Ok(ListOrderOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/sbin/networksetup" && r.Arguments[0] == "-getdnsservers",
            Ok(getDnsOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return fake;
    }

    [Fact]
    public void Apply_sets_primary_service_dns_to_tun_gateway_via_sudo()
    {
        var fake = BuildFake("8.8.8.8\n1.1.1.1");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        var set = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "/usr/bin/sudo" &&
            c.Arguments.Contains("-setdnsservers"));
        Assert.NotNull(set);
        Assert.Equal(new[] { "-n", "/usr/sbin/networksetup", "-setdnsservers", "Wi-Fi", "172.19.0.1" },
            set!.Arguments.ToArray());
    }

    [Fact]
    public void Apply_saves_original_resolver_to_sentinel()
    {
        var fake = BuildFake("8.8.8.8\n1.1.1.1");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.True(File.Exists(_statePath));
        var json = File.ReadAllText(_statePath);
        Assert.Contains("Wi-Fi", json);
        Assert.Contains("8.8.8.8", json);
        Assert.Contains("1.1.1.1", json);
    }

    [Fact]
    public void Apply_flushes_dns_cache()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("/usr/bin/dscacheutil"));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("mDNSResponder"));
    }

    [Fact]
    public void Restore_sets_dns_back_to_saved_original()
    {
        var fake = BuildFake("8.8.8.8\n1.1.1.1");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        sut.Restore(null);

        var restore = fake.RunCalls.Last(c =>
            c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-setdnsservers"));
        Assert.Equal(new[] { "-n", "/usr/sbin/networksetup", "-setdnsservers", "Wi-Fi", "8.8.8.8", "1.1.1.1" },
            restore.Arguments.ToArray());
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Restore_uses_empty_token_when_original_was_dhcp()
    {
        var fake = BuildFake("There aren't any DNS Servers set on Wi-Fi.");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        sut.Restore(null);

        var restore = fake.RunCalls.Last(c =>
            c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-setdnsservers"));
        Assert.Equal(new[] { "-n", "/usr/sbin/networksetup", "-setdnsservers", "Wi-Fi", "empty" },
            restore.Arguments.ToArray());
    }

    [Fact]
    public void Reapply_does_not_overwrite_saved_original_with_tun_address()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        var fake2 = BuildFake("172.19.0.1");
        var sut2 = new MacDnsHardening(fake2, _statePath);
        sut2.Apply("172.19.0.1", null);

        var json = File.ReadAllText(_statePath);
        Assert.Contains("8.8.8.8", json);
        Assert.DoesNotContain("172.19.0.1", json);
    }

    [Fact]
    public void Restore_is_noop_when_no_sentinel()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Restore(null);

        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-setdnsservers"));
    }

    [Fact]
    public void Apply_with_blank_target_is_noop()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("  ", null);

        Assert.Empty(fake.RunCalls);
        Assert.False(File.Exists(_statePath));
    }

    private static ProcessResult Fail(string stderr = "sudo: a password is required") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private FakeProcessRunner BuildFakeFailingSet(string getDnsOut)
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/sbin/route", Ok(RouteOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/sbin/networksetup" && r.Arguments[0] == "-listnetworkserviceorder",
            Ok(ListOrderOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/sbin/networksetup" && r.Arguments[0] == "-getdnsservers",
            Ok(getDnsOut));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-setdnsservers"), Fail());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return fake;
    }

    [Fact]
    public void Apply_when_setdnsservers_fails_keeps_state_and_does_not_flush()
    {
        var fake = BuildFakeFailingSet("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.True(File.Exists(_statePath));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/usr/bin/dscacheutil"));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("mDNSResponder"));
    }

    [Fact]
    public void Restore_when_setdnsservers_fails_keeps_sentinel_for_retry()
    {
        new MacDnsHardening(BuildFake("8.8.8.8\n1.1.1.1"), _statePath).Apply("172.19.0.1", null);
        Assert.True(File.Exists(_statePath));

        new MacDnsHardening(BuildFakeFailingSet("8.8.8.8"), _statePath).Restore(null);

        Assert.True(File.Exists(_statePath));
    }

    [Fact]
    public void RestoreStranded_heals_after_a_prior_failed_restore()
    {
        new MacDnsHardening(BuildFake("8.8.8.8"), _statePath).Apply("172.19.0.1", null);
        new MacDnsHardening(BuildFakeFailingSet("8.8.8.8"), _statePath).Restore(null);
        Assert.True(File.Exists(_statePath));

        new MacDnsHardening(BuildFake("8.8.8.8"), _statePath).RestoreStrandedIfAny(null);
        Assert.False(File.Exists(_statePath));
    }
}
