#nullable enable

using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.App.ViewModels;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class UpdateNotificationViewModelTests
{
    private static UpdateSettings DefaultSettings() => new()
    {
        GitHubRepo = "PavelLizunov/VPNRouter",
        Channel = "stable",
        AutoCheck = true,
    };

    private static ILogger DefaultLogger() =>
        new LoggerConfiguration().CreateLogger();

    private static UpdateSourceInfo SampleInfo(string version = "2.34.1") => new(
        Version: version,
        ReleaseUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/tag/v{version}",
        AssetName: $"VPNRouter-v{version}-win.zip",
        DownloadUrl: $"https://example.com/VPNRouter-v{version}-win.zip",
        AssetSize: 25_000_000,
        AssetSha256: new string('a', 64),
        IsPrerelease: false,
        ReleaseNotes: $"v{version} — bug fixes");

    [AvaloniaFact]
    public async Task CheckOnStartupAsync_NonNullSource_ShowsBanner()
    {
        var fake = new FakeUpdateSource { CheckResult = SampleInfo() };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {  });

        Assert.Equal(1, fake.CheckCallCount);
        Assert.True(vm.IsVisible, "Banner must be visible after non-null IUpdateSource result.");
        Assert.Contains("2.34.1", vm.Message);
    }

    [AvaloniaFact]
    public async Task CheckOnStartupAsync_NullSource_StaysHidden()
    {
        var fake = new FakeUpdateSource { CheckResult = null };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, fake.CheckCallCount);
        Assert.False(vm.IsVisible, "Banner must stay hidden when IUpdateSource reports up-to-date.");
    }

    [AvaloniaFact]
    public async Task CheckOnStartupAsync_SourceThrows_SilentlySwallows()
    {
        var fake = new FakeUpdateSource
        {
            CheckException = new InvalidOperationException("simulated network failure"),
        };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, fake.CheckCallCount);
        Assert.False(vm.IsVisible);
    }

    [AvaloniaFact]
    public async Task CheckManuallyCommand_NonNullSource_FlipsStateToFound()
    {
        var fake = new FakeUpdateSource { CheckResult = SampleInfo("2.34.2") };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.CheckManuallyCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, fake.CheckCallCount);
        Assert.Equal(UpdateNotificationViewModel.UpdateCheckState.Found, vm.CheckState);
        Assert.True(vm.IsVisible);
        Assert.Contains("2.34.2", vm.Message);
    }

    [AvaloniaFact]
    public async Task CheckManuallyCommand_NullSource_FlipsStateToUpToDate()
    {
        var fake = new FakeUpdateSource { CheckResult = null };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.CheckManuallyCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal(1, fake.CheckCallCount);
        Assert.Equal(UpdateNotificationViewModel.UpdateCheckState.UpToDate, vm.CheckState);
        Assert.False(vm.IsVisible);
    }

    [AvaloniaFact]
    public async Task ToggleVersionHistoryCommand_StableResults_ShowsInstalledAndOlderRows()
    {
        var olderA = SampleInfo("2.49.2");
        var olderB = SampleInfo("2.49.1");
        var fake = new FakeUpdateSource
        {
            StableReleases = new[] { olderA, olderB },
        };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        await vm.ToggleVersionHistoryCommand.ExecuteAsync(null);

        Assert.True(vm.IsVersionHistoryVisible);
        Assert.Equal(1, fake.ListStableCallCount);
        Assert.Equal(3, vm.StableVersions.Count);
        Assert.True(vm.StableVersions[0].IsInstalled);
        Assert.Equal("2.49.2", vm.StableVersions[1].Version);
        Assert.False(vm.StableVersions[1].IsInstalled);
    }

    [AvaloniaFact]
    public async Task VersionHistory_OlderSelection_RequiresExplicitConfirmation()
    {
        var older = SampleInfo("2.49.2");
        var fake = new FakeUpdateSource { StableReleases = new[] { older } };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);
        await vm.ToggleVersionHistoryCommand.ExecuteAsync(null);

        vm.StableVersions[1].SelectCommand.Execute(null);

        Assert.True(vm.IsRollbackConfirmationVisible);
        Assert.Same(older, vm.SelectedRollback);
        Assert.Contains("2.49.2", vm.RollbackConfirmationText);
        Assert.Equal(0, fake.DownloadCallCount);
        Assert.Equal(0, fake.ApplyCallCount);

        vm.CancelRollbackCommand.Execute(null);
        Assert.False(vm.IsRollbackConfirmationVisible);
        Assert.Null(vm.SelectedRollback);
    }

    [AvaloniaFact]
    public async Task ConfirmRollback_UsesSelectedReleaseForDownloadAndApply_ThenExits()
    {
        var older = SampleInfo("2.49.2");
        var fake = new FakeUpdateSource { StableReleases = new[] { older } };
        int? exitCode = null;
        var vm = new UpdateNotificationViewModel(
            DefaultSettings(), DefaultLogger(), fake, code => exitCode = code);
        await vm.ToggleVersionHistoryCommand.ExecuteAsync(null);
        vm.StableVersions[1].SelectCommand.Execute(null);

        await vm.ConfirmRollbackCommand.ExecuteAsync(null);

        Assert.Same(older, fake.LastDownloadInfo);
        Assert.Same(older, fake.LastApplyInfo);
        Assert.Equal(fake.DownloadReturnPath, fake.LastApplyStagedPath);
        Assert.Equal(0, exitCode);
    }

    [AvaloniaFact]
    public async Task ConfirmRollback_ConcurrentStartupCheck_DoesNotSwapApplyMetadata()
    {
        var older = SampleInfo("2.49.2");
        var newer = SampleInfo("2.50.0");
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUpdateSource
        {
            StableReleases = new[] { older },
            CheckResult = newer,
            DownloadHandler = async _ =>
            {
                downloadStarted.SetResult();
                await releaseDownload.Task;
                return "staged-old";
            },
        };
        var vm = new UpdateNotificationViewModel(
            DefaultSettings(), DefaultLogger(), fake, _ => { });
        await vm.ToggleVersionHistoryCommand.ExecuteAsync(null);
        vm.StableVersions[1].SelectCommand.Execute(null);

        var confirm = vm.ConfirmRollbackCommand.ExecuteAsync(null);
        await downloadStarted.Task;
        await vm.CheckOnStartupAsync();
        releaseDownload.SetResult();
        await confirm;

        Assert.Same(older, fake.LastDownloadInfo);
        Assert.Same(older, fake.LastApplyInfo);
    }

    [AvaloniaFact]
    public async Task VersionHistory_OpenDuringLanguageSwitch_RecomputesMessage()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            VPNRouter.App.Localization.Strings.Lang = "en";
            var fake = new FakeUpdateSource { StableReleases = new[] { SampleInfo("2.49.2") } };
            var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);
            await vm.ToggleVersionHistoryCommand.ExecuteAsync(null);
            var english = vm.VersionHistoryMessage;

            VPNRouter.App.Localization.Strings.Lang = "ru";
            vm.NotifyLangChanged();

            Assert.NotEqual(english, vm.VersionHistoryMessage);
            Assert.Equal(VPNRouter.App.Localization.Strings.RollbackSafetyHint, vm.VersionHistoryMessage);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }
}
