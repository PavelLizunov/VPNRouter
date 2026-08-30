#nullable enable
// ============================================================================
// ZapretActionsTests.cs — Phase 2G sub-wave 7b-2 (2026-05-18)
// ============================================================================
//
// Tests for the largest previously-untested service in VPNRouter.Core (562
// LOC). Brief: plans/phase2-2G-untested-services-2026-05-17.md.
//
// Coverage:
//   * IProcessRunner-routed methods: IsServiceRunning, ServiceExists,
//     IsAnyServiceMatching, RunSc, RunNetsh. Migrated in this commit.
//   * Cygwin .bat regression: ZapretManager.BuildCygwinLaunchBat
//     (extracted helper) pins the SET BIN= / SET LISTS= contract from
//     the v2.9.x Cygwin launch lesson.
//   * Strategy parser: ZapretUpdater.ExtractWinwsArgsFromLines (pure
//     helper extracted in this commit).
// ============================================================================

using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// xUnit suite for <see cref="ZapretActions"/>. Each test sets up its own
/// <see cref="FakeProcessRunner"/> for tight isolation, then resets the
/// shared seam back to the default <see cref="ProcessRunner"/>. Tests are
/// sealed and live in the same assembly via InternalsVisibleTo so we can
/// call the internal helpers directly.
/// </summary>
public sealed class ZapretActionsTests : IDisposable
{
    // Snapshot the existing process runner before we override so a teardown
    // can restore it. This protects parallel test classes from picking up
    // our FakeProcessRunner instance (xUnit may run classes in parallel
    // by default; ZapretActions._processRunner is static).
    private readonly IProcessRunner _originalRunner;

    public ZapretActionsTests()
    {
        _originalRunner = ZapretActions.ProcessRunner;
    }

    public void Dispose()
    {
        // Restore the production-default runner so subsequent test classes
        // (e.g. an integration test running real `sc`) don't inherit our
        // fake.
        ZapretActions.ProcessRunner = _originalRunner;
    }

    // ── helpers ──

    private static ProcessResult Ok(string stdout) =>
        new(ExitCode: 0, Stdout: stdout, Stderr: "",
            Duration: TimeSpan.FromMilliseconds(5), TimedOut: false);

    private static ProcessResult Fail(int exitCode, string stderr = "") =>
        new(ExitCode: exitCode, Stdout: "", Stderr: stderr,
            Duration: TimeSpan.FromMilliseconds(5), TimedOut: false);

    // Canned `sc query` outputs that match what Windows emits in practice.
    // Pinning the exact wording protects against an accidental parser
    // regression where someone fuzzily matches "Run" instead of "RUNNING".
    private const string ScQueryRunning =
        "SERVICE_NAME: BFE\r\n" +
        "        TYPE               : 20 WIN32_SHARE_PROCESS\r\n" +
        "        STATE              : 4  RUNNING\r\n" +
        "                                (STOPPABLE, NOT_PAUSABLE, ACCEPTS_SHUTDOWN)\r\n" +
        "        WIN32_EXIT_CODE    : 0  (0x0)\r\n";

    private const string ScQueryStopped =
        "SERVICE_NAME: zapret\r\n" +
        "        TYPE               : 10 WIN32_OWN_PROCESS\r\n" +
        "        STATE              : 1  STOPPED\r\n" +
        "        WIN32_EXIT_CODE    : 0  (0x0)\r\n";

    // ── 1. Service detection: RUNNING → true ──

