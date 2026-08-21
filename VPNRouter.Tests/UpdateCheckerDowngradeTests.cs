#nullable enable

using System;
using System.IO;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class UpdateCheckerDowngradeTests
{
    [Theory]
    [InlineData("2.49.3", "2.49.2", true)]
    [InlineData("2.49.3", "2.49.3", false)]
    [InlineData("2.49.3", "2.50.0", false)]
    public void IsVersionDowngrade_TargetRelation_ReturnsExpected(
        string current,
        string target,
        bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsVersionDowngrade(current, target));
    }

    [Fact]
    public void ShouldWriteInstallReceipt_Downgrade_ReturnsFalseForOldClientCompatibility()
    {
        Assert.False(UpdateChecker.ShouldWriteInstallReceipt("2.49.3", "2.49.2"));
        Assert.True(UpdateChecker.ShouldWriteInstallReceipt("2.49.3", "2.49.4"));
    }

    [Fact]
    public void BackupConfigForDowngrade_ExistingConfig_CreatesExactPrivateCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vpnrouter-downgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "config.yaml");
        const string contents = "schema_version: 8\napp:\n  language: ru\n";
        File.WriteAllText(config, contents);

        try
        {
            var backup = UpdateChecker.BackupConfigForDowngrade("2.49.2", config);

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
            Assert.Equal(contents, File.ReadAllText(backup!));
            Assert.Contains("before-downgrade-to-2.49.2", backup, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BackupConfigForDowngrade_InvalidVersion_FailsBeforeWriting()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpdateChecker.BackupConfigForDowngrade("../../bad", "missing.yaml"));

        Assert.Equal(Strings.DowngradeInvalidVersion, ex.Message);
    }

    [Fact]
    public void DeleteInstallReceiptForDowngrade_RemovesStaleFailureMarker()
    {
        var dir = Directory.CreateTempSubdirectory("vpnrouter-receipt-");
        try
        {
            var receipt = Path.Combine(dir.FullName, ".update-installed-version");
            File.WriteAllText(receipt, "timestamp\n2.50.0\n");

            UpdateChecker.DeleteInstallReceiptForDowngrade(dir.FullName);

            Assert.False(File.Exists(receipt));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
