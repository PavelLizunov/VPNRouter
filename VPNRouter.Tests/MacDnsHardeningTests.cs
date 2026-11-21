using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    private static string? Op(ProcessRequest call)
    {
        if (call.StdinInput is null) return null;
        using var document = JsonDocument.Parse(call.StdinInput);
        return document.RootElement.GetProperty("op").GetString();
    }

    private static bool IsHelperOp(ProcessRequest call, string op) =>
        call.ExecutablePath == MacPrivilegePaths.SudoPath
        && call.Arguments.SequenceEqual(new[] { "-n", MacPrivilegePaths.HelperPath })
        && Op(call) == op;

    private static JsonElement DnsRequest(ProcessRequest call)
    {
        using var document = JsonDocument.Parse(call.StdinInput!);
        return document.RootElement.Clone();
    }

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

        var set = fake.RunCalls.FirstOrDefault(c => IsHelperOp(c, "dns_set"));
        Assert.NotNull(set);
        var request = DnsRequest(set!);
        Assert.Equal("Wi-Fi", request.GetProperty("service").GetString());
        Assert.Equal(new[] { "172.19.0.1" },
            request.GetProperty("servers").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    [Fact]
    public void Apply_flushes_dns_cache()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Apply("172.19.0.1", null);

        Assert.Contains(fake.RunCalls, c => IsHelperOp(c, "dns_flush"));
    }

    [Fact]
    public void Restore_sets_dns_back_to_saved_original()
    {
        var fake = BuildFake("8.8.8.8\n1.1.1.1");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        sut.Restore(null);

        var restoreCall = fake.RunCalls.Last(c => IsHelperOp(c, "dns_set"));
        var restore = DnsRequest(restoreCall);
        Assert.Equal("Wi-Fi", restore.GetProperty("service").GetString());
        Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" },
            restore.GetProperty("servers").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Restore_uses_empty_token_when_original_was_dhcp()
    {
        var fake = BuildFake("There aren't any DNS Servers set on Wi-Fi.");
        var sut = new MacDnsHardening(fake, _statePath);
        sut.Apply("172.19.0.1", null);

        sut.Restore(null);

        var restoreCall = fake.RunCalls.Last(c => IsHelperOp(c, "dns_set"));
        var restore = DnsRequest(restoreCall);
        Assert.Equal(new[] { "empty" },
            restore.GetProperty("servers").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    [Fact]
    public void Restore_is_noop_when_no_sentinel()
    {
        var fake = BuildFake("8.8.8.8");
        var sut = new MacDnsHardening(fake, _statePath);

        sut.Restore(null);

        Assert.DoesNotContain(fake.RunCalls, c => IsHelperOp(c, "dns_set"));
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
        fake.OnRun(r => IsHelperOp(r, "dns_set"), Fail());
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
        Assert.DoesNotContain(fake.RunCalls, c => IsHelperOp(c, "dns_flush"));
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
