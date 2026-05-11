using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.2 (W-4) — regression pins for the Emergency Channel (wgturn)
/// VM surface on the Tools tab. Tests live in the App-layer harness
/// because the wired-up commands + computed properties only exist on
/// the real <see cref="MainWindowViewModel"/>. Covers:
///
/// <list type="bullet">
/// <item>Three-state visual-gate machine (install / idle / connected)
/// stays mutually exclusive across every combination of
/// <c>IsWgturnInstalled</c> + <c>IsWgturnConnected</c>.</item>
/// <item>Download command pre-condition (not in-flight).</item>
/// <item>Remove command deletes the on-disk binary stub.</item>
/// <item>Connect command surfaces the URL + VK link to the underlying
/// <see cref="VPNRouter.Core.Services.EmergencyChannel.EmergencyChannelConfig"/>
/// shape (validated via TryParse roundtrip — no live Manager spawn).</item>
/// <item>Title text picks up the version + variant when populated.</item>
/// </list>
/// </summary>
public class MainWindowViewModelWgturnTests
{
    /// <summary>
    /// Install card visible when wgturn-cli is NOT on disk. This is
    /// the first state the user sees on a fresh install.
    /// </summary>
    [AvaloniaFact]
    public void IsWgturnCardInstallVisible_TrueWhenNotInstalled()
    {
        var vm = new MainWindowViewModel();
        vm.IsWgturnInstalled = false;

        Assert.True(vm.IsWgturnCardInstallVisible);
        Assert.False(vm.IsWgturnCardIdleVisible);
        Assert.False(vm.IsWgturnCardConnectedVisible);
    }

    /// <summary>
    /// Install card hides once the user has installed the binary. The
    /// idle-state card takes over (config picker + VK link input).
    /// </summary>
    [AvaloniaFact]
    public void IsWgturnCardInstallVisible_FalseWhenInstalled()
    {
        var vm = new MainWindowViewModel();
        vm.IsWgturnInstalled = true;
        vm.IsWgturnConnected = false;

        Assert.False(vm.IsWgturnCardInstallVisible);
        Assert.True(vm.IsWgturnCardIdleVisible);
        Assert.False(vm.IsWgturnCardConnectedVisible);
    }

    /// <summary>
    /// Connected card visible iff installed AND the engine reported
    /// Connected. Three-state machine must be mutually exclusive.
    /// </summary>
    [AvaloniaFact]
    public void IsWgturnCardConnectedVisible_TrueWhenInstalledAndConnected()
    {
        var vm = new MainWindowViewModel();
        vm.IsWgturnInstalled = true;
        vm.IsWgturnConnected = true;

        Assert.False(vm.IsWgturnCardInstallVisible);
        Assert.False(vm.IsWgturnCardIdleVisible);
        Assert.True(vm.IsWgturnCardConnectedVisible);
    }

    /// <summary>
    /// All three gates evaluate to false would be impossible if the
    /// matrix is exhaustive — verify by sweeping all four
    /// (installed × connected) combinations.
    /// </summary>
    [AvaloniaFact]
    public void VisualStateGates_AreMutuallyExclusive_AcrossMatrix()
    {
        var vm = new MainWindowViewModel();
        foreach (var (installed, connected) in new[]
        {
            (false, false), (false, true), (true, false), (true, true)
        })
        {
            vm.IsWgturnInstalled = installed;
            vm.IsWgturnConnected = connected;

            // Exactly one of the three gates must be true. Connected
            // requires installed; not-installed always shows the
            // install card regardless of the connected flag.
            var flags = new[]
            {
                vm.IsWgturnCardInstallVisible,
                vm.IsWgturnCardIdleVisible,
                vm.IsWgturnCardConnectedVisible,
            };
            var trueCount = flags.Count(f => f);
            Assert.Equal(1, trueCount);
        }
    }

