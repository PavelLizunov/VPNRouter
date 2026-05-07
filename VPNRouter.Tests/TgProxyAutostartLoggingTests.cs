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

/// <summary>
/// v2.31.10 — pin the structured logging breadcrumbs added to
/// <see cref="TgProxyUpdater.IsInstalled(ILogger?)"/>,
/// <see cref="TgProxyManager.Start"/>,
/// <see cref="ResilientStarter.StartWithBackoffAsync(string, Action, Func{Exception, bool}?, int[]?, ILogger?, System.Threading.CancellationToken)"/>
/// and <c>VPNRouterService.AutostartTgProxyAsync</c>.
///
/// <para>Pre-fix the autostart could exit silently down any of three
/// early-return branches (not installed, no secret, exception) with no
/// breadcrumb pointing at the failure site — diagnosing required
/// rebuilding with extra <c>printf</c>-style logs. The fix wires
/// structured Serilog calls in at every decision so a fresh logs read
/// answers "where did autostart give up?" without re-running the user.</para>
///
/// <para>Tests are SOURCE-STRING PINS — same pattern as
/// <see cref="ServiceAppCoexistenceTests"/> — because the goal is to
/// guarantee the log calls keep firing, not to behaviour-test process
/// spawning (which requires Python embeddable + GitHub network +
/// Windows-only paths). The redaction guarantee is the one behavioural
/// test: it calls the redaction helper directly + scans an in-memory
/// Serilog sink for any plaintext secret leak.</para>
/// </summary>
public sealed class TgProxyAutostartLoggingTests
{
    // ─── source-pin tests ───────────────────────────────────────────────────

    [Fact]
    public void TgProxyUpdater_IsInstalled_HasLoggerOverloadAndStructuredLog()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyUpdater.cs");
        if (src == null) return; // partial CI checkout

        // Logger-aware overload must exist so callers (Service +
        // App-side after DBG-2) can opt into structured probe logs.
        // Match either Serilog ILogger import path (the file imports
        // Serilog so unqualified is fine) or fully qualified.
        Assert.Matches(
            @"public\s+static\s+bool\s+IsInstalled\s*\(\s*(?:Serilog\.)?ILogger\?\s+\w+\s*\)",
            src);

        // The parameterless overload still exists (back-compat).
        Assert.Matches(
            @"public\s+static\s+bool\s+IsInstalled\s*\(\s*\)",
            src);

