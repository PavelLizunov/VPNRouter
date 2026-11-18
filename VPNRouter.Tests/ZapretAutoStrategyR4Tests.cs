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

public sealed class ZapretAutoStrategyR4Tests
{
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

    [Fact]
    public async Task InstalledProbe_NonWindowsPreservesDiagnostic()
    {
        if (OperatingSystem.IsWindows()) return;

        using var manager = new ZapretManager();
        var result = await ZapretAutoStrategy.RunInstalledProbeAsync(manager, null, null, CancellationToken.None);

        Assert.Equal("not_windows", result.Diagnostic);
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
