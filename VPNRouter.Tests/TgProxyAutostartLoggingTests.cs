using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TgProxyAutostartLoggingTests
{
    [Fact]
    public void RedactSecretInArgs_ReplacesSecretValueWithLiteral()
    {
        const string realSecret = "abcdef0123456789abcdef0123456789";
        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {realSecret}";

        var redacted = TgProxyManager.RedactSecretInArgs(args);

        Assert.DoesNotContain(realSecret, redacted);
        Assert.Contains("--secret REDACTED", redacted);
        Assert.Contains("--port 1443", redacted);
        Assert.Contains("--host 127.0.0.1", redacted);
        Assert.Contains("proxy.tg_ws_proxy", redacted);
    }

    [Fact]
    public void RedactSecretInArgs_VerboseFlag_StillRedactsSecret()
    {
        const string realSecret = "deadbeefcafebabe1122334455667788";
        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {realSecret} --verbose";

        var redacted = TgProxyManager.RedactSecretInArgs(args);

        Assert.DoesNotContain(realSecret, redacted);
        Assert.Contains("--verbose", redacted);
    }

    [Fact]
    public void RedactSecretInArgs_HandlesEmptyAndNull()
    {
        Assert.Equal(string.Empty, TgProxyManager.RedactSecretInArgs(string.Empty));
        Assert.Null(TgProxyManager.RedactSecretInArgs(null!));
    }

    [Fact]
    public void IsInstalled_LoggerOverload_EmitsOnePerCall_NoSecretLeak()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        _ = TgProxyUpdater.IsInstalled(logger);

        var lines = sink.Render();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("[TgProxy] IsInstalled:"));
        Assert.Contains(lines, l => l.Contains("PythonExe at"));
        Assert.Contains(lines, l => l.Contains("ProxySourceDir at"));
        Assert.Contains(lines, l => l.Contains("certifi"));
        Assert.Contains(lines, l => l.Contains("overall = "));

        foreach (var line in lines)
        {
            Assert.False(
                Regex.IsMatch(line, @"\b[a-f0-9]{32}\b"),
                $"IsInstalled log line contained a 32-char hex blob (potential secret leak): {line}");
        }
    }

    [Fact]
    public void Logger_OverallLogChain_DoesNotEmitPlaintextSecret()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        const string testSecret = "0123456789abcdef0123456789abcdef";

        _ = TgProxyUpdater.IsInstalled(logger);

        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {testSecret}";
        var redacted = TgProxyManager.RedactSecretInArgs(args);

        logger.Information("[TgProxy] Spawn ProcessStartInfo: Arguments={Arguments}", redacted);

        var lines = sink.Render();
        foreach (var line in lines)
        {
            Assert.DoesNotContain(testSecret, line);
        }
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<string> _events = new();
        public void Emit(LogEvent logEvent)
        {
            var rendered = logEvent.RenderMessage();
            foreach (var kvp in logEvent.Properties)
            {
                rendered += " | " + kvp.Value.ToString();
            }
            lock (_events) _events.Add(rendered);
        }

        public IReadOnlyList<string> Render()
        {
            lock (_events) return _events.ToList();
        }
    }
}
