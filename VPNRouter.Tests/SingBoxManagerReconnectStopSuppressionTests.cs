#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerReconnectStopSuppressionTests
{
    [Fact]
    public void ReconnectStop_LateExitedRaceLost_DoesNotFireCrashed_LogsExpectedExitAtInfo()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42001) { SimulateExitedRaceLost = true };
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");
            Assert.Equal(SingBoxState.Running, manager.State);

            manager.Stop();

            Assert.Equal(SingBoxState.Stopped, manager.State);

            Assert.Equal(0, crashedCount);

            var events = sink.Events;

            Assert.DoesNotContain(events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));

            var suppression = events.FirstOrDefault(e =>
                e.Level == LogEventLevel.Information &&
                e.MessageTemplate.Text.Contains("Expected exit during intentional") &&
                e.MessageTemplate.Text.Contains("suppressing Crashed event"));
            Assert.True(suppression != null,
                "Expected an INF 'Expected exit during intentional ... suppressing Crashed event' line.");
            Assert.True(suppression!.Properties.TryGetValue("Phase", out var phaseProp),
                "suppression log event must carry a {Phase} property");
            Assert.Equal("stop", (phaseProp as ScalarValue)?.Value);
            Assert.Contains("intentional stop (exit code: -1)", suppression.RenderMessage());
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void GenuineCrash_NoTeardownInFlight_FiresCrashed_StateFailed_LogsCrashAtError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42002);
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");

            handle.SignalExit(exitCode: 1);

            Assert.Equal(1, crashedCount);
            Assert.Equal(SingBoxState.Failed, manager.State);

            var events = sink.Events;
            Assert.Contains(events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));
            Assert.DoesNotContain(events, e =>
                e.MessageTemplate.Text.Contains("Expected exit during intentional"));
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void ExitMinusOne_NoTeardownInFlight_StillFiresCrashed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42003);
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");

            handle.SignalExit(exitCode: -1);

            Assert.Equal(1, crashedCount);
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));
            Assert.DoesNotContain(sink.Events, e =>
                e.MessageTemplate.Text.Contains("Expected exit during intentional"));
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath, ILogger logger) =>
        new(new SingBoxSettings { ExecutablePath = exePath, ClashApi = "127.0.0.1:9090" },
            logger: logger,
            http: new FakeHttpClient(),
            runner: runner);

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-recon-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    private static (ILogger logger, CapturingSink sink) BuildCapturingLogger()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static (string body, int start) StopInternalBody()
    {
        var src = ReadSingBoxManagerSource();
        var start = src.IndexOf("private void StopInternal(bool releaseLock)", StringComparison.Ordinal);
        Assert.True(start >= 0, "StopInternal not found in SingBoxManager.cs");
        var end = src.IndexOf("public void Restart()", start, StringComparison.Ordinal);
        Assert.True(end > start, "Restart() (StopInternal end bound) not found after StopInternal");
        return (src.Substring(start, end - start), start);
    }

    private static string ReadSingBoxManagerSource() =>
        ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return ReadAllParts(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine(new[] { Environment.CurrentDirectory }.Concat(segments).ToArray());
        if (!File.Exists(fallback))
            throw new FileNotFoundException($"Source file not found: {string.Join("/", segments)}");
        return ReadAllParts(fallback);
    }

    private static string ReadAllParts(string primaryPath)
    {
        var dir = Path.GetDirectoryName(primaryPath)!;
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var parts = Directory.GetFiles(dir, stem + "*.cs")
            .Where(p =>
            {
                var fn = Path.GetFileName(p);
                return fn == stem + ".cs" || fn.StartsWith(stem + ".", StringComparison.Ordinal);
            })
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join("\n", parts.Select(File.ReadAllText));
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
