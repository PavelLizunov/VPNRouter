#nullable enable

using System;
using System.IO;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RollbackReceiptCaptureTests
{
    [Fact]
    public void CheckInstallReceipt_CapturesTheConfirmedPreviousVersionBeforeConsumingIt()
    {
        if (!OperatingSystem.IsWindows())
            return; // The production capture is Windows Standard only.

        var previousDataDir = AppPaths.DataDir;
        var directory = Path.Combine(Path.GetTempPath(),
            "vpnrouter-receipt-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AppPaths.OverrideDataDir(directory);
            var receipt = Path.Combine(directory, ".update-installed-version");
            File.WriteAllText(receipt, $"2026-11-24T00:00:00Z{Environment.NewLine}2.50.5{Environment.NewLine}");

            var warning = UpdateChecker.CheckInstallReceipt("2.51.1-r1", receipt);

            Assert.Null(warning);
            Assert.False(File.Exists(receipt));
            var history = new ConnectionRollbackHistory(
                Path.Combine(directory, ConnectionRollbackHistory.FileName),
                buildId: "receipt-test",
                currentVersion: "2.51.1-r1");
            Assert.Equal("2.50.5", history.PreviousInstalledVersion);
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RememberPreviousInstalledVersion_RejectsCurrentAndNewerVersions()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "vpnrouter-receipt-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var history = new ConnectionRollbackHistory(
                Path.Combine(directory, ConnectionRollbackHistory.FileName),
                buildId: "receipt-test",
                currentVersion: "2.51.1-r1");

            history.RememberPreviousInstalledVersion("2.51.1-r1", "test");
            history.RememberPreviousInstalledVersion("2.52.0", "test");
            Assert.Null(history.PreviousInstalledVersion);

            history.RememberPreviousInstalledVersion("2.50.5", "test");
            Assert.Equal("2.50.5", history.PreviousInstalledVersion);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
