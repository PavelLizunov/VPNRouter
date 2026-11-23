using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AssistantCrashEvidenceTests
{
    [Fact]
    public void ReadsLatestCrashAndSummarizesItsExceptionAndLogTail()
    {
        var data = Directory.CreateTempSubdirectory("assistant-crash-");
        try
        {
            var reports = data.CreateSubdirectory("crashes");
            File.WriteAllText(Path.Combine(reports.FullName, "shutdown-20261124-010000-000.txt"),
                "VPNRouter shutdown marker");
            File.WriteAllText(Path.Combine(reports.FullName, "crash-20261123-010000-000.txt"),
                "VPNRouter crash report\nFatal: False\n");
            var current = Path.Combine(reports.FullName, "crash-20261123-020000-000.txt");
            File.WriteAllText(current, """
                VPNRouter crash report
                Version:   2.51.0-r2
                Fatal:     True

                ──── Exception ────
                System.InvalidOperationException: secret-in-exception
                   at VPNRouter.Core.Services.StartupPipeline.RunAsync() in C:\private\path.cs:line 41

                ──── Tail of vpnrouter.log (last 200 lines) ────
                [WRN] TUN adapter busy with secret-in-log
                """);

            Assert.Equal(new AssistantCrashEvidence(true, AssistantCrashFault.InvalidOperation,
                AssistantCrashArea.Startup, AssistantCrashSignal.TunBusy),
                AssistantCrashEvidence.ReadLatest(data.FullName));
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }

    [Fact]
    public void IgnoresGracefulShutdownMarkersAndUnrecognizedReports()
    {
        var data = Directory.CreateTempSubdirectory("assistant-crash-");
        try
        {
            Assert.Null(AssistantCrashEvidence.ReadLatest(data.FullName));
            var reports = data.CreateSubdirectory("crashes");
            File.WriteAllText(Path.Combine(reports.FullName, "shutdown-20261123-010000-000.txt"),
                "VPNRouter shutdown marker");
            Assert.Null(AssistantCrashEvidence.ReadLatest(data.FullName));
            File.WriteAllText(Path.Combine(reports.FullName, "crash-20261123-020000-000.txt"),
                "Unrecognized report\nFatal: True\n");
            Assert.Null(AssistantCrashEvidence.ReadLatest(data.FullName));
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }
}
