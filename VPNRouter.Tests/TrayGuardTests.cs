using System;
using System.IO;
using VPNRouter.App.Services;
using Xunit;

namespace VPNRouter.Tests;

public class TrayGuardTests
{
    [Fact]
    public void Missing_icon_keeps_the_window_on_the_taskbar()
    {
        Assert.Equal(TrayCloseAction.MinimizeToTaskbar, TrayGuard.DecideClose(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Present_or_unknown_icon_hides_to_the_tray(bool? registered)
    {
        Assert.Equal(TrayCloseAction.HideToTray, TrayGuard.DecideClose(registered));
    }

    [Fact]
    public void Hint_is_shown_once_and_only_for_a_visible_icon()
    {
        Assert.True(TrayGuard.ShouldShowHint(true, hintShownBefore: false));
        Assert.False(TrayGuard.ShouldShowHint(true, hintShownBefore: true));
        Assert.False(TrayGuard.ShouldShowHint(null, hintShownBefore: false));
        Assert.False(TrayGuard.ShouldShowHint(false, hintShownBefore: false));
    }

    [Fact]
    public void Hint_marker_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpnrouter-tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "tray-hint-shown");
            Assert.False(TrayGuard.HintShownBefore(marker));
            TrayGuard.MarkHintShown(marker);
            Assert.True(TrayGuard.HintShownBefore(marker));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Hint_texts_fit_the_balloon_fields()
    {
        // NOTIFYICONDATAW: szInfoTitle holds 63 characters, szInfo 255. Measured in the current language; the language is not switched here because
        // it is process-wide state other tests read in parallel.
        Assert.InRange(VPNRouter.Core.Localization.Strings.TrayHintTitle.Length, 1, 63);
        Assert.InRange(VPNRouter.Core.Localization.Strings.TrayHintBody.Length, 1, 255);
    }

    [Fact]
    public void Tray_icon_is_registered_with_the_application()
    {
        // Avalonia 12.1.3 creates the native icon only for a TrayIcon in TrayIcon.Icons; v2.50.0 to v2.50.4-r1 constructed one without registering it
        // and had no tray icon at all.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var app = File.ReadAllText(Path.Combine(directory!.FullName, "VPNRouter.App", "App.axaml.cs"));
        Assert.Contains("TrayIcon.SetIcons(this, new TrayIcons { _trayIcon })", app);
    }
}
