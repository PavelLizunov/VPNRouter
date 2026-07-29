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
    [InlineData("FATAL configure tun interface: TUNSETIFF: operation not permitted", true)]
    [InlineData("fatal configure tun interface: tunsetiff: OPERATION NOT PERMITTED", true)]
    [InlineData("FATAL configure tun interface: operation not permitted", false)]
    [InlineData("FATAL TUNSETIFF: device busy", false)]
    public void RuntimeFailure_RequiresExactTunPermissionPair(string line, bool expected) =>
        Assert.Equal(expected, SingBoxManager.IsLinuxTunPermissionFailure(line));
}
