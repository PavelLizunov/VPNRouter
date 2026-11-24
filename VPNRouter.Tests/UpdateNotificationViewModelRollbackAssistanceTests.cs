#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class UpdateNotificationViewModelRollbackAssistanceTests
{
    private const string TargetVersion = "2.50.5";

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static UpdateSettings Settings() => new()
    {
        GitHubRepo = "PavelLizunov/VPNRouter",
        Channel = "stable",
        AutoCheck = true,
    };

    private static ILogger Logger() => new LoggerConfiguration().CreateLogger();

    private static string HistoryPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-rollback-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ConnectionRollbackHistory.FileName);
    }

    private static ConnectionRollbackHistory History(
        string path,
        string build = "current:0123456789abcdef",
        string version = "2.51.1-r1") =>
        new(path, () => Now, buildId: build, currentVersion: version);

    private static UpdateSourceInfo Listed(string version = TargetVersion) => new(
        Version: version,
        ReleaseUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/tag/v{version}",
        AssetName: $"VPNRouter-v{version}-win.zip",
        DownloadUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/download/v{version}/VPNRouter-v{version}-win.zip",
        AssetSize: 72_127_549,
        AssetSha256: new string('a', 64),
        IsPrerelease: false,
        ReleaseNotes: string.Empty);

    private static RollbackAssistDecision ThreeFailures(
        ConnectionRollbackHistory history,
        string config = "config-a")
    {
        history.ObserveAttempt("a1", config, new(ConnectionFailureCategory.Technical, "one"));
        history.ObserveAttempt("a2", config, new(ConnectionFailureCategory.Technical, "two"));
        return history.ObserveAttempt("a3", config,
            new(ConnectionFailureCategory.Technical, "three"),
            signedRecoveryVersion: TargetVersion);
    }

    private static RollbackAssistDecision OfferedDecision(
        ConnectionRollbackHistory history,
        string config = "config-a")
    {
        history.RememberPreviousInstalledVersion(TargetVersion, "update-receipt");
        ThreeFailures(history, config);
        history.RecordPriorVersionAttestation(TargetVersion, config, worked: true);
        return history.Evaluate(config, signedRecoveryVersion: TargetVersion);
    }

    private static UpdateNotificationViewModel ViewModel(
        FakeUpdateSource source,
        ConnectionRollbackHistory history,
        Action<int>? exit = null)
    {
        var vm = new UpdateNotificationViewModel(Settings(), Logger(), source, exit, history)
        {
            IsRollbackHostSupported = () => true,
            MatchesRollbackConfig = fingerprint => fingerprint == "config-a",
            VerifyStagedManifest = (_, _) => true,
        };
        return vm;
    }

    [AvaloniaFact]
    public void Suggestion_RendersOnlyForTheCurrentDismissibleDecision()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var vm = ViewModel(new FakeUpdateSource(), history);

        vm.ShowConnectionRollbackSuggestion(decision);

        Assert.True(vm.IsRollbackAssistanceVisible);
        Assert.False(vm.IsRollbackAssistanceDiagnosticsOnly);
        Assert.Contains("3", vm.RollbackAssistanceBody);
        Assert.Contains(TargetVersion, vm.RollbackAssistanceBody);
        Assert.Equal(RollbackConfidence.Medium, decision.Confidence);
        Assert.Equal(VPNRouter.Core.Localization.Strings.RollbackAssistanceConfidenceMedium,
            vm.RollbackAssistanceConfidenceText);

        vm.DismissRollbackAssistanceCommand.Execute(null);

        Assert.False(vm.IsRollbackAssistanceVisible);
        Assert.True(history.Evaluate("config-a", signedRecoveryVersion: TargetVersion).WasDismissed);
    }

    [AvaloniaFact]
    public async Task Attestation_IsSeparateFromTheFinalInstallConsent()
    {
        var history = History(HistoryPath());
        history.RememberPreviousInstalledVersion(TargetVersion, "update-receipt");
        var decision = ThreeFailures(history);
        var source = new FakeUpdateSource { StableReleases = new[] { Listed() } };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);

        Assert.Equal(RollbackAssistKind.NeedsPriorVersionAttestation, decision.Kind);
        Assert.Equal(VPNRouter.Core.Localization.Strings.RollbackAssistanceAttestationYes,
            vm.RollbackAssistancePrimaryText);
        Assert.Equal(VPNRouter.Core.Localization.Strings.RollbackAssistanceAttestationNoOrUnsure,
            vm.RollbackAssistanceSecondaryText);

        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        Assert.Equal(0, source.DownloadCallCount);
        Assert.False(vm.IsRollbackConfirmationVisible);
        var offered = history.Evaluate("config-a", signedRecoveryVersion: TargetVersion);
        Assert.Equal(RollbackAssistKind.OfferRollback, offered.Kind);
        Assert.Equal("attested-previous-install", offered.ReasonCode);
        Assert.Equal(RollbackConfidence.Medium, offered.Confidence);

        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SelectedRollback);
        Assert.True(vm.IsRollbackConfirmationVisible);
        Assert.Equal(0, source.DownloadCallCount);
    }

    [AvaloniaFact]
    public void NegativeAttestation_GoesToLocalDiagnosticsWithoutAnOffer()
    {
        var history = History(HistoryPath());
        history.RememberPreviousInstalledVersion(TargetVersion, "update-receipt");
        var decision = ThreeFailures(history);
        var source = new FakeUpdateSource { StableReleases = new[] { Listed() } };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);

        vm.DismissRollbackAssistanceCommand.Execute(null);

        Assert.Equal(0, source.DownloadCallCount);
        Assert.True(vm.IsRollbackAssistanceVisible);
        Assert.True(vm.IsRollbackAssistanceDiagnosticsOnly);
        Assert.Equal(RollbackAssistKind.NeedsLocalDiagnostics,
            history.Evaluate("config-a", signedRecoveryVersion: TargetVersion).Kind);
    }

    [AvaloniaFact]
    public async Task PrimaryAction_LoadsOnlyTheSignedRecoveryArtifactAndRequiresConfirmation()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var source = new FakeUpdateSource { StableReleases = new[] { Listed() } };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);

        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        Assert.Equal(0, source.DownloadCallCount);
        Assert.NotNull(vm.SelectedRollback);
        Assert.Equal(TargetVersion, vm.SelectedRollback!.Version);
        Assert.True(vm.IsRollbackConfirmationVisible);
        Assert.True(vm.IsAssistedRollbackConfirmationVisible);
        Assert.False(vm.IsHistoryRollbackConfirmationVisible);
        Assert.Contains(AppVersion.Version, vm.RollbackAssistanceConfirmationText);
        Assert.Contains(TargetVersion, vm.RollbackAssistanceConfirmationText);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingListing_RechecksProfileAndRuntimeBeforeShowingConsent(bool changedProfile)
    {
        var history = History(HistoryPath());
        var pending = new TaskCompletionSource<IReadOnlyList<UpdateSourceInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeUpdateSource { PendingStableReleases = pending.Task };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(OfferedDecision(history));
        var errorOnUi = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.RollbackAssistanceError) && vm.HasRollbackAssistanceError)
                errorOnUi = Dispatcher.UIThread.CheckAccess();
        };

        var listing = vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.IsRollbackAssistanceBusy);
        if (changedProfile)
            vm.MatchesRollbackConfig = _ => false;
        else
            vm.ActiveConfigIncompatibility = () => RollbackConfigCompatibility.LegacyTelegramAutostart;
        pending.SetResult(new[] { Listed() });
        await listing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(errorOnUi);
        Assert.Equal(changedProfile
            ? VPNRouter.Core.Localization.Strings.RollbackAssistanceExpired
            : VPNRouter.Core.Localization.Strings.RollbackAssistanceIncompatibleBody(RollbackConfigCompatibility.LegacyTelegramAutostart),
            vm.RollbackAssistanceError);
        Assert.False(vm.IsRollbackAssistanceBusy);
        Assert.False(vm.IsRollbackConfirmationVisible);
        Assert.Null(vm.SelectedRollback);
        Assert.Equal(0, source.DownloadCallCount);
    }

    [AvaloniaFact]
    public async Task Confirm_AppliesTheManifestBoundArtifactAndWritesTheHandoff()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var source = new FakeUpdateSource { StableReleases = new[] { Listed() } };
        int? exitCode = null;
        var vm = ViewModel(source, history, code => exitCode = code);
        vm.ShowConnectionRollbackSuggestion(decision);
        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        await vm.ConfirmRollbackCommand.ExecuteAsync(null);

        Assert.Equal(1, source.DownloadCallCount);
        Assert.Equal(1, source.ApplyCallCount);
        Assert.Equal(0, exitCode);
        Assert.NotNull(source.LastDownloadInfo);
        Assert.Equal($"VPNRouter-update-v{TargetVersion}-win.zip", source.LastDownloadInfo!.AssetName);
        Assert.Equal(72_127_549, source.LastDownloadInfo.AssetSize);
        Assert.Equal("d9053ea06a8abc0ef7bee09b52f83d035b662e3e838818a254f7077aae6068c2",
            source.LastDownloadInfo.AssetSha256);
        Assert.True(source.LastDownloadInfo.RequireExactSize);

        var result = history.CompleteHandoff(TargetVersion);
        Assert.NotNull(result);
        Assert.True(result!.Success);
    }

    [AvaloniaFact]
    public async Task FinalApply_RefusesWhenTheActiveConfigBecameIncompatible()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var source = new FakeUpdateSource { StableReleases = new[] { Listed() } };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);
        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        vm.ActiveConfigIncompatibility = () => RollbackConfigCompatibility.ActiveDnstt;
        await vm.ConfirmRollbackCommand.ExecuteAsync(null);

        Assert.Equal(0, source.DownloadCallCount);
        Assert.Equal(0, source.ApplyCallCount);
        Assert.Contains("DNSTT", vm.RollbackAssistanceError, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedDecisionDuringDownload_CannotReachTheInstallerOrRetryAsAnUpdate(bool changedProfile)
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeUpdateSource {
            StableReleases = new[] { Listed() },
            DownloadHandler = _ => { entered.SetResult(); return finish.Task; },
        };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);
        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);
        Assert.True(vm.IsAssistedRollbackConfirmationVisible, vm.RollbackAssistanceError);
        var ct = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(10);
        var install = vm.ConfirmRollbackCommand.ExecuteAsync(null);
        try
        {
            var first = await Task.WhenAny(entered.Task, install).WaitAsync(timeout, ct);
            Assert.Same(entered.Task, first);
            if (changedProfile)
                vm.MatchesRollbackConfig = _ => false;
            else
                vm.ClearConnectionRollbackSuggestion();
        }
        finally
        {
            finish.TrySetResult("synthetic-stage");
        }
        await install.WaitAsync(timeout, ct);
        await vm.DownloadAndApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, source.DownloadCallCount);
        Assert.Equal(0, source.ApplyCallCount);
        Assert.Null(history.CompleteHandoff(TargetVersion));
    }

    [AvaloniaFact]
    public async Task MissingCompatibleRelease_DoesNotStartADownload()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var source = new FakeUpdateSource { StableReleases = Array.Empty<UpdateSourceInfo>() };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);

        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedRollback);
        Assert.False(vm.IsRollbackConfirmationVisible);
        Assert.True(vm.HasRollbackAssistanceError);
        Assert.Equal(0, source.DownloadCallCount);
    }

    [AvaloniaFact]
    public async Task ApplyFailure_PreservesTheInstalledBuildAndClearsTheChoice()
    {
        var history = History(HistoryPath());
        var decision = OfferedDecision(history);
        var source = new FakeUpdateSource
        {
            StableReleases = new[] { Listed() },
            ApplyException = new InvalidOperationException("publisher refused"),
        };
        var vm = ViewModel(source, history);
        vm.ShowConnectionRollbackSuggestion(decision);
        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        await vm.ConfirmRollbackCommand.ExecuteAsync(null);

        Assert.Equal(1, source.ApplyCallCount);
        Assert.True(vm.RollbackAssistanceError.Length > 0);
        Assert.False(history.ShouldSuppressAutomaticUpdates());
        Assert.Null(history.CompleteHandoff(TargetVersion));
    }

    [AvaloniaFact]
    public async Task DiagnosticsOnlyDecision_UsesTheLocalExporter()
    {
        var history = History(HistoryPath());
        history.ObserveAttempt("a1", "config-a", new(ConnectionFailureCategory.Technical, "one"));
        history.ObserveAttempt("a2", "config-a", new(ConnectionFailureCategory.Technical, "two"));
        var decision = history.ObserveAttempt("a3", "config-a",
            new(ConnectionFailureCategory.Technical, "three"));
        var called = false;
        var vm = ViewModel(new FakeUpdateSource(), history);
        vm.RunLocalDiagnostics = () =>
        {
            called = true;
            return Task.CompletedTask;
        };
        vm.ShowConnectionRollbackSuggestion(decision);

        await vm.RunRollbackAssistancePrimaryCommand.ExecuteAsync(null);

        Assert.True(called);
        Assert.True(vm.IsRollbackAssistanceDiagnosticsOnly);
    }

    [AvaloniaFact]
    public async Task AutomaticUpdateCheck_IsSkippedWhileTheRollbackChoiceIsActive()
    {
        var path = HistoryPath();
        var history = History(path, version: AppVersion.Version);
        history.BeginRollbackHandoff(AppVersion.Version, "config-a");
        var source = new FakeUpdateSource { CheckResult = Listed("2.52.0") };
        var vm = ViewModel(source, history);

        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(0, source.CheckCallCount);
        Assert.True(vm.IsRollbackStatusVisible);
        Assert.Contains(AppVersion.Version, vm.RollbackStatusText);
    }

    [AvaloniaFact]
    public async Task HandoffMismatch_ShowsTheRecoveryState()
    {
        var path = HistoryPath();
        var history = History(path, version: AppVersion.Version);
        history.BeginRollbackHandoff(TargetVersion, "config-a");
        var source = new FakeUpdateSource { CheckResult = null };
        var vm = ViewModel(source, history);

        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.True(vm.IsRollbackStatusVisible);
        Assert.Contains(TargetVersion, vm.RollbackStatusText);
        Assert.Contains(AppVersion.Version, vm.RollbackStatusText);
        Assert.Equal(1, source.CheckCallCount);
    }
}
