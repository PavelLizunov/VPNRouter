#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="ZapretAutoStrategy.RunFlowsealProbeAsync"/>, the sweep that runs Flowseal's
/// own <c>utils\test zapret.ps1</c> and turns its console output into a winning strategy. The tests replace that
/// script with a small fake that prints Flowseal-style lines, so the real parsing, scoring, early-winner kill,
/// cancellation and probe-log code runs without starting winws. The method needs Windows and an elevated process,
/// so the tests skip anywhere else.
/// </summary>
public sealed class ZapretFlowsealProbeTests : IDisposable
{
    private readonly string _originalDataDir;
    private readonly string _tempDataDir;

    public ZapretFlowsealProbeTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    private static void RequireWindowsAdmin() =>
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(),
            "the Flowseal sweep needs Windows and an elevated process");

    private static string[] Echo(params string[] lines) =>
        lines.Select(l => "Write-Output '" + l.Replace("'", "''") + "'").ToArray();

    private static void WriteScript(params string[] powershellLines)
    {
        var path = Path.Combine(ZapretUpdater.ZapretDir, "utils", "test zapret.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, powershellLines);
    }

    private static Task<ZapretAutoStrategy.FlowsealSweepResult> Run(
        ProgressLog? progress = null, CancellationToken ct = default) =>
        ZapretAutoStrategy.RunFlowsealProbeAsync(ZapretUpdater.ZapretDir, progress, logger: null, ct);

    private sealed class ProgressLog : IProgress<ZapretAutoStrategy.FlowsealProgress>
    {
        public List<ZapretAutoStrategy.FlowsealProgress> Items { get; } = new();
        public Action? OnFirstReport { get; init; }
        public void Report(ZapretAutoStrategy.FlowsealProgress value)
        {
            bool first;
            lock (Items) { first = Items.Count == 0; Items.Add(value); }
            if (first) OnFirstReport?.Invoke();
        }
    }

    private static readonly string[] TwoStrategyOutput =
    {
        "[1/2] general.bat",
        "  [Discord][HTTP] https://discord.com status=OK",
        "  [Discord][TLS1.2] https://discord.com status=OK",
        "  [YouTube][HTTP] https://youtube.com status=FAIL",
        "[2/2] general (ALT).bat",
        "  [Discord][HTTP] https://discord.com status=OK",
        "  [Discord][TLS1.3] https://discord.com status=UNSUPPORTED",
    };

    [Fact]
    public async Task Sweep_WithABestConfigLine_ReturnsThatWinnerScoresAndProgress()
    {
        RequireWindowsAdmin();
        WriteScript(Echo(TwoStrategyOutput.Append("Best config: general (ALT).bat").ToArray()));
        var progress = new ProgressLog();

        var result = await Run(progress, TestContext.Current.CancellationToken);

        Assert.Equal("general (ALT)", result.Winner);
        Assert.Equal(2, result.TestedCount);
        Assert.Equal(2, result.TotalCount);
        Assert.False(result.EarlyWinner);
        Assert.Null(result.Diagnostic);
        Assert.Empty(result.ErrorLines!);
        Assert.Contains("Best config: general (ALT).bat", result.FullOutput);
        var perStrategy = result.PerStrategyResults!;
        Assert.Equal(2, perStrategy.Count);
        Assert.Equal((2, 3), (perStrategy["general"].Passed, perStrategy["general"].Total));
        Assert.Equal((2, 2), (perStrategy["general (ALT)"].Passed, perStrategy["general (ALT)"].Total));
        Assert.Equal(
            new[]
            {
                new ZapretAutoStrategy.FlowsealProgress(1, 2, "general", 0, 0),
                new ZapretAutoStrategy.FlowsealProgress(1, 2, "", 1, 1),
                new ZapretAutoStrategy.FlowsealProgress(1, 2, "", 2, 2),
                new ZapretAutoStrategy.FlowsealProgress(1, 2, "", 2, 3),
                new ZapretAutoStrategy.FlowsealProgress(2, 2, "general (ALT)", 0, 0),
                new ZapretAutoStrategy.FlowsealProgress(2, 2, "", 1, 1),
                new ZapretAutoStrategy.FlowsealProgress(2, 2, "", 2, 2),
            },
            progress.Items);
    }

    [Fact]
    public async Task Sweep_WithoutABestConfigLine_PromotesTheBestScoringStrategy()
    {
        RequireWindowsAdmin();
        WriteScript(Echo(TwoStrategyOutput));

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.Equal("general (ALT)", result.Winner);
        Assert.False(result.EarlyWinner);
    }

    [Fact]
    public async Task Sweep_WhereEveryCheckFails_HasNoWinner()
    {
        RequireWindowsAdmin();
        WriteScript(Echo(
            "[1/1] general.bat",
            "  [Discord][HTTP] https://discord.com status=FAIL",
            "  [YouTube][HTTP] https://youtube.com status=ERROR"));

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.Null(result.Winner);
        Assert.Null(result.Diagnostic);
        Assert.Equal((0, 2), (result.PerStrategyResults!["general"].Passed, result.PerStrategyResults["general"].Total));
    }

    [Fact]
    public async Task Sweep_StrategyThatPassesSixteenChecksInARow_WinsEarlyAndTheScriptIsKilled()
    {
        RequireWindowsAdmin();
        var lines = Echo("[1/5] general (ALT9).bat")
            .Concat(Enumerable.Range(1, 16).SelectMany(i => Echo($"  [Site{i}][HTTP] https://site{i}.example status=OK")))
            .Concat(new[] { "Start-Sleep -Seconds 120", "Write-Output 'Best config: never printed.bat'" })
            .ToArray();
        WriteScript(lines);
        var progress = new ProgressLog();

        var started = DateTime.UtcNow;
        var result = await Run(progress, TestContext.Current.CancellationToken);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), "the sweep must not wait for the sleeping script");
        Assert.Equal("general (ALT9)", result.Winner);
        Assert.True(result.EarlyWinner);
        Assert.Equal(1, result.TestedCount);
        Assert.Equal(5, result.TotalCount);
        Assert.DoesNotContain("never printed", result.FullOutput);
        Assert.Equal((16, 16), (result.PerStrategyResults!["general (ALT9)"].Passed, result.PerStrategyResults["general (ALT9)"].Total));
        Assert.Equal(new ZapretAutoStrategy.FlowsealProgress(1, 5, "general (ALT9)", 16, 16), progress.Items[^1]);
    }

    [Fact]
    public async Task Sweep_KeepsOnlyTheLastEightErrorLines_Trimmed()
    {
        RequireWindowsAdmin();
        WriteScript(Echo(Enumerable.Range(1, 10).Select(i => $"   [ERROR] problem {i}")
            .Append("  [WARN] a warning").Append("not an error line").ToArray()));

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            new[]
            {
                "[ERROR] problem 4", "[ERROR] problem 5", "[ERROR] problem 6", "[ERROR] problem 7",
                "[ERROR] problem 8", "[ERROR] problem 9", "[ERROR] problem 10", "[WARN] a warning",
            },
            result.ErrorLines);
        Assert.Contains("not an error line", result.FullOutput);
    }

    [Fact]
    public async Task Sweep_AnswersTheScriptsMenuWithTwoThenOne()
    {
        RequireWindowsAdmin();
        WriteScript(
            "$a = [Console]::In.ReadLine()",
            "$b = [Console]::In.ReadLine()",
            "Write-Output \"answers=$a,$b\"");

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.Contains("answers=2,1", result.FullOutput);
    }

    [Fact]
    public async Task Sweep_CancelledWhileTheScriptRuns_ReportsCanceledWithoutAWinner()
    {
        RequireWindowsAdmin();
        WriteScript(Echo("[1/3] general.bat").Append("Start-Sleep -Seconds 120").ToArray());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var progress = new ProgressLog { OnFirstReport = () => cts.Cancel() };

        var result = await Run(progress, cts.Token);

        Assert.Equal("canceled", result.Diagnostic);
        Assert.Null(result.Winner);
        Assert.Equal(1, result.TestedCount);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task Sweep_MissingScript_ReportsMissingScriptWithoutStartingAnything()
    {
        RequireWindowsAdmin();
        Directory.CreateDirectory(ZapretUpdater.ZapretDir);

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.Equal("missing_script", result.Diagnostic);
        Assert.Null(result.Winner);
        Assert.StartsWith("missing:", result.FullOutput);
    }

    [Fact]
    public async Task Sweep_WritesAProbeLogWithHeaderTimestampedLinesAndOutcome()
    {
        RequireWindowsAdmin();
        WriteScript(Echo(TwoStrategyOutput.Append("Best config: general (ALT).bat").ToArray()));

        var result = await Run(ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result.ProbeLogPath);
        Assert.StartsWith(AppPaths.LogsDir, result.ProbeLogPath!);
        var log = File.ReadAllLines(result.ProbeLogPath!);
        Assert.StartsWith("# Zapret probe log started at", log[0]);
        Assert.Contains(log, l => l.StartsWith("# zapretInstallDir = ", StringComparison.Ordinal));
        Assert.Contains(log, l => l.EndsWith(" [1/2] general.bat", StringComparison.Ordinal));
        Assert.StartsWith("# Probe finished at", log[^1]);
        Assert.EndsWith("outcome=winner, winner=general (ALT)", log[^1]);
    }
}
