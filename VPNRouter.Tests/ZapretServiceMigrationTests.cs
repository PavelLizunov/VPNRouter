using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretServiceMigrationTests
{
    private readonly ZapretServiceOwnership _paths = new(
        Path.Combine(Path.GetTempPath(), "legacy edition", "bin"), Path.Combine(Path.GetTempPath(), "protected edition", "bin"));
    private readonly Dictionary<string, FakeRegistration> _services = [];
    private IReadOnlyList<ZapretLegacyServices.Strategy> _catalogue = [];
    private IWindowsRegistration? Open(string name, bool writable) => _services.GetValueOrDefault(name);
    private ZapretLegacyServices.Result Migrate(string? selection = null) => ZapretLegacyServices.Migrate(_paths, Open, () => _catalogue, selection);

    private FakeRegistration Old(string? label, uint start = 2, bool active = true)
    {
        var service = new FakeRegistration(new($"\"{Path.Combine(_paths.LegacyBin, "winws.exe")}\" --previous-option", 16, start)) { IsActive = active };
        if (label != null) service.Values["zapret-discord-youtube"] = label;
        _services["zapret"] = service;
        return service;
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void MissingPackageParksServiceAndVerifiedSelectionRestoresOnlyItsNativeArguments(int mode, bool active)
    {
        var old = Old("general (ALT3)", (uint)mode, active);
        Assert.Contains("Download Zapret", Migrate().Message);
        Assert.Equal(4u, old.Config.Start);
        Assert.False(old.IsActive);
        Assert.DoesNotContain("previous", old.Config.Image);
        Assert.Contains(_paths.ProtectedBin, old.Config.Image);
        int parkedChanges = old.Changes;
        Assert.True(Migrate().SafeForReboot);
        Assert.Equal(parkedChanges, old.Changes);

        _catalogue = [new("general (ALT3)", () => ["--wf-tcp=443", "--hostlist=protected list.txt"])];
        Assert.Null(Migrate().Message);
        Assert.Equal((uint)mode, old.Config.Start);
        Assert.Equal(active, old.IsActive);
        Assert.Contains("\"--hostlist=protected list.txt\"", old.Config.Image);
        Assert.DoesNotContain("--previous-option", old.Config.Image);
        Assert.False(old.Values.ContainsKey("VPNRouterMigrationV1"));
        var changes = old.Changes;
        Assert.Null(Migrate().Message);
        Assert.Equal(changes, old.Changes);
    }

    [Fact]
    public void PreInstallSnapshotSurvivesAStopAndDisabledStartModeBeforeFilesWereReplaced()
    {
        var old = Old("general", 4, false);
        old.Values["VPNRouterPreUpgradeState"] = 18;
        _catalogue = [new("general", () => ["--wf-tcp=443"])];
        Assert.Null(Migrate().Message);
        Assert.Equal(2u, old.Config.Start);
        Assert.True(old.IsActive);
    }

    [Fact]
    public void UnlabelledSelectionCanBeRecoveredByFingerprintOrExplicitInstalledName()
    {
        var old = Old(null);
        Migrate();
        _catalogue = [new("general", () => ["--previous-option"])];
        Assert.Null(Migrate().Message);
        Assert.Equal(2u, old.Config.Start);
        old = Old("../old selection");
        _catalogue = [new("general", () => ["--wf-tcp=80"])];
        Assert.NotNull(Migrate().Message);
        Assert.NotNull(Migrate("other").Message);
        Assert.Null(Migrate("general").Message);
        Assert.Contains("--wf-tcp=80", old.Config.Image);
    }

    [Fact]
    public void ForeignRegistrationAndSharedDriverArePreservedAndBlockRestore()
    {
        var old = Old("general");
        var other = new FakeRegistration(new("other driver.sys", 1, 3)) { IsActive = true };
        _services["WinDivert"] = other;
        _catalogue = [new("general", () => ["--wf-tcp=443"])];
        Assert.Contains("conflict", Migrate().Message);
        Assert.True(other.IsActive);
        Assert.Equal(0, other.Changes);
        Assert.Equal(4u, old.Config.Start);
        _services["zapret"] = new FakeRegistration(new("foreign.exe", 16, 2)) { IsActive = true };
        Migrate();
        Assert.True(_services["zapret"].IsActive);
        Assert.Equal(0, _services["zapret"].Changes);
    }

    [Fact]
    public void RedirectsOnlyTheVerifiedLegacyDriverWithoutUnloadingIt()
    {
        var driver = new FakeRegistration(new(@"\??\" + Path.Combine(_paths.LegacyBin, "WinDivert64.sys"), 1, 4))
            { IsActive = true, OnStop = () => throw new InvalidOperationException("Shared driver must stay loaded") };
        driver.Values["VPNRouterPreUpgradeState"] = 18;
        _services["WinDivert"] = driver;
        Migrate();
        Assert.Equal(Path.Combine(_paths.ProtectedBin, "WinDivert64.sys"), driver.Config.Image);
        Assert.Equal(3u, driver.Config.Start);
        Assert.True(driver.IsActive);
    }

    [Fact]
    public void MatchingServiceNameInAnotherEditionOrWrongServiceTypeDoesNotAuthorizeRemoval()
    {
        var exe = Path.Combine(_paths.ProtectedBin, "winws.exe");
        foreach (var invalid in new[] { new ServiceConfig("\"" + exe + "\"", 1, 2),
                     new("\"" + exe + ".old\"", 16, 2), new("\"other-edition/bin/winws.exe\"", 16, 2), new("", 16, 2) })
        {
            var foreign = new FakeRegistration(invalid) { IsActive = true, OnDelete = () => throw new Exception("foreign delete") };
            _services["zapret"] = foreign;
            Assert.Equal(CleanupOutcome.Skipped, _paths.Remove("zapret", Open, false).Outcome);
            Assert.True(foreign.IsActive);
        }
    }
}
