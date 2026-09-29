#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretAutoStrategyR4Tests : IDisposable
{
    private readonly string _tempRoot;

    public ZapretAutoStrategyR4Tests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-r4-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "lists"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    [Fact]
    public void HasOrphanedIpsetFlag_NoFlag_ReturnsFalse()
    {
        Assert.False(ZapretAutoStrategy.HasOrphanedIpsetFlag(_tempRoot));
    }

    [Fact]
    public void HasOrphanedIpsetFlag_FlagPresent_ReturnsTrue()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "ipset_switched.flag"), "");
        Assert.True(ZapretAutoStrategy.HasOrphanedIpsetFlag(_tempRoot));
    }

    [Fact]
    public void HasOrphanedIpsetFlag_NonexistentDir_ReturnsFalse()
    {
        var bogus = Path.Combine(_tempRoot, "no-such-dir");
        Assert.False(ZapretAutoStrategy.HasOrphanedIpsetFlag(bogus));
    }

    [Fact]
    public void RestoreIpsetAfterKill_NoFlag_NoOp()
    {
        ZapretAutoStrategy.RestoreIpsetAfterKill(_tempRoot, logger: null);

        Assert.False(File.Exists(Path.Combine(_tempRoot, "ipset_switched.flag")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "lists", "ipset-all.txt")));
    }

    [Fact]
    public void FlowsealProgress_ScoreOnlyUpdate_EmptyStrategyName()
    {
        var p = new ZapretAutoStrategy.FlowsealProgress(5, 20, string.Empty, 3, 6);
        Assert.Equal(string.Empty, p.StrategyName);
        Assert.Equal(3, p.OkCount);
        Assert.Equal(6, p.TotalChecks);
    }

    [Fact]
    public void FlowsealSweepResult_BackCompatCtor_DiagnosticAndErrorLinesDefault()
    {
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: "general (ALT3)", TestedCount: 20, TotalCount: 20,
            FullOutput: "<output>");
        Assert.Equal("general (ALT3)", r.Winner);
        Assert.Null(r.Diagnostic);
        Assert.Null(r.ErrorLines);
    }

    [Fact]
    public void FlowsealSweepResult_WithDiagnostic_CarriesTypedToken()
    {
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: null, TestedCount: 0, TotalCount: 0, FullOutput: "",
            Diagnostic: "not_admin", ErrorLines: Array.Empty<string>());
        Assert.Equal("not_admin", r.Diagnostic);
        Assert.NotNull(r.ErrorLines);
        Assert.Empty(r.ErrorLines!);
    }

    [Fact]
    public void FlowsealSweepResult_WithErrorLines_PreservesList()
    {
        var errs = new[] { "[ERROR] zapret service installed", "[WARN] curl missing" };
        var r = new ZapretAutoStrategy.FlowsealSweepResult(
            Winner: null, TestedCount: 1, TotalCount: 20, FullOutput: "",
            Diagnostic: "canceled", ErrorLines: errs);
        Assert.Equal(2, r.ErrorLines!.Count);
        Assert.Contains("[ERROR] zapret service installed", r.ErrorLines);
        Assert.Contains("[WARN] curl missing", r.ErrorLines);
    }

    [Fact]
    public void IsRunningAsAdmin_NonWindows_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ZapretAutoStrategy.IsRunningAsAdmin());
        }
        else
        {
            var _ = ZapretAutoStrategy.IsRunningAsAdmin();
        }
    }

    [Theory]
    [InlineData("script with spaces.ps1")]
    [InlineData("script&with%characters^.ps1")]
    [InlineData("script\"quoted.ps1")]
    [InlineData("script\r\nline.ps1")]
    public void BuildFlowsealProbeStartInfo_KeepsScriptAsOneArgument(string scriptPath)
    {
        var psi = ZapretAutoStrategy.BuildFlowsealProbeStartInfo("install directory", scriptPath);

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Equal("install directory", psi.WorkingDirectory);
        Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }, psi.ArgumentList);
        Assert.Empty(psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardErrorEncoding);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, psi.WindowStyle);
    }

    [Fact]
    public async Task RunFlowsealProbeAsync_NonWindowsPreservesDiagnostic()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = await ZapretAutoStrategy.RunFlowsealProbeAsync(
            "directory&with%shell^characters", null, null, CancellationToken.None);

        Assert.Equal("not_windows", result.Diagnostic);
    }

    [Fact]
    public void FlowsealMaxSweepTime_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ZapretAutoStrategy.FlowsealMaxSweepTime);
    }

    [Fact]
    public async Task ProbeOneTargetAsync_LogsDoNotContainToken()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var handler = new FailingHttpMessageHandler();
        using var client = new HttpClient(handler);

        const string sensitiveUrl = "https://targets.example/probe?token=secret123";

        var outcome = await ZapretAutoStrategy.ProbeOneTargetAsync(
            sensitiveUrl,
            client,
            logger,
            CancellationToken.None);

        Assert.Equal(ZapretAutoStrategy.ProbeOutcome.Failed, outcome);

        var allLogs = string.Join("\n", sink.Events.Select(e => e.RenderMessage()));
        Assert.DoesNotContain("secret123", allLogs);
        Assert.DoesNotContain("token=", allLogs);
        Assert.DoesNotContain("/probe", allLogs);
        Assert.Contains("https://targets.example", allLogs);
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated probe network failure");
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        private readonly object _gate = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_gate) _events.Add(logEvent);
        }

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_gate) return _events.ToList(); }
        }
    }
}
