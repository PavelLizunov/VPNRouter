#nullable enable

using System.Threading;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class TgProxyManagerProcessRunnerTests : IDisposable
{
    private readonly string _tgProxyDir = Path.Combine(Path.GetTempPath(), "tgproxy-manager-" + Guid.NewGuid().ToString("N"));
    private string PythonExePath => Path.Combine(_tgProxyDir, "python", "python.exe");

    public TgProxyManagerProcessRunnerTests() => AutostartContractTests.SeedPinnedRuntime(_tgProxyDir);

    private static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => Directory.Delete(_tgProxyDir, recursive: true);

    [Fact]
    public void UpdateLockPreventsLaunchBeforeSpawn()
    {
        var runner = new FakeProcessRunner();
        using var manager = new TgProxyManager(null, runner, _tgProxyDir);
        using var updateLock = TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: true);

        Assert.ThrowsAny<IOException>(() => manager.Start(PickFreePort(), "test"));
        Assert.Empty(runner.StartCalls);
    }

    [Fact]
    public void TwoLaunchLocksExcludeAnUpdateUntilBothAreReleased()
    {
        using (var first = TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: false))
        {
            using (TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: false))
                Assert.ThrowsAny<IOException>(() => TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: true));
            Assert.ThrowsAny<IOException>(() => TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: true));
        }
        using var updateLock = TgProxyUpdater.LockRuntimeAt(_tgProxyDir, updating: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_EmitsExpectedPythonArgvOnRunner(bool verbose)
    {
        var handle = new FakeProcessHandle();
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => handle);
        using var manager = new TgProxyManager(null, runner, _tgProxyDir);
        var port = PickFreePort();
        manager.Start(port, "test", verbose);

        var call = Assert.Single(runner.StartCalls);
        Assert.Equal(PythonExePath, call.ExecutablePath);
        var expected = new List<string> { "-m", "proxy.tg_ws_proxy", "--port", port.ToString(),
            "--host", "127.0.0.1", "--secret", "test" };
        if (verbose) expected.Add("--verbose");
        Assert.Equal(expected, call.Arguments.ToList());
        Assert.Equal(_tgProxyDir, call.WorkingDirectory);
        Assert.True(call.CaptureStdout);
        Assert.True(call.CaptureStderr);
        Assert.True(manager.IsRunning);
        Assert.Equal(handle.Pid, manager.Pid);
    }

    [Fact]
    public void Start_OutputLineWithStats_TriggersStatsUpdatedAndLastStats()
    {
        var handle = new FakeProcessHandle();
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => handle);
        using var manager = new TgProxyManager(null, runner, _tgProxyDir);
        var stats = new List<string>();
        manager.StatsUpdated += stats.Add;
        manager.Start(PickFreePort(), "test");

        handle.EmitOutput("stats: total=10 active=3 ws=1");
        handle.EmitError("stats: total=20 active=5 ws=2");
        handle.EmitOutput("startup: bound to 127.0.0.1:1443");
        handle.EmitOutput("stats: total=30 active=7 ws=3");

        Assert.Equal(3, stats.Count);
        Assert.Equal("stats: total=30 active=7 ws=3", manager.LastStats);
    }

    [Fact]
    public void Stop_OnRunningManager_KillsHandleAndIsIdempotent()
    {
        var handle = new FakeProcessHandle();
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => handle);
        using var manager = new TgProxyManager(null, runner, _tgProxyDir);
        manager.Start(PickFreePort(), "test");

        manager.Stop();
        manager.Stop();

        Assert.Equal(1, handle.KillCallCount);
        Assert.Equal(1, handle.DisposeCallCount);
        Assert.True(handle.HasExited);
        Assert.False(manager.IsRunning);
        Assert.Null(manager.Pid);
    }

    [Fact]
    public void RedactSecretInArgs_StillSanitisesLegacyArgsString()
    {
        const string realSecret = "0123456789abcdef0123456789abcdef";
        var args = $"-m proxy.tg_ws_proxy --port 1443 --host 127.0.0.1 --secret {realSecret} --verbose";

        var redacted = TgProxyManager.RedactSecretInArgs(args);

        Assert.DoesNotContain(realSecret, redacted);
        Assert.Contains("--secret REDACTED", redacted);
        Assert.Contains("--verbose", redacted);
    }

    [Fact]
    public void Start_EarlyExit_ThrowsAndClearsRunningState()
    {
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99007);
        fake.OnStart(_ => true, _ => handle);
        using var sut = new TgProxyManager(null, fake, _tgProxyDir);

        handle.SignalExit(1);
        Assert.Throws<InvalidOperationException>(() => sut.Start(PickFreePort(), "secret-early-exit"));
        Assert.False(sut.IsRunning);
        Assert.Null(sut.Pid);
    }

    [Fact]
    public void RedactSensitiveOutput_RemovesPlainAndUrlSecrets()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var output = $"Secret: {secret} tg://proxy?server=127.0.0.1&secret=dd{secret}";

        var redacted = TgProxyManager.RedactSensitiveOutput(output, secret);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("REDACTED", redacted);
    }

    [Fact]
    public void Start_AfterDispose_ThrowsBeforeSpawn()
    {
        var fake = new FakeProcessRunner();
        var sut = new TgProxyManager(null, fake, _tgProxyDir);
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sut.Start(PickFreePort(), "secret"));
        Assert.Empty(fake.StartCalls);
    }

    [Fact]
    public void Start_AfterPreviousProcessExited_DisposesStaleHandleBeforeRespawn()
    {
        var fake = new FakeProcessRunner();
        var first = new FakeProcessHandle(pid: 99008);
        var second = new FakeProcessHandle(pid: 99009);
        var spawn = 0;
        fake.OnStart(_ => true, _ => Interlocked.Increment(ref spawn) == 1 ? first : second);
        using var sut = new TgProxyManager(null, fake, _tgProxyDir);

        sut.Start(PickFreePort(), "first-secret");
        first.SignalExit(1);

        sut.Start(PickFreePort(), "second-secret");

        Assert.Equal(1, first.DisposeCallCount);
        Assert.Equal(2, fake.StartCalls.Count);
        Assert.Equal(99009, sut.Pid);
        second.SignalExit(0);
    }

    [Fact]
    public void BuildProxyLink_GeneratesValidTgProxyUri()
    {
        var link = TgProxyManager.BuildProxyLink("127.0.0.1", 1443, "abc123secret");
        Assert.StartsWith("tg://proxy?", link);
        Assert.True(Uri.TryCreate(link, UriKind.Absolute, out var uri));
        Assert.Equal("tg", uri!.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1\r\n", "secret")]
    [InlineData("127.0.0.1", "secret\"injected")]
    [InlineData("127.0.0.1 ", "secret")]
    [InlineData("", "secret")]
    [InlineData("127.0.0.1", "secret\n")]
    public void OpenInTelegram_RejectsInvalidHostOrSecret(string host, string secret)
    {
        var ex = Record.Exception(() => TgProxyManager.OpenInTelegram(host, 1443, secret));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void OpenInTelegram_InvalidPort_RejectsAndDoesNotLaunch(int invalidPort)
    {
        var ex = Record.Exception(() => TgProxyManager.OpenInTelegram("127.0.0.1", invalidPort, "abc123secret"));
        Assert.Null(ex);
    }
}
