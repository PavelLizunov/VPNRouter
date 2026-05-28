#nullable enable
// ============================================================================
// RoutingAppListEditorTests.cs — v2.38.0 (2026-05-28)
// ============================================================================
//
// Tests for RoutingAppListEditor.TryAddProcessName — the pure helper behind
// the Explorer context-menu "route this app through VPN" feature
// (plans/feature-shell-context-menu-add-app.md). Pins: .exe-only guard,
// case-insensitive dedup with casing PRESERVED (golden rule #7), full-path
// reduction, and invalid-input safety.
// ============================================================================

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class RoutingAppListEditorTests
{
    private static AppSettings Fresh() => new();

    [Fact]
    public void AddNewExe_Inserts_ReturnsAddedTrue()
    {
        var s = Fresh();
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        Assert.True(added);
        Assert.Equal("Discord.exe", normalized);
        Assert.Contains("Discord.exe", s.App.RoutingAppsInclude);
    }

    [Fact]
    public void AddDuplicateSameCase_NotAdded_ReturnsExisting()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        Assert.False(added);
        Assert.Equal("Discord.exe", normalized);
        Assert.Single(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void AddDuplicateDifferentCase_NotAdded_PreservesOriginalCasing()
    {
        // process_name matching is case-sensitive; we must NOT add a second
        // lower-cased entry and must NOT mutate the original casing.
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(s, "discord.EXE");
        Assert.False(added);
        Assert.Equal("Discord.exe", normalized);          // returns the existing casing
        Assert.Single(s.App.RoutingAppsInclude);
        Assert.Equal("Discord.exe", s.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void AddFullPath_ReducesToBasename_PreservesCasing()
    {
        var s = Fresh();
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(
            s, @"C:\Users\osuhu\AppData\Local\Discord\app-1.0\Discord.exe");
        Assert.True(added);
        Assert.Equal("Discord.exe", normalized);
        Assert.Contains("Discord.exe", s.App.RoutingAppsInclude);
    }

    [Fact]
    public void AddQuotedPath_Handled()
    {
        var s = Fresh();
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(
            s, "\"C:\\Program Files\\App\\Game.exe\"");
        Assert.True(added);
        Assert.Equal("Game.exe", normalized);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("shortcut.lnk")]
    [InlineData("folder")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NonExeOrBlank_Rejected(string? input)
    {
        var s = Fresh();
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(s, input);
        Assert.False(added);
        Assert.Null(normalized);
        Assert.Empty(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void NullSettings_NoThrow_ReturnsFalseNull()
    {
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(null, "Discord.exe");
        Assert.False(added);
        Assert.Null(normalized);
    }

    [Fact]
    public void AddMultipleDistinct_AllInserted_OrderPreserved()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        RoutingAppListEditor.TryAddProcessName(s, "Telegram.exe");
        RoutingAppListEditor.TryAddProcessName(s, "chrome.exe");
        Assert.Equal(new[] { "Discord.exe", "Telegram.exe", "chrome.exe" },
            s.App.RoutingAppsInclude);
    }

    [Fact]
    public void CasePreserved_NotLowercased()
    {
        // The whole point of golden rule #7 — a mixed-case exe stays mixed.
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "EpicGamesLauncher.exe");
        Assert.Equal("EpicGamesLauncher.exe", s.App.RoutingAppsInclude[0]);
    }
}
