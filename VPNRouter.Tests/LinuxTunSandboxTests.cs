using System.Text.Json.Nodes;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class LinuxTunSandboxTests
{
    [Theory]
    [InlineData("Name:\ttest\nNoNewPrivs:\t1\n", true)]
    [InlineData("NoNewPrivs:\t0\n", false)]
    [InlineData("NoNewPrivs:\tbogus\n", false)]
    public void NoNewPrivileges_IsParsed(string status, bool expected) =>
        Assert.Equal(expected, LinuxRuntimeEnvironment.HasNoNewPrivileges(status));

    [Theory]
    [InlineData("0 0 4294967295\n", false)]
    [InlineData("0 1000 1\n", true)]
    [InlineData("0 0 1\n1 1000 65536\n", true)]
    [InlineData("not a map\n", false)]
    public void UserNamespace_IsDetected(string uidMap, bool expected) =>
        Assert.Equal(expected, LinuxRuntimeEnvironment.IsNonInitialUserNamespace(uidMap));

    [Fact]
    public void PkexecResolver_PrefersStandardThenNixOsWrapper()
    {
        Assert.Equal(
            LinuxRuntimeEnvironment.StandardPkexecPath,
            LinuxRuntimeEnvironment.ResolvePkexec(_ => true));
        Assert.Equal(
            LinuxRuntimeEnvironment.NixOsPkexecPath,
            LinuxRuntimeEnvironment.ResolvePkexec(
                path => path == LinuxRuntimeEnvironment.NixOsPkexecPath));
        Assert.Null(LinuxRuntimeEnvironment.ResolvePkexec(_ => false));
    }

    [Theory]
    [InlineData("/opt/VPN Router/sing-box")]
    [InlineData("/opt/vpn=1/sing-box")]
    public void PkexecArguments_AuthorizeTheExactExecutable(string executable)
    {
        const string configPath = "/home/test user/TUN config.json";

        Assert.Equal(new[] { executable, "run", "-c", configPath },
            SingBoxManager.BuildPkexecRuntimeArguments(executable, configPath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"experimental\":{\"debug\":{\"gc_percent\":70}}}")]
    public void PkexecConfig_AddsTheGoTargetWithoutChangingExistingOptions(string json)
    {
        var expected = JsonNode.Parse(json)!.AsObject();
        var prepared = JsonNode.Parse(SingBoxManager.WithPkexecMemoryTarget(json))!.AsObject();
        var experimental = prepared["experimental"]!.AsObject();
        var debug = experimental["debug"]!.AsObject();
        Assert.Equal(402_653_184, debug["memory_limit"]!.GetValue<int>());
        debug.Remove("memory_limit");
        if (debug.Count == 0) experimental.Remove("debug");
        if (experimental.Count == 0) prepared.Remove("experimental");
        Assert.True(JsonNode.DeepEquals(expected, prepared));
    }

    [Theory]
    [InlineData("{\"experimental\":{\"debug\":{\"memory_limit\":\"768MB\"}}}")]
    [InlineData("{\"experimental\":{\"debug\":[]}}")]
    [InlineData("{")]
    public void PkexecConfig_PreservesExplicitLimitAndLeavesInvalidJsonToTheCore(string json) =>
        Assert.Equal(json, SingBoxManager.WithPkexecMemoryTarget(json));

    [Theory]
    [InlineData("FATAL configure tun interface: TUNSETIFF: operation not permitted", true)]
    [InlineData("fatal configure tun interface: tunsetiff: OPERATION NOT PERMITTED", true)]
    [InlineData("FATAL configure tun interface: operation not permitted", false)]
    [InlineData("FATAL TUNSETIFF: device busy", false)]
    public void RuntimeFailure_RequiresExactTunPermissionPair(string line, bool expected) =>
        Assert.Equal(expected, SingBoxManager.IsLinuxTunPermissionFailure(line));
}
