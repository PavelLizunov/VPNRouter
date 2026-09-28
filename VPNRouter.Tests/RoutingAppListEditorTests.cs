#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class RoutingAppListEditorTests
{
    [Theory]
    [InlineData("Discord", true, "Discord.exe")]
    [InlineData("Discord.exe", true, "Discord.exe")]
    [InlineData(@"C:\\Apps\\Discord.exe", true, "Discord.exe")]
    [InlineData("Discord.exe", false, "Discord")]
    public void NormalizeManualProcessName_AcceptsUserFacingForms(
        string input, bool windows, string expected)
        => Assert.Equal(expected,
            RoutingAppListEditor.NormalizeManualProcessName(input, windows));

    [Theory]
    [InlineData("bad.dll")]
    [InlineData("*.exe")]
    [InlineData(@"C:\\Apps\\")]
    public void NormalizeManualProcessName_RejectsMalformedWindowsInput(string input)
        => Assert.Null(RoutingAppListEditor.NormalizeManualProcessName(input, windows: true));

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
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (added, normalized) = RoutingAppListEditor.TryAddProcessName(s, "discord.EXE");
        Assert.False(added);
        Assert.Equal("Discord.exe", normalized);
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
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "EpicGamesLauncher.exe");
        Assert.Equal("EpicGamesLauncher.exe", s.App.RoutingAppsInclude[0]);
    }

    [Fact]
    public void RemoveExisting_Removes_ReturnsRemovedTrue()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (removed, normalized) = RoutingAppListEditor.TryRemoveProcessName(s, "Discord.exe");
        Assert.True(removed);
        Assert.Equal("Discord.exe", normalized);
        Assert.Empty(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void RemoveDifferentCase_StillRemoves()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (removed, normalized) = RoutingAppListEditor.TryRemoveProcessName(s, "discord.EXE");
        Assert.True(removed);
        Assert.Equal("discord.EXE", normalized);
        Assert.Empty(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void RemoveFullPath_ReducesToBasename()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Game.exe");
        var (removed, _) = RoutingAppListEditor.TryRemoveProcessName(
            s, @"C:\Program Files\App\Game.exe");
        Assert.True(removed);
        Assert.Empty(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void RemoveNotPresent_ReturnsFalse_KeepsList()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (removed, normalized) = RoutingAppListEditor.TryRemoveProcessName(s, "NotThere.exe");
        Assert.False(removed);
        Assert.Equal("NotThere.exe", normalized);
        Assert.Single(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void RemoveLeavesOtherEntries_OnlyTargetGone()
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        RoutingAppListEditor.TryAddProcessName(s, "Telegram.exe");
        var (removed, _) = RoutingAppListEditor.TryRemoveProcessName(s, "Discord.exe");
        Assert.True(removed);
        Assert.Equal(new[] { "Telegram.exe" }, s.App.RoutingAppsInclude);
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("shortcut.lnk")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RemoveNonExeOrBlank_Rejected(string? input)
    {
        var s = Fresh();
        RoutingAppListEditor.TryAddProcessName(s, "Discord.exe");
        var (removed, _) = RoutingAppListEditor.TryRemoveProcessName(s, input);
        Assert.False(removed);
        Assert.Single(s.App.RoutingAppsInclude);
    }

    [Fact]
    public void RemoveNullSettings_NoThrow_ReturnsFalseNull()
    {
        var (removed, normalized) = RoutingAppListEditor.TryRemoveProcessName(null, "Discord.exe");
        Assert.False(removed);
        Assert.Null(normalized);
    }

    [Fact]
    public void StillRouted_AnotherGroupHasSameName_True()
    {
        Assert.True(RoutingAppListEditor.IsStillRoutedByAnother(
            "Discord.exe", new[] { "Chrome.exe", "Discord.exe" }));
    }

    [Fact]
    public void StillRouted_NoOtherReference_False()
    {
        Assert.False(RoutingAppListEditor.IsStillRoutedByAnother(
            "Discord.exe", new[] { "Chrome.exe", "Telegram.exe" }));
    }

    [Theory]
    [InlineData("Discord.exe", "discord")]
    [InlineData("Discord", "Discord.exe")]
    [InlineData("Discord.exe", "DISCORD.EXE")]
    public void StillRouted_ExeSuffixAndCaseInsensitive_True(string target, string survivor)
    {
        Assert.True(RoutingAppListEditor.IsStillRoutedByAnother(
            target, new[] { survivor }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StillRouted_BlankTarget_False(string? target)
    {
        Assert.False(RoutingAppListEditor.IsStillRoutedByAnother(target, new[] { "Discord.exe" }));
    }

    [Fact]
    public void StillRouted_NullOrEmptySurvivors_False()
    {
        Assert.False(RoutingAppListEditor.IsStillRoutedByAnother("Discord.exe", null));
        Assert.False(RoutingAppListEditor.IsStillRoutedByAnother("Discord.exe", System.Array.Empty<string?>()));
        Assert.False(RoutingAppListEditor.IsStillRoutedByAnother("Discord.exe", new string?[] { null, "", "  " }));
    }
}
