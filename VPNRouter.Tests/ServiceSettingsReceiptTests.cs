using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class ServiceSettingsReceiptTests
{
    [Fact]
    public void OnlyAFreshResultForTheSavedSettingsFromTheSameManagerConfirmsApply()
    {
        var settings = new AppSettings().EnsureSane();
        var previousRevision = SettingsLoader.GetSubmittedRevision(settings);
        var oldId = Guid.NewGuid().ToString("N");
        settings.App.BlockAds = !settings.App.BlockAds;
        var adsRevision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(previousRevision, adsRevision);
        settings.App.BypassRussianTraffic = !settings.App.BypassRussianTraffic;
        var ruRevision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(adsRevision, ruRevision);
        settings.App.ConfigMode = "custom";
        settings.App.ActiveCustomConfig = "test-import";
        var revision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(ruRevision, revision);
        string[] result = [Guid.NewGuid().ToString("N"), "42", revision, "applied"];

        Assert.True(ServiceSettingsReceipt.Match(result, oldId, 42, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, result[0], 42, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, oldId, 43, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, oldId, 42, previousRevision));
        result[3] = "rejected";
        Assert.False(ServiceSettingsReceipt.Match(result, oldId, 42, revision));
    }

    [Fact]
    public void ServiceRestartRetriesTheSameRejectedFileWithCompleteRuleSets()
    {
        const string yaml = "app:\n  block_ads: true";
        var revision = SettingsLoader.GetRevision(yaml);
        string[] receipt = [Guid.NewGuid().ToString("N"), "42", revision, "rejected"];
        Assert.Equal(yaml, ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => yaml));
        Assert.Null(ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => "app: {}"));
        receipt[3] = "applied";
        Assert.Null(ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => yaml));
        var cold = new StartupContext(new AppSettings(), StartupMode.ColdStart);
        Assert.False(cold.ValidateRequiredRuleSets);
        Assert.True((cold with { RequireCompleteRuleSets = true }).ValidateRequiredRuleSets);
    }

    [Fact]
    public void SubmittedRevisionMatchesTheSavedFileReadByTheWatcher()
    {
        var temp = Directory.CreateTempSubdirectory("vpnrouter-service-settings-");
        try
        {
            var path = Path.Combine(temp.FullName, "config.yaml");
            var settings = new AppSettings().EnsureSane();
            settings.App.BlockAds = true;
            SettingsLoader.Save(settings, path);
            var persisted = File.ReadAllText(path);
            Assert.Equal(SettingsLoader.GetSubmittedRevision(settings), SettingsLoader.GetRevision(persisted));
            var candidate = Assert.IsType<AppSettings>(SettingsLoader.ParseLiveCandidate(persisted));
            Assert.True(candidate.App.BlockAds);
        }
        finally { temp.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("app: [")]
    [InlineData("app:\n  config_mode: invalid")]
    public void LiveReloadRejectsInvalidFilesWithoutRecoveringToDefaults(string yaml)
    {
        Assert.Null(SettingsLoader.ParseLiveCandidate(yaml));
    }
}
