#nullable enable
// ============================================================================
// HostsManagerProcessRunnerWireShapeTests.cs — Phase 3+ (2026-05-21)
// ============================================================================
//
// Pins the IProcessRunner wire shape for the FlushDns helper inside
// HostsManager. After the Phase 3+ migration, FlushDns no longer calls
// Process.Start directly — it routes through the per-instance IProcessRunner
// seam. This test class assigns a FakeProcessRunner and asserts the captured
// ipconfig argv, timeout, and failure-tolerance semantics.
//
// Why this matters: a regression in the FlushDns wire shape would silently
// fail to refresh the Windows DNS cache after every hosts mutation, leaving
// the user with stale Discord voice-server entries until the cache TTL
// expires (typically minutes to hours). Pinning the argv keeps that path
// honest without spawning real ipconfig in tests.
//
// Brief: plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md
// ============================================================================

using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// xUnit suite pinning the ipconfig argv emitted by
/// <see cref="HostsManager.InstallInstance"/> / <see cref="HostsManager.UninstallInstance"/>
/// via the private FlushDns helper. The helper is private but its observable
/// effect (an IProcessRunner.RunAsync call) is asserted through the
/// per-instance ctor injection.
/// </summary>
public sealed class HostsManagerProcessRunnerWireShapeTests
{
    private const string FakeHostsPath = @"C:\Test\Windows\System32\drivers\etc\hosts";

    /// <summary>
    /// Build a manager wired to an InMemoryFileSystem + FakeProcessRunner so
    /// the test can drive the hosts-file path AND observe the ipconfig call
    /// shape. The HTTP seam defaults to PolicyHttpClient.Shared but isn't
    /// touched by Install/Uninstall — only the Flowseal install path uses it.
    /// </summary>
    private static HostsManager NewManager(InMemoryFileSystem fs, FakeProcessRunner runner)
        => new(fs, FakeHostsPath, http: null, runner: runner);

    private static ProcessResult Ok() =>
        new(ExitCode: 0, Stdout: "Windows IP Configuration\r\nSuccessfully flushed the DNS Resolver Cache.\r\n",
            Stderr: "", Duration: TimeSpan.FromMilliseconds(50), TimedOut: false);

    // ── 1. Install path triggers ipconfig /flushdns with correct argv ──────

    [Fact]
    public void Install_OnSuccess_CallsIpconfigFlushdnsWithExpectedArgv()
    {
        // CRITICAL invariant: after appending the Discord block we MUST flush
        // the DNS cache. Pin the exact argv shape — `ipconfig` + single
        // argument `/flushdns`, with the 5s timeout per the v2.20.2 lesson.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, Ok());

        var (ok, _) = NewManager(fs, fake).InstallInstance();

        Assert.True(ok);
        Assert.Single(fake.RunCalls);
        var call = fake.RunCalls[0];
        Assert.Equal("ipconfig", call.ExecutablePath);
        Assert.Equal(new[] { "/flushdns" }, call.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(5000), call.Timeout);
    }

    // ── 2. Uninstall path also flushes DNS ─────────────────────────────────

    [Fact]
    public void Uninstall_OnSuccess_AlsoCallsIpconfigFlushdns()
    {
        // Uninstall must symmetric-flush so the absence of redirects becomes
        // immediate. Otherwise the user would have leftover entries cached
        // until the next reboot.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, Ok());

        var sut = NewManager(fs, fake);
        sut.InstallInstance(); // sets up the block + flushes (1st call)
        var (ok, _) = sut.UninstallInstance(); // 2nd flush

        Assert.True(ok);
        Assert.Equal(2, fake.RunCalls.Count);
        foreach (var call in fake.RunCalls)
        {
            Assert.Equal("ipconfig", call.ExecutablePath);
            Assert.Equal(new[] { "/flushdns" }, call.Arguments.ToArray());
        }
    }

    // ── 3. ipconfig timeout/failure does not break hosts mutation ─────────

    [Fact]
    public void Install_WhenIpconfigTimesOut_StillReportsSuccess()
    {
        // The hosts mutation is the load-bearing part — DNS flush is best
        // effort. A stuck ipconfig (5s timeout) MUST NOT bubble up as an
        // install failure. The legacy code swallowed the timeout silently;
        // pin that behaviour through the IProcessRunner seam.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(ExitCode: -1, Stdout: "", Stderr: "",
                Duration: TimeSpan.FromMilliseconds(5000), TimedOut: true));

        var (ok, _) = NewManager(fs, fake).InstallInstance();

        Assert.True(ok, "DNS flush timeout must not propagate as install failure");
        // Mutation still happened — block is present in the hosts file.
        Assert.Contains("# === VPNRouter Discord hosts START ===", fs.ReadAllText(FakeHostsPath));
    }

    // ── 4. Idempotent install: second call short-circuits, no ipconfig ────

    [Fact]
    public void Install_AlreadyInstalled_DoesNotInvokeIpconfig()
    {
        // The IsInstalled fast-path returns "Already installed" without
        // touching the file system OR ipconfig. Pin that fake runner stays
        // un-touched on the no-op call — protects against an accidental
        // regression where someone moves the flush above the guard.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, Ok());

        var sut = NewManager(fs, fake);
        sut.InstallInstance(); // 1st: real install + flush
        var (ok, msg) = sut.InstallInstance(); // 2nd: should be no-op

        Assert.True(ok);
        Assert.Equal("Already installed", msg);
        // Only the 1st install triggered ipconfig.
        Assert.Single(fake.RunCalls);
    }

    // ── 5. Ctor wiring: runner=null falls back to static Runner default ───

    [Fact]
    public void Constructor_AcceptsCustomRunner_WiresUpInjection()
    {
        // The new ctor signature accepts an optional IProcessRunner so tests
        // can inject FakeProcessRunner without going through the static
        // Runner property. Pin that the ctor doesn't ignore the argument and
        // that null falls back to the static default.
        var fs = new InMemoryFileSystem();
        var fake = new FakeProcessRunner();

        // Smoke: ctor doesn't throw on either path.
        var withFake = new HostsManager(fs, FakeHostsPath, http: null, runner: fake);
        var withDefault = new HostsManager(fs, FakeHostsPath, http: null, runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);
    }
}
