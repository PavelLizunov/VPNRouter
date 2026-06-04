using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Wire-shape + crash-safety coverage for the macOS DNS-hardening orchestrator
/// (Fix #1, r2). MacDnsHardening is pure IProcessRunner orchestration (no macOS
/// APIs), so the exact networksetup/sudo command args — the part where a wrong
/// token silently breaks the user's DNS — are pinned here on the Windows build.
/// The live runtime effect (DNS actually entering the tunnel) is verified on the
/// Mac host separately.
/// </summary>
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
        // sudo (set + flush) — succeeds.
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return fake;
    }

    [Fact]
    public void Apply_sets_primary_service_dns_to_tun_gateway_via_sudo()
    {
        var fake = BuildFake("8.8.8.8\n1.1.1.1");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        // The critical command: sudo -n /usr/sbin/networksetup -setdnsservers Wi-Fi 172.19.0.1
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
        Assert.False(File.Exists(_statePath)); // sentinel cleared
    }

    [Fact]
    public void Restore_uses_empty_token_when_original_was_dhcp()
    {
        // networksetup prints this sentinel when DNS is DHCP-managed → restore to "empty".
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
        // Crash-safety: a second Apply (reconnect / post-crash) must keep the
        // TRUE original, not save the TUN address as "original" — else Restore
        // would set DNS to the dead TUN.
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        // Second apply: getdnsservers would now report the TUN address, but the
        // sentinel already exists so the original must be preserved.
        var fake2 = BuildFake("172.19.0.1");   // current DNS is now the TUN
        var sut2 = new MacDnsHardening(fake2, _statePath);
        sut2.Apply("172.19.0.1", null);

        var json = File.ReadAllText(_statePath);
        Assert.Contains("8.8.8.8", json);          // true original preserved
        Assert.DoesNotContain("172.19.0.1", json); // TUN not saved as original
    }

    [Fact]
    public void Restore_is_noop_when_no_sentinel()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Restore(null); // never applied

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
}
