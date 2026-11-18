using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class MainServiceRegistrationTests
{
    [Theory]
    [InlineData("VPNRouter")]
    [InlineData("VPNRouterBusiness")]
    public void ResolvesTheOwnedRunningAliasAndPublishesOnlyToItsRegistration(string name)
    {
        var exe = Path.Combine(Path.GetTempPath(), "relocated app", "VPNRouter.Service.exe");
        var own = new FakeRegistration(new(WindowsServiceCommand.FormatImagePath(exe), 16, 2)) { IsActive = true, Pid = Environment.ProcessId };
        var foreign = new FakeRegistration(new("\"other.exe\" --service", 16, 2)) { IsActive = true, Pid = 43 };
        IWindowsRegistration Open(string key, uint _) => key == name ? own : foreign;
        Assert.Equal(name, MainServiceRegistration.Resolve(exe, true, Open));
        var host = MainServiceRegistration.Resolve(exe, true, Open, hostPid: own.Pid);
        using var registration = MainServiceRegistration.OpenVerified(host, exe, 0, Open, hostPid: own.Pid);
        Assert.NotNull(registration);
        ServiceSettingsReceipt.Publish(registration.Handle, "expected revision", true);
        var receipt = Assert.IsType<string[]>(own.Values["VPNRouterLastSettingsApply"]);
        Assert.True(ServiceSettingsReceipt.Match(receipt, null, own.Pid, "expected revision"));
        Assert.Null(ServiceSettingsReceipt.Match(receipt, null, own.Pid + 1, "expected revision"));
        Assert.Empty(foreign.Values);
        Assert.Throws<InvalidOperationException>(() => MainServiceRegistration.OpenVerified(name, exe, 0, Open, hostPid: own.Pid + 1));
    }

    [Fact]
    public void OneActiveRegistrationWinsAndAmbiguousOwnershipBlocksMutation()
    {
        var exe = Path.Combine(Path.GetTempPath(), "owned app", "VPNRouter.Service.exe");
        var preferred = new FakeRegistration(new(WindowsServiceCommand.FormatImagePath(exe), 16, 3));
        var running = new FakeRegistration(preferred.Config) { IsActive = true, Pid = 1 };
        IWindowsRegistration Open(string name, uint _) => name == "VPNRouter" ? running : preferred;
        Assert.Equal("VPNRouter", MainServiceRegistration.Resolve(exe, true, Open));
        preferred.IsActive = true;
        Assert.Throws<InvalidOperationException>(() => MainServiceRegistration.Resolve(exe, true, Open));
        Assert.Equal("VPNRouter", MainServiceRegistration.Resolve(exe, false, Open));
        Assert.Equal(0, running.Changes + preferred.Changes);
    }
}
