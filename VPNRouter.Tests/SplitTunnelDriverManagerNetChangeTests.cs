using System;
using System.Threading;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class SplitTunnelDriverManagerNetChangeTests
{
    [Fact]
    public void NetworkAddressChanged_BurstAndAfterTaskCompletes_NeverThrows()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mgr = new SplitTunnelDriverManager();

        var burst = Record.Exception(() =>
        {
            for (int i = 0; i < 50; i++)
                mgr.RaiseNetworkAddressChangedForTest();
        });
        Assert.Null(burst);

        Thread.Sleep(TimeSpan.FromSeconds(2.5));
        var afterComplete = Record.Exception(() => mgr.RaiseNetworkAddressChangedForTest());
        Assert.Null(afterComplete);
    }
}