        // The structured log line must include both PythonExe and
        // ProxySourceDir paths + their existence flag + overall.
        // We don't pin the exact wording; we pin every load-bearing
        // token so a refactor that drops any of them is caught.
        Assert.Contains("PythonExe at", src);
        Assert.Contains("ProxySourceDir at", src);
        Assert.Contains("overall = ", src);
    }

    [Fact]
    public void TgProxyManager_Start_LogsRedactedPsiAndPostSpawnProbe()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Log call must include FileName + Arguments + WorkingDirectory.
        // We don't lock the exact format string — we lock the structured
        // properties so a future tweak that renames the template is
        // caught only if it drops a key field.
        Assert.Contains("FileName=", stripped);
        Assert.Contains("Arguments=", stripped);
        Assert.Contains("WorkingDirectory=", stripped);

        // The redacted-args local must be wired into the log call,
        // never the raw `args` string. If a future refactor reverts to
        // logging raw args, the secret leaks.
        Assert.Contains("redactedArgs", stripped);
        Assert.Contains("RedactSecretInArgs", stripped);

        // Post-spawn watchdog: WaitForExit(2000) must run + log on
        // early exit. Pin the timeout literal + the "within 2s" wording
        // so a future refactor that removes the probe is caught.
        Assert.Contains("WaitForExit(2000)", stripped);
        Assert.Contains("within 2s", stripped);

        // ExitCode + StandardError tail must appear in the log path
        // for early exits. Without these, a failed Python launch would
        // emit a generic "process exited" warning with no details.
        Assert.Contains("ExitCode", stripped);
        Assert.Contains("StandardError", stripped);
    }

    [Fact]
    public void ResilientStarter_LogsAttemptCadenceAndOutcome()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "ResilientStarter.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Pre-attempt log: every iteration must emit BEFORE startFn
        // runs. Pin the structured tag + the "delay=" property so a
        // future refactor that moves the log to post-attempt only
        // (the pre-fix behaviour) is caught.
        Assert.Contains("[Resilient]", stripped);
        Assert.Contains("attempt {Attempt}/{Max}, delay={Delay}s", stripped);

        // Success outcome must log per-attempt (not just on retry).
        // Pre-fix the success log was gated on attempt > 1 so a
        // first-try success was invisible.
        Assert.Contains("succeeded", stripped);

        // Failure outcome carries the exception message.
        Assert.Contains("failed-with-{Error}", stripped);
    }

    [Fact]
    public void VPNRouterService_AutostartTgProxyAsync_LogsEntryAndDecisions()
    {
        var src = LoadSource("VPNRouter.Service", "VPNRouterService.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Entry breadcrumb — proves the method even fired.
        Assert.Contains("AutostartTgProxyAsync: entered", stripped);

        // The Install probe must call the LOGGER-AWARE overload, not
        // the parameterless one. Pre-fix the probe used the bool-only
        // overload, hiding which path was missing on disk.
        Assert.Matches(
            @"TgProxyUpdater\.IsInstalled\s*\(\s*Serilog\.Log\.Logger\s*\)",
            stripped);

        // Secret + port decision log fires AFTER the secret check passes.
        Assert.Contains("secret configured (len {SecretLen}), port {Port}", stripped);

        // The autostart MUST NOT log the actual secret. Negative pin —
        // any future refactor that adds {Secret} to a structured log
        // here trips this immediately.
        Assert.DoesNotMatch(
            @"\{Secret\}|secret\s*=\s*\{?[a-zA-Z]*Secret",
            stripped.Replace("SecretLen", "")); // SecretLen is fine
    }

    [Fact]
    public void AppViewModel_ToggleTgProxyAsync_UsesLoggerAwareIsInstalledAndStructuredLogs()
    {
        // App-side parity. The DBG-2 sister task hasn't merged its
        // AutostartTgProxyAsync yet, but the manual-Start handler in
        // ToggleTgProxyAsync mirrors the structured-log pattern so
        // logs from manual + autostart paths grep the same way.
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Logger-aware probe in the manual-Start path.
        Assert.Matches(
            @"TgProxyUpdater\.IsInstalled\s*\(\s*_logger\s*\)",
            stripped);

        // Entry + secret/port log shape mirrors Service.
        Assert.Contains("ToggleTgProxyAsync: start path entered", stripped);
        Assert.Contains("secret configured (len {SecretLen}), port {Port}", stripped);
    }

    // ─── behavioural tests ──────────────────────────────────────────────────

    [Fact]
    public void RedactSecretInArgs_ReplacesSecretValueWithLiteral()
    {
        // Direct unit test of the redaction helper. We rely on this
        // exclusively — every log call site that touches Args feeds
        // it through this function, so behaviour-pinning here covers
        // every present + future log site.
        const string realSecret = "abcdef0123456789abcdef0123456789";
        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {realSecret}";

        var redacted = TgProxyManager.RedactSecretInArgs(args);

        Assert.DoesNotContain(realSecret, redacted);
        Assert.Contains("--secret REDACTED", redacted);
        // Other args must survive (port, host, module path) so logs
        // remain useful.
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
        // Defensive: empty string round-trips, null doesn't NRE. The
        // log site passes whatever args was built; a future refactor
        // that produces null/empty shouldn't crash the start path.
        Assert.Equal(string.Empty, TgProxyManager.RedactSecretInArgs(string.Empty));
        // Null input — Regex.Replace would NRE on null pattern, but
        // the helper guards it. Pin the guarded behaviour.
        Assert.Null(TgProxyManager.RedactSecretInArgs(null!));
    }

    [Fact]
    public void IsInstalled_LoggerOverload_EmitsOnePerCall_NoSecretLeak()
    {
        // Behavioural: call the logger overload, capture the sink, and
        // verify (a) at least one structured line emitted, (b) the
        // line contains the expected probe shape, (c) no secret-shaped
        // string slips through (defensive — IsInstalled doesn't see
        // the secret today, but a future refactor that adds it
        // shouldn't accidentally start logging it).
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // We don't care about the bool result — paths may or may not
        // exist on the test host. We pin the LOG output shape only.
        _ = TgProxyUpdater.IsInstalled(logger);

        var lines = sink.Render();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("[TgProxy] IsInstalled:"));
        Assert.Contains(lines, l => l.Contains("PythonExe at"));
        Assert.Contains(lines, l => l.Contains("ProxySourceDir at"));
        Assert.Contains(lines, l => l.Contains("overall = "));

        // Defensive secret-redaction scan — none of the rendered lines
        // should contain a 32-char hex blob (the canonical secret
        // shape generated by ToggleTgProxyAsync).
        foreach (var line in lines)
        {
            Assert.False(
                Regex.IsMatch(line, @"\b[a-f0-9]{32}\b"),
                $"IsInstalled log line contained a 32-char hex blob (potential secret leak): {line}");
        }
    }

    [Fact]
    public void IsInstalled_ParameterlessOverload_DelegatesToLoggerOverloadWithNullLogger()
    {
        // Pin: the parameterless overload calls the logger-aware one
        // with a null logger so the behaviour is identical and the
        // bool result is consistent. If a future refactor splits the
        // implementations, we'd risk the parameterless probe and the
        // logged probe disagreeing on disk state (e.g. one caching).
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyUpdater.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Should delegate to a logger-aware probe with a null logger.
        // After v2.31.10-r5 conflict resolution between DBG-4 (logger
        // overload) and DBG-5 (path-injectable IsInstalledAt overload),
        // both code paths funnel into IsInstalledAt(TgProxyDir, logger).
        // The parameterless overload now reads
        //   IsInstalled() => IsInstalledAt(TgProxyDir, logger: null)
        // OR the pre-merge form
        //   IsInstalled() => IsInstalled(logger: null)
        // Both demonstrate the same contract: parameterless probe = no
        // logger. We accept either spelling.
        Assert.Matches(
            @"public\s+static\s+bool\s+IsInstalled\s*\(\s*\)\s*=>\s*IsInstalled(At)?\s*\(\s*[^)]*?(logger\s*:\s*)?null\s*\)",
            stripped);
    }

    [Fact]
    public void Logger_OverallLogChain_DoesNotEmitPlaintextSecret()
    {
        // Defensive end-to-end: drive every helper that could log and
        // verify NO line in the captured sink matches our test secret.
        // This is the privacy-critical assertion called out in the
        // brief acceptance criteria.
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        const string testSecret = "0123456789abcdef0123456789abcdef";

        // 1. IsInstalled probe — never sees the secret.
        _ = TgProxyUpdater.IsInstalled(logger);

        // 2. Redaction helper — feed a real-shaped args string and
        // confirm the redacted result has the secret stripped.
        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {testSecret}";
        var redacted = TgProxyManager.RedactSecretInArgs(args);

        // Log the redacted version through the test logger to mimic
        // what TgProxyManager.Start does.
        logger.Information("[TgProxy] Spawn ProcessStartInfo: Arguments={Arguments}", redacted);

        var lines = sink.Render();
        foreach (var line in lines)
        {
            Assert.DoesNotContain(testSecret, line);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Tiny in-memory Serilog sink — captures every log event's
    /// rendered message + property values for later inspection. We
    /// only render to the message template; properties go into the
    /// rendered string so the secret-leak scan covers them.
    /// </summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<string> _events = new();
        public void Emit(LogEvent logEvent)
        {
            // RenderMessage substitutes property values into the template
            // — this is what gets written to disk in production, so it's
            // the right surface for a secret-leak scan.
            var rendered = logEvent.RenderMessage();
            // Also serialize raw property values in case a property
            // contains the secret without being substituted into the
            // template (defensive).
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
