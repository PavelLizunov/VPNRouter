using System.ComponentModel;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretDriverOwnershipTests
{
    [Theory]
    [InlineData(@"{0}", true)]
    [InlineData(@"\??\{0}", true)]
    [InlineData(@"\\?\{0}", true)]
    [InlineData("\"{0}\"", true)]
    [InlineData("\"\\??\\{0}\"", true)]
    [InlineData("\"\\\\?\\{0}\"", true)]
    [InlineData(@"\Device\HarddiskVolume1\{0}", false)]
    [InlineData(@"{0}.old", false)]
    [InlineData("\"{0}", false)]
    [InlineData("\"{0}\" --option", false)]
    [InlineData(@"WinDivert64.sys", false)]
    public void ScmDriverImageIsOneExactFilename(string format, bool matches)
    {
        const string driver = @"D:\Program Files\VPNRouter Zapret Runtime\zapret\bin\WinDivert64.sys";
        Assert.Equal(matches, ZapretServiceOwnership.IsImage(string.Format(format, driver), driver, allowArguments: false));
    }

    [Theory]
    [InlineData("WinDivert", "edition", "WinDivert64.sys", 1, false)]
    [InlineData("WinDivert14", "edition", "WinDivert32.sys", 1, false)]
    [InlineData("WinDivert15", "other edition", "WinDivert64.sys", 1, true)]
    [InlineData("WinDivert", "edition", "WinDivert64.sys", 16, true)]
    [InlineData("WinDivert", "legacy", "WinDivert64.sys", 1, true)]
    [InlineData("WinDivert", "edition", "other.sys", 1, true)]
    public void KernelRegistrationMustBelongToThisProtectedEdition(string name, string bin, string file, int type, bool conflict)
    {
        var paths = new ZapretServiceOwnership("legacy", "edition");
        var registration = new FakeRegistration(new(@"\??\" + Path.Combine(bin, file), (uint)type, 3)) { IsActive = true };
        bool actual = ZapretActions.HasDriverConflict(paths, (service, writable) =>
        {
            Assert.False(writable);
            return service == name ? registration : null;
        });
        Assert.Equal(conflict, actual);
        Assert.True(registration.IsActive);
        Assert.Equal(0, registration.Changes);
    }

    [Theory]
    [InlineData("legacy", true)]
    [InlineData("other edition", false)]
    public void ExplicitServiceApplyOnlyDelegatesMigrationWithVerifiedDrivers(string driverBin, bool migrate)
    {
        var paths = new ZapretServiceOwnership("legacy", "protected");
        string ServiceImage(string bin) => "\"" + Path.Combine(bin, "winws.exe") + "\" --wf-tcp=443";
        var service = new FakeRegistration(new(ServiceImage(paths.LegacyBin), 16, 2)) { IsActive = true };
        var driver = new FakeRegistration(new(@"\??\" + Path.Combine(driverBin, "WinDivert64.sys"), 1, 3)) { IsActive = true };
        var result = ZapretActions.ApplyServiceStrategy(paths, ServiceImage(paths.ProtectedBin), (name, writable) =>
        {
            Assert.False(writable);
            return name switch { "zapret" => service, "WinDivert" => driver, _ => null };
        }, _ => throw new InvalidOperationException("registration must not change"));
        Assert.Equal(migrate ? ZapretActions.ServiceApply.Migrate : ZapretActions.ServiceApply.Conflict, result);
        Assert.True(service.IsActive);
        Assert.True(driver.IsActive);
        Assert.Equal(0, service.Changes);
        Assert.Equal(0, driver.Changes);
    }

    [Fact]
    public void AbsentRegistrationsPermitLaunchAndUnreadableOnesBlockIt()
    {
        var paths = new ZapretServiceOwnership("legacy", "protected");
        Assert.False(ZapretActions.HasDriverConflict(paths, (_, _) => null));
        Assert.True(ZapretActions.HasDriverConflict(paths, (_, _) => throw new Win32Exception(5)));
    }

    [Fact]
    public void OnlyIdenticalDriverAtTheProtectedNinaPathIsCompatible()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "vpnrouter-driver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nina = Directory.CreateDirectory(Path.Combine(scratch, "nina")).FullName;
            var zapret = Directory.CreateDirectory(Path.Combine(scratch, "zapret")).FullName;
            var paths = new ZapretServiceOwnership("legacy", zapret);
            var image = Path.Combine(nina, "WinDivert64.sys");
            File.WriteAllBytes(image, [1, 2, 3]);
            var installed = Path.Combine(zapret, "WinDivert64.sys");
            File.WriteAllBytes(installed, [1, 2, 3]);

            Assert.True(ZapretActions.CanShareNinaDriver(paths, nina, "WinDivert", image));
            Assert.False(ZapretActions.CanShareNinaDriver(paths, nina, "WinDivert14", image));
            Assert.False(ZapretActions.CanShareNinaDriver(paths, nina, "WinDivert", installed));
            File.WriteAllBytes(installed, [1, 3, 2]);
            Assert.False(ZapretActions.CanShareNinaDriver(paths, nina, "WinDivert", image));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    }
}
