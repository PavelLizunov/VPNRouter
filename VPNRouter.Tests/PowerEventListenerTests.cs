using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class PowerEventListenerTests
{
    [Fact]
    public void Start_NonWindows_NoOp()
    {
        var fired = false;
        var listener = new PowerEventListener(() => fired = true);
        listener.Start();
        Assert.False(fired);
        listener.Dispose();
    }
}
