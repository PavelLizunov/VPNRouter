using System;
using System.IO;
using Xunit;

namespace VPNRouter.Tests;

public sealed class UpdateButtonLocalizationContractTests
{
    [Fact]
    public void UpdateButton_BindsLProxy_NotHardcoded()
    {
        var axaml = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "VPNRouter.App", "Views", "MainWindow.axaml"));
        var src = File.ReadAllText(axaml);

        var cmd = src.IndexOf("UpdateVm.DownloadAndApplyCommand", StringComparison.Ordinal);

        var start = src.LastIndexOf("<Button", cmd, StringComparison.Ordinal);

        var tag = src.Substring(start, cmd - start);

        Assert.Contains("Content=\"{Binding L_UpdateButton}\"", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("↓ Update", tag, StringComparison.Ordinal);
    }
}
