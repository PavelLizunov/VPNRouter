#nullable enable
using System.Diagnostics;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.40.0-r3 (audit Этап 1 P0 handle-leak sweep): pins the behaviour of the
/// shared handle-safe <see cref="ProcessQuery"/> wrappers that replaced the
/// bare <c>GetProcessesByName(...).Length</c> leak sites. The disposal itself
/// is structurally guaranteed by the <c>finally</c> in <see cref="ProcessQuery"/>;
/// these tests pin the observable contract (input guards, real positive/negative
/// cases, params overload) and a callable-stability soak as a leak proxy —
/// mirrors <c>RuntimeStatusDetectorHandleLeakTests</c>.
/// </summary>
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
        // The test host process is, by definition, alive — GetProcessesByName
        // uses the base name (no .exe), which is what ProcessName returns.
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
        // Leak proxy: 500 probes of a present + a missing name must stay callable
        // and consistent. Pre-fix each call leaked a Process handle; ProcessQuery
        // disposes the array so the soak is flat. We assert correctness + that the
        // process's own handle count doesn't run away (generous bound for noise).
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        for (int i = 0; i < 500; i++)
        {
            Assert.True(ProcessQuery.AnyAlive(CurrentProcessName));
            Assert.False(ProcessQuery.AnyAlive(MissingName));
            Assert.Equal(0, ProcessQuery.CountAlive(MissingName));
        }
        // No assertion on absolute HandleCount (flaky cross-platform); the point
        // is that 500 iterations complete without exhausting handles or throwing.
    }
}
