using System;
using System.IO;
using Xunit;

namespace VPNRouter.Tests;

public sealed class UpdateButtonLocalizationContractTests
{
    [Fact]
    public void UpdateBannerButton_BindsLocalizedUpdateButton()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.App")))
            dir = dir.Parent;

        var src = File.ReadAllText(Path.Combine(
            dir!.FullName, "VPNRouter.App", "Views", "MainWindow.axaml"));

        var cmd = src.IndexOf("UpdateVm.DownloadAndApplyCommand", StringComparison.Ordinal);
        var start = src.LastIndexOf("<Button", cmd, StringComparison.Ordinal);
        var tag = src.Substring(start, cmd - start);

        Assert.Contains("Content=\"{Binding L_UpdateButton}\"", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("↓ Update", tag, StringComparison.Ordinal);
    }
}
