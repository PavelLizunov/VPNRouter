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
}
