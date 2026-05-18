// Phase 4 (Wave 18, 2026-05-18) — pin the desktop UpdateNotificationViewModel
// migration from UpdateChecker.CheckForUpdateAsync → IUpdateSource.CheckAsync.
//
// Test cases:
//   1. CheckOnStartupAsync: IUpdateSource returns non-null → IsVisible = true,
//      Message formatted with version + size.
//   2. CheckOnStartupAsync: IUpdateSource returns null → IsVisible stays false
//      (no spurious toast on up-to-date check).
//   3. CheckOnStartupAsync: IUpdateSource throws → swallowed silently
//      (background path is silent-fail by design).
//   4. CheckManually command: IUpdateSource returns non-null → CheckState
//      flips to Found + banner becomes visible.
//   5. CheckManually command: IUpdateSource returns null → CheckState flips
//      to UpToDate + banner stays hidden.
//
// Each test exercises the VM through the test-only ctor that accepts a
// FakeUpdateSource so we never hit GitHub. Tests use [AvaloniaFact] so the
// Dispatcher.UIThread.Post calls inside the VM run on the headless
// dispatcher thread.
//
// Brief: plans/phase4-iupdatesource-callers-2026-05-18.md.

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

/// <summary>
/// Tests for <see cref="UpdateNotificationViewModel"/>'s IUpdateSource
/// wiring. Drives the VM via the test ctor that accepts a
/// <see cref="FakeUpdateSource"/> so we don't hit the network or rely on
/// the desktop <see cref="UpdateChecker"/>'s <c>PolicyHttpClient</c>.
/// </summary>
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
        // Arrange — source returns a fresh update; VM should flip the
        // banner on. CheckOnStartupAsync uses Dispatcher.UIThread.Post
        // for the UI-state mutation, so we await the dispatcher to drain
        // any queued action before asserting.
        var fake = new FakeUpdateSource { CheckResult = SampleInfo() };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        // Act
        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { /* drain queued Posts */ });

        // Assert
        Assert.Equal(1, fake.CheckCallCount);
        Assert.True(vm.IsVisible, "Banner must be visible after non-null IUpdateSource result.");
        Assert.Contains("2.34.1", vm.Message);
    }

    [AvaloniaFact]
    public async Task CheckOnStartupAsync_NullSource_StaysHidden()
    {
        // Arrange — source returns null (up to date). VM must NOT flip
        // the banner.
        var fake = new FakeUpdateSource { CheckResult = null };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        // Act
        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Assert
        Assert.Equal(1, fake.CheckCallCount);
        Assert.False(vm.IsVisible, "Banner must stay hidden when IUpdateSource reports up-to-date.");
    }

    [AvaloniaFact]
    public async Task CheckOnStartupAsync_SourceThrows_SilentlySwallows()
    {
        // Arrange — background check is silent-fail; VM logs at Debug
        // level but doesn't surface the error to the user.
        var fake = new FakeUpdateSource
        {
            CheckException = new InvalidOperationException("simulated network failure"),
        };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        // Act (must not throw)
        await vm.CheckOnStartupAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Assert — VM swallowed the exception; no banner.
        Assert.Equal(1, fake.CheckCallCount);
        Assert.False(vm.IsVisible);
    }

    [AvaloniaFact]
    public async Task CheckManuallyCommand_NonNullSource_FlipsStateToFound()
    {
        // Arrange — manual check surfaces the result via CheckState +
        // banner. CheckState flips Default → Checking → Found inside the
        // command; the deferred Default reset (Task.Delay(3000) +
        // dispatcher Post) happens later and is not what this test pins.
        var fake = new FakeUpdateSource { CheckResult = SampleInfo("2.34.2") };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        // Act — invoke the [RelayCommand] generated command.
        await vm.CheckManuallyCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Assert
        Assert.Equal(1, fake.CheckCallCount);
        Assert.Equal(UpdateNotificationViewModel.UpdateCheckState.Found, vm.CheckState);
        Assert.True(vm.IsVisible);
        Assert.Contains("2.34.2", vm.Message);
    }

    [AvaloniaFact]
    public async Task CheckManuallyCommand_NullSource_FlipsStateToUpToDate()
    {
        // Arrange — no update available → CheckState lands on UpToDate
        // (not Found), banner stays hidden.
        var fake = new FakeUpdateSource { CheckResult = null };
        var vm = new UpdateNotificationViewModel(DefaultSettings(), DefaultLogger(), fake);

        // Act
        await vm.CheckManuallyCommand.ExecuteAsync(null);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Assert
        Assert.Equal(1, fake.CheckCallCount);
        Assert.Equal(UpdateNotificationViewModel.UpdateCheckState.UpToDate, vm.CheckState);
        Assert.False(vm.IsVisible);
    }
}