    [Fact]
    public void IsServiceRunning_OutputContainsRunning_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "sc"
              && r.Arguments.Count == 2
              && r.Arguments[0] == "query"
              && r.Arguments[1] == "BFE",
            Ok(ScQueryRunning));
        ZapretActions.ProcessRunner = fake;

        var actual = ZapretActions.IsServiceRunning("BFE");

        Assert.True(actual);
        // Pin the runner shape so a future regression where someone
        // unconditionally adds e.g. `state=` would surface here.
        Assert.Single(fake.RunCalls);
        var req = fake.RunCalls[0];
        Assert.Equal("sc", req.ExecutablePath);
        Assert.Equal(new[] { "query", "BFE" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), req.Timeout);
        Assert.True(req.CaptureStdout, "must capture stdout to parse RUNNING token");
    }

    // ── 2. Service detection: STOPPED → false (NOT a false positive) ──

    [Fact]
    public void IsServiceRunning_OutputContainsStoppedOnly_ReturnsFalse()
    {
        // Regression: legacy parser used to check for "STATE" presence,
        // which would falsely match a STOPPED service's status block.
        // The correct contract is "matches 'RUNNING' substring".
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc", Ok(ScQueryStopped));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsServiceRunning("zapret"));
    }

    // ── 3. Service detection: process throws (e.g. missing sc.exe) → false ──

    [Fact]
    public void IsServiceRunning_RunnerThrows_ReturnsFalse()
    {
        // FakeProcessRunner without a matching predicate throws
        // InvalidOperationException — this exercises the catch-all `return false`
        // in IsServiceRunning, mirroring "sc.exe not on PATH" on a stripped
        // Windows install or a non-Windows host. Behavioural contract:
        // ZapretActions must NEVER let an exception escape to the diagnostic
        // pipeline; the per-svc line would just say "not running" silently.
        var fake = new FakeProcessRunner(); // no OnRun() registered
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsServiceRunning("any-svc"));
    }

    // ── 4. ServiceExists: positive (SERVICE_NAME header in stdout) ──

    [Fact]
    public void ServiceExists_OutputContainsServiceName_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc", Ok(ScQueryStopped));
        ZapretActions.ProcessRunner = fake;

        // Note: ServiceExists checks for SERVICE_NAME *or* STATE — so even a
        // stopped service counts as "exists". That's the right semantic for
        // StopDeleteServiceLineAsync which then unconditionally calls
        // `sc stop` + `sc delete` to clean it up.
        Assert.True(ZapretActions.ServiceExists("zapret"));
    }

    // ── 5. ServiceExists: 1060-style "not found" output → false ──

    [Fact]
    public void ServiceExists_EmptyOrErrorOutput_ReturnsFalse()
    {
        // Windows emits "[SC] EnumQueryServicesStatus:OpenService FAILED 1060"
        // to stderr (not stdout) when a service doesn't exist. The parser
        // only inspects stdout, so a missing service produces empty stdout
        // and the SERVICE_NAME/STATE tokens are absent.
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "sc",
            Fail(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.ServiceExists("nonexistent-svc"));
    }

    // ── 6. IsAnyServiceMatching: substring glob hits → true ──

    [Fact]
    public void IsAnyServiceMatching_OutputContainsSubstring_ReturnsTrue()
    {
        // Simulate `sc query state= all` dumping every service. The diagnostic
        // pipeline calls this with "vpn" to spot conflicting VPN services
        // (RuVPN, NordVPN, etc.). Match is case-insensitive substring.
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "sc"
              && r.Arguments.Count == 3
              && r.Arguments[0] == "query"
              && r.Arguments[1] == "state="
              && r.Arguments[2] == "all",
            Ok("SERVICE_NAME: NordVPN-Service\r\nSTATE: 4 RUNNING\r\n"));
        ZapretActions.ProcessRunner = fake;

        Assert.True(ZapretActions.IsAnyServiceMatching("vpn"));

        // Verify the arg-list shape — `state=` and `all` are distinct tokens.
        // sing-box-style argument splitting must not collapse them into one.
        var req = fake.RunCalls[0];
        Assert.Equal(new[] { "query", "state=", "all" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(3), req.Timeout);
    }

    // ── 7. IsAnyServiceMatching: substring miss → false ──

    [Fact]
    public void IsAnyServiceMatching_OutputMissesSubstring_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc",
            Ok("SERVICE_NAME: DnsCache\r\nSERVICE_NAME: BFE\r\n"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsAnyServiceMatching("zapret"));
    }

    // ── 8. RunSc: ProcessRequest shape (executable, args, timeout) ──

    [Fact]
    public async Task RunSc_PassesParsedArgsAndTimeout()
    {
        // Pin the contract that the legacy shell-string "stop zapret" gets
        // split into ["stop", "zapret"] and routed to "sc" with the 5s
        // timeout. Quotes in legacy strings would break here — the brief
        // notes this is acceptable because actual callers only pass simple
        // verb+name without spaces.
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc", Ok(""));
        ZapretActions.ProcessRunner = fake;

        await ZapretActions.RunSc("stop zapret");

        Assert.Single(fake.RunCalls);
        var req = fake.RunCalls[0];
        Assert.Equal("sc", req.ExecutablePath);
        Assert.Equal(new[] { "stop", "zapret" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(5), req.Timeout);
    }

    // ── 9. RunNetsh: success exit-code path + stdout out-param ──

    [Fact]
    public void RunNetsh_ZeroExitCode_ReturnsTrueAndPopulatesOutput()
    {
        // CheckTcpTimestampsLine relies on RunNetsh capturing stdout so it
        // can grep for "Timestamps: enabled". Pin both: returns true on
        // exit 0 + stdout flows through the `out string output` param.
        const string netshOut =
            "Querying active state...\r\n" +
            "TCP Global Parameters\r\n" +
            "----------------------------------------------\r\n" +
            "Timestamps                          : enabled\r\n";
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "netsh"
              && r.Arguments.SequenceEqual(new[] { "interface", "tcp", "show", "global" }),
            Ok(netshOut));
        ZapretActions.ProcessRunner = fake;

        var ok = ZapretActions.RunNetsh("interface tcp show global", out var captured);

        Assert.True(ok);
        Assert.Contains("Timestamps", captured);
        Assert.Contains("enabled", captured);
        // Verify the arg-split contract: shell-string → ArgumentList tokens
        // with embedded spaces collapsed (StringSplitOptions.RemoveEmptyEntries).
        var req = fake.RunCalls[0];
        Assert.Equal("netsh", req.ExecutablePath);
        Assert.Equal(new[] { "interface", "tcp", "show", "global" }, req.Arguments.ToArray());
    }

    // ── 10. RunNetsh: nonzero exit-code surfaces failure ──

    [Fact]
    public void RunNetsh_NonzeroExitCode_ReturnsFalseButStillPopulatesOutput()
    {
        // netsh emits diagnostics on stdout even when failing (e.g. policy
        // not configured = exit 1 + "The system cannot find the file specified").
        // The contract is: callers see the exit code via the bool return, but
        // can still read the partial stdout for diagnostic display.
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "netsh",
            new ProcessResult(
                ExitCode: 1,
                Stdout: "The system cannot find the file specified.\r\n",
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(5),
                TimedOut: false));
        ZapretActions.ProcessRunner = fake;

        var ok = ZapretActions.RunNetsh("dnsclient show state", out var captured);

        Assert.False(ok);
        Assert.Contains("cannot find", captured);
    }

    // ── 11. Cygwin .bat regression-prevention (v2.9.x lesson) ──

    [Fact]
    public void BuildCygwinLaunchBat_UsesSetBinAndSetLists_NotLiteralPaths()
    {
        // Critical Cygwin contract: winws.exe needs SET %VARS% in .bat,
        // not literal Windows paths. hostfakesplit + SNI spoofing
        // (sni=www.google.com) is what makes Discord work (ALT3).
        //
        // This test pins the contract. The .bat content MUST contain:
        //   1. `set "BIN=<path>\"`   (Cygwin's POSIX resolver needs CMD var
        //                              expansion to handle the path)
        //   2. `set "LISTS=<path>\"` (same reason)
        //   3. `cd /d "%BIN%"`       (use the var, not literal path, when cd-ing)
        //   4. `winws.exe %args%`    (invocation should not embed binDir literal
        //                              as `<binDir>\winws.exe` — Cygwin breaks)
        //
        // Regression history: in v2.9.x we used to write `cd /d "<actual\\path>"`
        // and `<actual\\path>\winws.exe`. winws.exe printed "cannot access
        // file" and silently exited. The fix was to switch to SET %VARS%.
        const string fakeBinDir = @"C:\ProgramData\VPNRouter\zapret\bin";
        const string fakeListsDir = @"C:\ProgramData\VPNRouter\zapret\lists";
        const string args = "--wf-tcp=443 --dpi-desync=fake,split2";

        var bat = ZapretManager.BuildCygwinLaunchBat(fakeBinDir, fakeListsDir, args);

        // Critical assertion: SET BIN= must be present.
        Assert.Contains("set \"BIN=", bat);
        // Critical assertion: SET LISTS= must be present.
        Assert.Contains("set \"LISTS=", bat);
        // The cd must use %BIN% (CMD variable expansion), not the literal path.
        Assert.Contains("cd /d \"%BIN%\"", bat);
        // The literal binDir must NOT appear in a cd /d position — that
        // would be the regression case. (It DOES appear inside the
        // `set "BIN=..."` line, which is fine — CMD expands it correctly.)
        Assert.DoesNotContain($"cd /d \"{fakeBinDir}", bat);
        // The args must be present unchanged (no escaping).
        Assert.Contains(args, bat);
        // The trailing slash on SET values is required by downstream Flowseal
        // scripts that build paths via `%BIN%winws.exe`-style joins.
        Assert.Contains($"set \"BIN={fakeBinDir}{System.IO.Path.DirectorySeparatorChar}", bat);
        Assert.Contains($"set \"LISTS={fakeListsDir}{System.IO.Path.DirectorySeparatorChar}", bat);
    }

    // ── 12. Strategy parser: extracts winws args from Flowseal-shape .bat ──

    [Fact]
    public void ExtractWinwsArgsFromLines_SingleLine_StripsExeAndReturnsArgs()
    {
        // Synthesised general.bat shape from Flowseal upstream. Real release
        // .bats have `start "" "%BIN%winws.exe" --foo --bar` invocations,
        // often with line continuations (^). This test pins the
        // single-line happy path.
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 > nul",
            "start \"\" \"%BIN%winws.exe\" --wf-tcp=443 --dpi-desync=fake,split2 --dpi-desync-fooling=md5sig"
        };

        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            lines,
            binPath: "%BIN%",
            listsPath: "%LISTS%");

        Assert.NotNull(args);
        // Args should not include `winws.exe` or the wrapping quotes.
        Assert.DoesNotContain("winws.exe", args!);
        Assert.Contains("--wf-tcp=443", args);
        Assert.Contains("--dpi-desync=fake,split2", args);
        Assert.Contains("--dpi-desync-fooling=md5sig", args);
    }

    // ── 13. Strategy parser: handles ^ line continuation + var substitution ──

    [Fact]
    public void ExtractWinwsArgsFromLines_LineContinuation_JoinsAndSubstitutesPlaceholders()
    {
        // Real Flowseal .bats use `^` to continue commands across lines and
        // reference %BIN% / %LISTS% placeholders. Parser must:
        //   1. Stitch continuation lines together
        //   2. Substitute %BIN% / %LISTS% with the supplied resolved paths
        //   3. Strip game-filter-only --new segments (here we use the
        //      ipset-loaded form so no game filter to strip)
        var lines = new[]
        {
            "@echo off",
            "start \"\" \"%BIN%winws.exe\" --wf-tcp=443 ^",
            "--dpi-desync=fake,split2 ^",
            "--dpi-desync-fake-tls=\"%LISTS%tls_clienthello_www_google_com.bin\""
        };

        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            lines,
            binPath: "%BIN%",          // pass through unchanged
            listsPath: "/RESOLVED/lists/");

        Assert.NotNull(args);
        Assert.Contains("--wf-tcp=443", args!);
        Assert.Contains("--dpi-desync=fake,split2", args);
        // %LISTS% must have been substituted with our supplied path.
        Assert.Contains("/RESOLVED/lists/tls_clienthello_www_google_com.bin", args);
        Assert.DoesNotContain("%LISTS%", args);
    }
}
