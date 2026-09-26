using System;
using System.IO;
using VPNRouter.App.Views;
using VPNRouter.Core;
using Xunit;

namespace VPNRouter.Tests;

public class AboutWindowLogScrubbingTests
{
    [Fact]
    public void GetSingBoxVersion_ExceptionLogging_ScrubsSecretsInLog()
    {
        const string secretUuid = "2d54442d-158f-49e2-b225-67ba1a5b77f4";
        const string secretToken = "SECRETTOKEN1234567890ABCDEF1234567890ABCDEF";
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter_about_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var dummyExePath = Path.Combine(tempDir, "sing-box.exe");
            File.WriteAllText(dummyExePath, "dummy");

            var logPath = Path.Combine(AppPaths.LogsDir, "about-probe.log");
            if (File.Exists(logPath))
            {
                try { File.Delete(logPath); } catch { }
            }

            var result = AboutWindow.GetSingBoxVersion(
                dummyExePath,
                path => throw new InvalidOperationException(
                    $"Failed to start vless://{secretUuid}@1.2.3.4:443 with token={secretToken}"));

            Assert.Equal("err: InvalidOperationException", result);
            Assert.True(File.Exists(logPath), "about-probe.log should be created when exception occurs");

            var logContent = File.ReadAllText(logPath);
            Assert.Contains("GetSingBoxVersion failed: InvalidOperationException:", logContent);
            Assert.DoesNotContain(secretUuid, logContent);
            Assert.DoesNotContain(secretToken, logContent);
            Assert.Contains("vless://[redacted]", logContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