    /// <summary>
    /// Download command short-circuits when an in-flight download is
    /// already running. Guards against double-click-while-busy races.
    /// </summary>
    [AvaloniaFact]
    public async Task DownloadWgturnCommand_NoOps_WhenAlreadyDownloading()
    {
        var vm = new MainWindowViewModel();
        vm.IsWgturnDownloading = true;
        var before = vm.IsWgturnInstalled;

        // Should return immediately without touching IsWgturnInstalled.
        await vm.DownloadWgturnCommand.ExecuteAsync(null);

        Assert.Equal(before, vm.IsWgturnInstalled);
        // The guard left IsWgturnDownloading at its pre-call value — no
        // finally-block side-effect should clear it because we never
        // entered the try block.
        Assert.True(vm.IsWgturnDownloading);
    }

    /// <summary>
    /// Remove command deletes the on-disk binary at
    /// <see cref="AppPaths.WgturnCliExePath"/>. We seed a tiny temp file
    /// at that location, invoke the command, and assert it's gone.
    /// </summary>
    [AvaloniaFact]
    public void RemoveWgturnCommand_DeletesCliExe()
    {
        var vm = new MainWindowViewModel();

        // Seed a stub binary at the W-2 path so Remove has something
        // to delete.
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.WgturnCliExePath)!);
        File.WriteAllText(AppPaths.WgturnCliExePath, "stub for W-4 RemoveWgturnCommand test");
        Assert.True(File.Exists(AppPaths.WgturnCliExePath));

        vm.RemoveWgturnCommand.Execute(null);

        Assert.False(File.Exists(AppPaths.WgturnCliExePath));
        Assert.False(vm.IsWgturnInstalled);
        Assert.Equal(string.Empty, vm.WgturnVersion);
        Assert.Equal(string.Empty, vm.WgturnVariantLabel);
    }

    /// <summary>
    /// ConnectWgturn command persists the VK link + active config name
    /// to settings. The actual engine spawn fails (no wgturn-cli on
    /// disk in CI) but the pre-spawn persistence is what we pin here —
    /// the engine path is covered in EmergencyChannelEngine unit tests.
    /// </summary>
    [AvaloniaFact]
    public async Task ConnectWgturnCommand_PersistsUrlAndVkLink()
    {
        var vm = new MainWindowViewModel();
        var settingsField = typeof(MainWindowViewModel).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settings = (AppSettings)settingsField.GetValue(vm)!;

        // Seed a config + VK link
        var entry = new WgturnEntry
        {
            Name = "Operator-Test",
            Url = "wgturn://dGVzdA==#OperatorTest",
            AddedAt = DateTimeOffset.UtcNow,
        };
        vm.WgturnConfigs.Clear();
        vm.WgturnConfigs.Add(entry);
        vm.SelectedWgturnConfig = entry;
        vm.WgturnVkLink = "https://vk.com/call/join/abc123";

        // Make sure the wgturn binary IS missing so the engine throws
        // and we never spawn a real process — but the persistence
        // happens BEFORE the engine call.
        try { if (File.Exists(AppPaths.WgturnCliExePath)) File.Delete(AppPaths.WgturnCliExePath); } catch { }

        await vm.ConnectWgturnCommand.ExecuteAsync(null);

        Assert.Equal("https://vk.com/call/join/abc123", settings.EmergencyChannel.LastVkLink);
        Assert.Equal("Operator-Test", settings.EmergencyChannel.ActiveConfig);
    }

    /// <summary>
    /// Title text falls back to the plain card title when no version
    /// is installed (e.g. first run before download).
    /// </summary>
    [AvaloniaFact]
    public void WgturnTitleText_UsesBareTitle_WhenNotInstalled()
    {
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var vm = new MainWindowViewModel();
            vm.IsWgturnInstalled = false;
            vm.WgturnVersion = string.Empty;
            vm.WgturnVariantLabel = string.Empty;

            Assert.Equal(Strings.EmergencyChannelCardTitle, vm.WgturnTitleText);
        }
        finally { Strings.Lang = en; }
    }

    /// <summary>
    /// Title text composes the version + variant once installed.
    /// </summary>
    [AvaloniaFact]
    public void WgturnTitleText_IncludesVersionAndVariant_WhenInstalled()
    {
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var vm = new MainWindowViewModel();
            vm.IsWgturnInstalled = true;
            vm.WgturnVersion = "v0.1.0";
            vm.WgturnVariantLabel = "slim";

            var expected = Strings.EmergencyChannelCardTitleWithVersion("v0.1.0", "slim");
            Assert.Equal(expected, vm.WgturnTitleText);
        }
        finally { Strings.Lang = en; }
    }

    /// <summary>
    /// Status text walks the three branches (connecting / connected /
    /// disconnected). The connected branch interpolates the active
    /// config's name so the user sees «Connected to Operator-A» in the
    /// banner.
    /// </summary>
    [AvaloniaFact]
    public void WgturnStatusText_WalksThreeBranches()
    {
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var vm = new MainWindowViewModel();

            // Disconnected (initial)
            vm.IsWgturnConnecting = false;
            vm.IsWgturnConnected = false;
            Assert.Equal(Strings.EmergencyChannelStatusDisconnected, vm.WgturnStatusText);

            // Connecting
            vm.IsWgturnConnecting = true;
            Assert.Equal(Strings.EmergencyChannelStatusConnecting, vm.WgturnStatusText);

            // Connected (with a selected config)
            vm.IsWgturnConnecting = false;
            vm.IsWgturnConnected = true;
            vm.SelectedWgturnConfig = new WgturnEntry { Name = "Operator-A" };
            Assert.Equal(
                Strings.EmergencyChannelStatusConnectedTo("Operator-A"),
                vm.WgturnStatusText);
        }
        finally { Strings.Lang = en; }
    }

    /// <summary>
    /// Bilingual parity sanity check — RU + EN must both return
    /// non-empty strings for every label the W-4 card uses. Catches a
    /// future copy edit that drops one branch.
    /// </summary>
    [Fact]
    public void EmergencyChannelStrings_AreBilingual()
    {
        var en = Strings.Lang;
        try
        {
            foreach (var lang in new[] { "en", "ru" })
            {
                Strings.Lang = lang;
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelCardTitle));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelDescription));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelInstall));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelInstallEmbedded));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelConfigsLabel));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelAddConfig));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelVkLinkLabel));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelVkLinkHint));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelVkLinkWatermark));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelConnect));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelDisconnect));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelRemove));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelUpdate));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelOpenLog));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelDetails));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusNotInstalled));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusDisconnected));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusConnecting));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusLabel));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusConnectedTo("Op-A")));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelStatusFailed("boom")));
                Assert.False(string.IsNullOrWhiteSpace(Strings.EmergencyChannelPidLine(123)));
            }
        }
        finally { Strings.Lang = en; }
    }

    /// <summary>
    /// Tools tab third sub-tab gate. Pre-W-4 the tab strip had 2 items
    /// (Zapret, Telegram Proxy) and the VM exposed two computed flags;
    /// W-4 adds index 2 for the Emergency Channel sub-page.
    /// </summary>
    [AvaloniaFact]
    public void SelectedToolIndex_DrivesThreeSubTabFlags()
    {
        var vm = new MainWindowViewModel();

        vm.SelectedToolIndex = 0;
        Assert.True(vm.IsZapretToolSelected);
        Assert.False(vm.IsTgProxyToolSelected);
        Assert.False(vm.IsEmergencyChannelToolSelected);

        vm.SelectedToolIndex = 1;
        Assert.False(vm.IsZapretToolSelected);
        Assert.True(vm.IsTgProxyToolSelected);
        Assert.False(vm.IsEmergencyChannelToolSelected);

        vm.SelectedToolIndex = 2;
        Assert.False(vm.IsZapretToolSelected);
        Assert.False(vm.IsTgProxyToolSelected);
        Assert.True(vm.IsEmergencyChannelToolSelected);
    }
}
