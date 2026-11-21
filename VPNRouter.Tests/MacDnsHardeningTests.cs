using System.Text.Json;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class MacDnsHardeningTests
{
    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "", TimeSpan.Zero, false);
    private static ProcessResult Fail() => new(1, "", "helper rejected the request", TimeSpan.Zero, false);

    private static string? Op(ProcessRequest call)
    {
        if (call.ExecutablePath != MacPrivilegePaths.SudoPath || call.StdinInput is null)
            return null;
        using var document = JsonDocument.Parse(call.StdinInput);
        return document.RootElement.GetProperty("op").GetString();
    }

    private const string RouteOut = "   route to: default\n    gateway: 192.168.0.1\n  interface: en0\n";
    private const string ListOrderOut =
        "(1) Wi-Fi\n(Hardware Port: Wi-Fi, Device: en0)\n\n(2) Ethernet\n(Hardware Port: Ethernet, Device: en1)\n";

    private static FakeProcessRunner BuildFake(bool applySucceeds = true)
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(call => call.ExecutablePath == "/sbin/route", Ok(RouteOut));
        fake.OnRun(call => call.ExecutablePath == "/usr/sbin/networksetup" &&
            call.Arguments[0] == "-listnetworkserviceorder", Ok(ListOrderOut));
        fake.OnRun(call => Op(call) == "dns_apply", applySucceeds ? Ok() : Fail());
        fake.OnRun(call => call.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        return fake;
    }

    [Fact]
    public void Apply_requests_one_service_and_gateway_and_flushes_after_success()
    {
        var fake = BuildFake();

        new MacDnsHardening(fake).Apply("172.19.0.1", null);

        var apply = Assert.Single(fake.RunCalls, call => Op(call) == "dns_apply");
        Assert.Equal(new[] { "-n", MacPrivilegePaths.HelperPath }, apply.Arguments);
        using var request = JsonDocument.Parse(apply.StdinInput!);
        Assert.Equal("Wi-Fi", request.RootElement.GetProperty("service").GetString());
        Assert.Equal("172.19.0.1", request.RootElement.GetProperty("server").GetString());
        Assert.DoesNotContain(fake.RunCalls, call => call.Arguments.Contains("-getdnsservers"));
        Assert.Contains(fake.RunCalls, call => Op(call) == "dns_flush");
    }

    [Fact]
    public void Failed_apply_does_not_flush_dns()
    {
        var fake = BuildFake(applySucceeds: false);

        new MacDnsHardening(fake).Apply("172.19.0.1", null);

        Assert.Contains(fake.RunCalls, call => Op(call) == "dns_apply");
        Assert.DoesNotContain(fake.RunCalls, call => Op(call) == "dns_flush");
    }

    [Fact]
    public void Blank_gateway_skips_apply()
    {
        var fake = BuildFake();

        new MacDnsHardening(fake).Apply("  ", null);

        Assert.Empty(fake.RunCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_uses_helper_owned_state_for_normal_and_stranded_sessions(bool stranded)
    {
        var fake = BuildFake();
        var hardening = new MacDnsHardening(fake);

        if (stranded)
            hardening.RestoreStrandedIfAny(null);
        else
            hardening.Restore(null);

        var restore = Assert.Single(fake.RunCalls);
        Assert.Equal("dns_restore", Op(restore));
        using var request = JsonDocument.Parse(restore.StdinInput!);
        Assert.Equal(new[] { "version", "op" },
            request.RootElement.EnumerateObject().Select(property => property.Name));
    }
}
