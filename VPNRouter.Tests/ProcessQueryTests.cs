#nullable enable
using System.Diagnostics;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ProcessQueryTests
{
    private static string CurrentProcessName => Process.GetCurrentProcess().ProcessName;

    private const string MissingName = "vpnrouter-no-such-process-xyz-9f3a1c";

    [Fact]
    public void AnyAlive_CurrentProcess_True()
    {
        Assert.True(ProcessQuery.AnyAlive(CurrentProcessName));
    }
}
