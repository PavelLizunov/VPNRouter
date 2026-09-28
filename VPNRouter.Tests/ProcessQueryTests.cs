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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CountAlive_BlankName_Zero(string? name)
        => Assert.Equal(0, ProcessQuery.CountAlive(name));

    [Fact]
    public void AnyAlive_MissingProcess_False()
        => Assert.False(ProcessQuery.AnyAlive(MissingName));

    [Fact]
    public void CountAlive_MissingProcess_Zero()
        => Assert.Equal(0, ProcessQuery.CountAlive(MissingName));

    [Fact]
    public void AnyAlive_CurrentProcess_True()
    {
        Assert.True(ProcessQuery.AnyAlive(CurrentProcessName));
    }

    [Fact]
    public void CountAlive_CurrentProcess_AtLeastOne()
        => Assert.True(ProcessQuery.CountAlive(CurrentProcessName) >= 1);

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

    [Fact]
    public void AnyAlive_RepeatedCalls_StableNoThrow()
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        for (int i = 0; i < 500; i++)
        {
            Assert.True(ProcessQuery.AnyAlive(CurrentProcessName));
            Assert.False(ProcessQuery.AnyAlive(MissingName));
            Assert.Equal(0, ProcessQuery.CountAlive(MissingName));
        }
    }
}
