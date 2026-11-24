#nullable enable

using System;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionRollbackHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private const string CurrentVersion = "2.51.1-r1";
    private const string CurrentBuild = "current:0123456789abcdef";
    private const string PreviousBuild = "previous:fedcba9876543210";
    private const string PreviousVersion = "2.50.5";
    private const string Config = "config-a";

    private static ConnectionRollbackHistory History(
        string path,
        string build = CurrentBuild,
        string version = CurrentVersion,
        DateTimeOffset? now = null) =>
        new(path, () => now ?? Now, buildId: build, currentVersion: version);

    private static ConnectionAttemptAssessment Technical(string detail = "phase-a-timeout") =>
        new(ConnectionFailureCategory.Technical, detail);

    private static string NewPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ConnectionRollbackHistory.FileName);
    }

    private static void SeedPreviousSuccess(
        string path,
        string build = PreviousBuild,
        string version = PreviousVersion)
    {
        var previous = History(path, build, version, Now.AddDays(-2));
        previous.ObserveSuccess(Config);
    }

    [Fact]
    public void BelowThreshold_DoesNotOffer()
    {
        var history = History(NewPath());

        var first = history.ObserveAttempt("a1", Config, Technical());
        var second = history.ObserveAttempt("a2", Config, Technical());

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(RollbackAssistKind.None, second.Kind);
        Assert.False(second.ShouldShow);
    }

    [Fact]
    public void ThreeTechnicalFailures_WithLocalPreviousSuccess_OffersThatBuild()
    {
        var path = NewPath();
        SeedPreviousSuccess(path);
        var history = History(path);

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical());

        Assert.Equal(RollbackAssistKind.OfferRollback, decision.Kind);
        Assert.Equal(PreviousVersion, decision.PreviousWorkingVersion);
        Assert.Equal(PreviousBuild, decision.PreviousWorkingBuildId);
        Assert.Equal(RollbackConfidence.High, decision.Confidence);
        Assert.Equal("previous-build-verified", decision.ReasonCode);
        Assert.True(decision.ShouldShow);
    }

    [Fact]
    public void ThreeTechnicalFailures_WithSignedManifestButCleanInstall_DoesNotOffer()
    {
        var history = History(NewPath());

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.Null(decision.PreviousWorkingVersion);
        Assert.Equal("insufficient-history", decision.ReasonCode);
    }

    [Fact]
    public void MatchingPreviousInstall_RequiresSeparateAttestationBeforeOffering()
    {
        var history = History(NewPath());
        history.RememberPreviousInstalledVersion(PreviousVersion, "update-receipt");

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.NeedsPriorVersionAttestation, decision.Kind);
        Assert.Equal(PreviousVersion, decision.PreviousWorkingVersion);
        Assert.Equal(PreviousVersion, decision.PreviousInstalledVersion);
        Assert.Equal("attestation-required", decision.ReasonCode);
        Assert.Equal(RollbackConfidence.None, decision.Confidence);
    }

    [Fact]
    public void AttestedPreviousInstall_OffersSignedTargetWithMediumConfidence()
    {
        var history = History(NewPath());
        history.RememberPreviousInstalledVersion(PreviousVersion, "update-receipt");
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        history.RecordPriorVersionAttestation(PreviousVersion, Config, worked: true);
        var decision = history.Evaluate(Config, signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.OfferRollback, decision.Kind);
        Assert.Equal(PreviousVersion, decision.PreviousWorkingVersion);
        Assert.Equal(RollbackConfidence.Medium, decision.Confidence);
        Assert.Equal("attested-previous-install", decision.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void NegativeOrUnknownAttestation_GoesToLocalDiagnostics(bool? worked)
    {
        var history = History(NewPath());
        history.RememberPreviousInstalledVersion(PreviousVersion, "update-receipt");
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        history.RecordPriorVersionAttestation(PreviousVersion, Config, worked);
        var decision = history.Evaluate(Config, signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.Equal("attestation-negative", decision.ReasonCode);
    }

    [Fact]
    public void OtherPreviousInstall_DoesNotRaiseConfidenceForTheSignedTarget()
    {
        var history = History(NewPath());
        history.RememberPreviousInstalledVersion("2.51.0-r2", "update-receipt");

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.Equal("2.51.0-r2", decision.PreviousInstalledVersion);
    }

    [Fact]
    public void LocalPreviousBuildMustMatchTheSignedTarget()
    {
        var path = NewPath();
        SeedPreviousSuccess(path, "previous-2506", "2.50.6");
        var history = History(path);

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
    }

    [Fact]
    public void LocalVerifiedSignedTarget_OffersWithoutAttestation()
    {
        var path = NewPath();
        SeedPreviousSuccess(path);
        var history = History(path);

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        Assert.Equal(RollbackAssistKind.OfferRollback, decision.Kind);
        Assert.Equal("previous-build-verified", decision.ReasonCode);
        Assert.Equal(RollbackConfidence.High, decision.Confidence);
    }

    [Fact]
    public void ThreeTechnicalFailures_WithoutEvidence_OffersOnlyLocalDiagnostics()
    {
        var history = History(NewPath());

        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical());

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.Null(decision.PreviousWorkingVersion);
        Assert.True(decision.ShouldShow);
    }

    [Theory]
    [InlineData(ConnectionFailureCategory.NetworkUnavailable)]
    [InlineData(ConnectionFailureCategory.ProviderRejected)]
    [InlineData(ConnectionFailureCategory.AuthenticationFailed)]
    [InlineData(ConnectionFailureCategory.UserCancelled)]
    [InlineData(ConnectionFailureCategory.LocalEnvironment)]
    [InlineData(ConnectionFailureCategory.Background)]
    public void ExcludedCategories_DoNotCount(ConnectionFailureCategory category)
    {
        var history = History(NewPath());
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());

        var excluded = history.ObserveAttempt("a3", Config, new ConnectionAttemptAssessment(category, "excluded"));
        var third = history.ObserveAttempt("a4", Config, Technical());

        Assert.Equal(2, excluded.ConsecutiveFailures);
        Assert.Equal(3, third.ConsecutiveFailures);
        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, third.Kind);
    }

    [Fact]
    public void DuplicateAttemptId_CountsOnce()
    {
        var history = History(NewPath());

        history.ObserveAttempt("same", Config, Technical());
        var duplicate = history.ObserveAttempt("same", Config, Technical());

        Assert.Equal(1, duplicate.ConsecutiveFailures);
    }

    [Fact]
    public void BuildChange_ResetsFailuresAndLeavesThePreviousHistoryAvailable()
    {
        var path = NewPath();
        SeedPreviousSuccess(path);
        var current = History(path);
        current.ObserveAttempt("a1", Config, Technical());
        current.ObserveAttempt("a2", Config, Technical());

        var nextBuild = History(path, "next:1111111111111111", CurrentVersion);
        var reset = nextBuild.Evaluate(Config);
        var firstOnNext = nextBuild.ObserveAttempt("a3", Config, Technical());

        Assert.Equal(0, reset.ConsecutiveFailures);
        Assert.Equal(1, firstOnNext.ConsecutiveFailures);
    }

    [Fact]
    public void ConfigChange_ResetsFailures()
    {
        var history = History(NewPath());
        history.ObserveAttempt("a1", "config-a", Technical());
        history.ObserveAttempt("a2", "config-a", Technical());

        var changed = history.ObserveAttempt("a3", "config-b", Technical());
        var next = history.ObserveAttempt("a4", "config-b", Technical());

        Assert.Equal(1, changed.ConsecutiveFailures);
        Assert.Equal(2, next.ConsecutiveFailures);
    }

    [Fact]
    public void Success_ResetsFailuresAndRecordsTheConfiguration()
    {
        var history = History(NewPath());
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());

        var success = history.ObserveSuccess(Config);
        var other = history.ObserveAttempt("a3", "other-config", Technical());

        Assert.Equal(0, success.ConsecutiveFailures);
        Assert.Equal(1, other.ConsecutiveFailures);
    }

    [Fact]
    public void Dismiss_SuppressesOnlyTheCurrentBuildAndConfiguration()
    {
        var path = NewPath();
        var history = History(path);
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        history.ObserveAttempt("a3", Config, Technical());
        history.Dismiss(Config);

        var dismissed = history.Evaluate(Config);
        Assert.True(dismissed.WasDismissed);
        Assert.False(dismissed.ShouldShow);

        var nextBuild = History(path, "next:2222222222222222", CurrentVersion);
        nextBuild.ObserveAttempt("b1", Config, Technical());
        nextBuild.ObserveAttempt("b2", Config, Technical());
        var fresh = nextBuild.ObserveAttempt("b3", Config, Technical());
        Assert.False(fresh.WasDismissed);
        Assert.True(fresh.ShouldShow);
    }

    [Fact]
    public void IncompatibleActiveConfig_SuppressesTheOfferEvenAfterAttestation()
    {
        var history = History(NewPath());
        history.RememberPreviousInstalledVersion(PreviousVersion, "update-receipt");
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);
        history.RecordPriorVersionAttestation(PreviousVersion, Config, worked: true);

        var decision = history.Evaluate(Config,
            signedRecoveryVersion: PreviousVersion,
            activeConfigIncompatibility: RollbackConfigCompatibility.ActiveDnstt);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.Equal("active-config-incompatible:" + RollbackConfigCompatibility.ActiveDnstt,
            decision.ReasonCode);
        Assert.Null(decision.PreviousWorkingVersion);
    }

    [Fact]
    public void IncompatibleActiveConfig_SuppressesALocalVerifiedOffer()
    {
        var path = NewPath();
        SeedPreviousSuccess(path);
        var history = History(path);
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        history.ObserveAttempt("a3", Config, Technical(),
            signedRecoveryVersion: PreviousVersion);

        var decision = history.ObserveAttempt("a4", Config, Technical(),
            signedRecoveryVersion: PreviousVersion,
            activeConfigIncompatibility: RollbackConfigCompatibility.ActiveVmess);

        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics, decision.Kind);
        Assert.StartsWith("active-config-incompatible:", decision.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownRegression_OffersItsTargetWithHighConfidence()
    {
        var history = History(NewPath());
        history.ObserveAttempt("a1", Config, Technical());
        history.ObserveAttempt("a2", Config, Technical());
        var decision = history.ObserveAttempt("a3", Config, Technical(),
            knownRegression: new KnownRegression(CurrentBuild, PreviousVersion, "known"));

        Assert.Equal(RollbackAssistKind.OfferRollback, decision.Kind);
        Assert.Equal(PreviousVersion, decision.PreviousWorkingVersion);
        Assert.Equal(RollbackConfidence.High, decision.Confidence);
        Assert.Equal("known-regression", decision.ReasonCode);
    }

    [Fact]
    public void Handoff_IsVerifiedByTheTargetBuildAndSuppressesAutomaticUpdates()
    {
        var path = NewPath();
        var writer = History(path);
        writer.BeginRollbackHandoff(PreviousVersion, Config);

        var target = History(path, PreviousBuild, PreviousVersion);
        Assert.True(target.ShouldSuppressAutomaticUpdates());

        var result = target.CompleteHandoff(PreviousVersion);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(PreviousVersion, result.TargetVersion);
        Assert.True(target.ShouldSuppressAutomaticUpdates());

        target.ClearRollbackChoice();
        Assert.False(target.ShouldSuppressAutomaticUpdates());
    }

    [Fact]
    public void HandoffMismatch_IsRecordedAndDoesNotKeepSuppression()
    {
        var path = NewPath();
        var writer = History(path);
        writer.BeginRollbackHandoff(PreviousVersion, Config);

        var stillCurrent = History(path);
        var result = stillCurrent.CompleteHandoff(CurrentVersion);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(CurrentVersion, result.ObservedVersion);
        Assert.False(stillCurrent.ShouldSuppressAutomaticUpdates());
    }

    [Fact]
    public void FailedHandoff_ClearsTheChoice()
    {
        var path = NewPath();
        var writer = History(path);
        writer.BeginRollbackHandoff(PreviousVersion, Config);

        var target = History(path, PreviousBuild, PreviousVersion);
        Assert.True(target.ShouldSuppressAutomaticUpdates());

        target.FailRollbackHandoff(PreviousVersion);

        Assert.False(target.ShouldSuppressAutomaticUpdates());
    }

    [Fact]
    public void ConfirmedConnectionOutsideManualAttempt_ResetsTheFailureThreshold()
    {
        var history = History(NewPath());
        for (int i = 0; i < 3; i++)
            history.ObserveAttempt(i.ToString(), "settings", new(ConnectionFailureCategory.Technical, "timeout"));
        history.ClearFailuresAfterConnection();
        Assert.Equal(0, history.Evaluate("settings").ConsecutiveFailures);
    }

    [Fact]
    public void ConfigFingerprint_ChangesWithServerIdentityAndRouting()
    {
        var first = Settings("one.example");
        var same = Settings("one.example");
        var differentServer = Settings("two.example");
        var differentRouting = Settings("one.example");
        differentRouting.App.RoutingMode = "full";

        Assert.Equal(
            ConnectionRollbackHistory.ComputeConfigFingerprint(first),
            ConnectionRollbackHistory.ComputeConfigFingerprint(same));
        Assert.NotEqual(
            ConnectionRollbackHistory.ComputeConfigFingerprint(first),
            ConnectionRollbackHistory.ComputeConfigFingerprint(differentServer));
        Assert.NotEqual(
            ConnectionRollbackHistory.ComputeConfigFingerprint(first),
            ConnectionRollbackHistory.ComputeConfigFingerprint(differentRouting));
    }

    [Fact]
    public void Fingerprint_IgnoresLocalDnsttGrantBindingsAndPreservesThemOnTheOriginal()
    {
        var settings = Settings("one.example");
        var row = Assert.Single(settings.Vless.Servers);
        row.Protocol = "dnstt";
        row.Dnstt = new DnsttProfile { Domain = "first.example.test" };
        var before = ConnectionRollbackHistory.ComputeConfigFingerprint(settings);
        Assert.NotNull(before);
        row.DnsttRefreshBinding = new string('A', 64);
        row.DnsttOriginalBinding = new string('B', 64);

        Assert.Equal(before, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
        Assert.Equal(new string('A', 64), row.DnsttRefreshBinding);
        Assert.Equal(new string('B', 64), row.DnsttOriginalBinding);
        row.Dnstt.Domain = "second.example.test";
        Assert.NotEqual(before, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
    }

    [Fact]
    public void Fingerprint_DistinguishesRuleContentDnsAndProcesses_IgnoresUiPreferences()
    {
        var settings = Settings("one.example");
        settings.App.CustomRules.Add(new CustomRule { Value = "one.example" });
        var before = ConnectionRollbackHistory.ComputeConfigFingerprint(settings);
        settings.App.Theme = "dark";
        Assert.Equal(before, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
        settings.App.CustomRules[0].Value = "two.example";
        var changed = ConnectionRollbackHistory.ComputeConfigFingerprint(settings);
        Assert.NotEqual(before, changed);
        settings.Dns.Strategy = "prefer_ipv6";
        Assert.NotEqual(changed, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
        changed = ConnectionRollbackHistory.ComputeConfigFingerprint(settings);
        settings.App.RoutingAppsInclude.Add("Browser.exe");
        Assert.NotEqual(changed, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
    }

    [Fact]
    public void Fingerprint_TracksChangesToAnActiveCustomFileAtTheSamePath()
    {
        var settings = Settings("one.example");
        settings.App.ConfigMode = "custom";
        var path = Path.GetTempFileName();
        try
        {
            settings.App.CustomConfig = path;
            File.WriteAllText(path, "{\"outbounds\":[]}");
            var before = ConnectionRollbackHistory.ComputeConfigFingerprint(settings);
            File.WriteAllText(path, "{\"outbounds\":[{\"type\":\"direct\"}]}");
            Assert.NotNull(before);
            Assert.NotEqual(before, ConnectionRollbackHistory.ComputeConfigFingerprint(settings));
        }
        finally { File.Delete(path); }
    }

    private static AppSettings Settings(string server) => new()
    {
        App = new AppConfig
        {
            ConfigMode = "generated",
            RoutingMode = "split",
            RoutingAppsMode = "include",
        },
        ActiveProfile = "simple",
        Vless = new VlessConfig
        {
            ActiveServer = "one",
            Servers =
            [
                new VlessServerEntry
                {
                    Name = "one",
                    Server = server,
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                },
            ],
        },
    };
}
