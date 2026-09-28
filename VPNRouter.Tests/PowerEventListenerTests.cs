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

    [Fact]
    public void Start_CalledTwice_IsIdempotent()
    {
        var listener = new PowerEventListener(() => { });
        listener.Start();
        listener.Start();
        listener.Dispose();
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        var listener = new PowerEventListener(() => { });
        listener.Start();
        listener.Dispose();
        listener.Dispose();
    }

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrow()
    {
        var listener = new PowerEventListener(() => { });
        listener.Dispose();
    }

    [Fact]
    public void Constructor_NullCallback_DoesNotThrow()
    {
        var listener = new PowerEventListener(() => { }, logger: null);
        Assert.NotNull(listener);
        listener.Dispose();
    }
}
