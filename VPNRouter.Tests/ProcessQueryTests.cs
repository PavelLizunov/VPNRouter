#nullable enable
using System.Diagnostics;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ProcessQueryTests
{
    private static string CurrentProcessName => Process.GetCurrentProcess().ProcessName;

    private const string MissingName = "vpnrouter-no-such-process-xyz-9f3a1c";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnyAlive_BlankName_False(string? name)
        => Assert.False(ProcessQuery.AnyAlive(name));

    [Fact]
    public void AnyAlive_MissingProcess_False()
        => Assert.False(ProcessQuery.AnyAlive(MissingName));

    [Fact]
    public void AnyAlive_CurrentProcess_True()
    {
        Assert.True(ProcessQuery.AnyAlive(CurrentProcessName));
    }

    [Fact]
    public void AnyAlive_Params_TrueIfAnyMatch()
    {
        Assert.True(ProcessQuery.AnyAlive(MissingName, CurrentProcessName));
        Assert.False(ProcessQuery.AnyAlive(MissingName, "another-missing-xyz-001"));
    }

    [Fact]
    public void AnyAlive_Params_NullOrEmpty_False()
    {
        Assert.False(ProcessQuery.AnyAlive((string[]?)null));
        Assert.False(ProcessQuery.AnyAlive(System.Array.Empty<string>()));
    }
}
