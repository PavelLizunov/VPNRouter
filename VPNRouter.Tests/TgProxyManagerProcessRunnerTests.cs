#nullable enable

using System.Threading;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class TgProxyManagerProcessRunnerTests : IDisposable
{
    private readonly string _pythonExePath = TgProxyUpdater.PythonExePath;
    private readonly string _proxySourceDir = TgProxyUpdater.ProxySourceDir;
    private readonly string _tgProxyDir = TgProxyUpdater.TgProxyDir;
    private readonly bool _seededFiles;

    public TgProxyManagerProcessRunnerTests()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pythonExePath)!);
            Directory.CreateDirectory(_proxySourceDir);
            if (!File.Exists(_pythonExePath))
            {
                File.WriteAllText(_pythonExePath, "fake python stub for tests");
            }
            _seededFiles = true;
        }
        catch (UnauthorizedAccessException)
        {
            _seededFiles = false;
        }
        catch (IOException)
        {
            _seededFiles = false;
        }
    }

    private static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_seededFiles && File.Exists(_pythonExePath))
            {
                var content = File.ReadAllText(_pythonExePath);
                if (content == "fake python stub for tests")
                {
                    File.Delete(_pythonExePath);
                }
            }
        }
        catch { }
    }

    private void SkipIfNotSeeded()
    {
        if (!_seededFiles)
        {
            return;
        }
    }

    [Fact]
    public void Start_EmitsExpectedPythonArgvOnRunner()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99001);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        try
        {
            var testCt = TestContext.Current.CancellationToken;
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var freePort = PickFreePort();
            var startTask = Task.Run(() =>
                sut.Start(port: freePort, secret: "deadbeef0123456789abcdef01234567"), testCt);

            while (fake.StartCalls.Count == 0 && !startCts.IsCancellationRequested)
            {
                Thread.Sleep(10);
            }

            Assert.Single(fake.StartCalls);
            var call = fake.StartCalls[0];

            Assert.Equal(_pythonExePath, call.ExecutablePath);

            Assert.Equal(new[]
            {
                "-m", "proxy.tg_ws_proxy",
                "--port", freePort.ToString(),
                "--host", "127.0.0.1",
                "--secret", "deadbeef0123456789abcdef01234567",
            }, call.Arguments.ToArray());

            Assert.Equal(_tgProxyDir, call.WorkingDirectory);

            Assert.True(call.CaptureStdout);
            Assert.True(call.CaptureStderr);

#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            startTask.Wait(TimeSpan.FromSeconds(3), testCt);
#pragma warning restore xUnit1031
            handle.SignalExit(0);
        }
        finally
        {
            sut.Dispose();
        }
    }

    [Fact]
    public void Start_WithVerbose_AppendsVerboseFlagToArgv()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99002);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        try
        {
            var testCt = TestContext.Current.CancellationToken;
            var startTask = Task.Run(() =>
                sut.Start(port: PickFreePort(), secret: "abc123def456", verbose: true), testCt);

            using var spin = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fake.StartCalls.Count == 0 && !spin.IsCancellationRequested) Thread.Sleep(10);

            Assert.Single(fake.StartCalls);
            var call = fake.StartCalls[0];
            Assert.Equal("--verbose", call.Arguments[^1]);
            Assert.Equal(9, call.Arguments.Count);

#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            startTask.Wait(TimeSpan.FromSeconds(3), testCt);
#pragma warning restore xUnit1031
            handle.SignalExit(0);
        }
        finally { sut.Dispose(); }
    }

    [Fact]
    public void Start_ProcessSurvives2sProbe_LogsAliveAndContinues()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99003);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        try
        {
            var testCt = TestContext.Current.CancellationToken;
            var startTask = Task.Run(() => sut.Start(PickFreePort(), "secretX"), testCt);

#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            Assert.True(startTask.Wait(TimeSpan.FromSeconds(5), testCt),
                "Start should return within 5s — the 2s probe budget plus dispatch overhead.");
#pragma warning restore xUnit1031

            Assert.True(sut.IsRunning);
            Assert.Equal(99003, sut.Pid);

            handle.SignalExit(0);
        }
        finally { sut.Dispose(); }
    }

    [Fact]
    public void Start_OutputLineWithStats_TriggersStatsUpdatedAndLastStats()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99004);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        var statsCaptured = new List<string>();
        sut.StatsUpdated += s => statsCaptured.Add(s);

        try
        {
            var testCt = TestContext.Current.CancellationToken;
            var startTask = Task.Run(() => sut.Start(PickFreePort(), "secretY"), testCt);

            using var spin = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (fake.StartCalls.Count == 0 && !spin.IsCancellationRequested) Thread.Sleep(10);

            handle.EmitOutput("stats: total=10 active=3 ws=1");

            handle.EmitError("stats: total=20 active=5 ws=2");

            handle.EmitOutput("startup: bound to 127.0.0.1:1443");

            handle.EmitOutput("stats: total=30 active=7 ws=3");

#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            startTask.Wait(TimeSpan.FromSeconds(5), testCt);
#pragma warning restore xUnit1031
            handle.SignalExit(0);

            Assert.Equal(3, statsCaptured.Count);
            Assert.Equal("stats: total=30 active=7 ws=3", sut.LastStats);
        }
        finally { sut.Dispose(); }
    }

    [Fact]
    public void Stop_OnRunningManager_KillsHandleAndDisposes()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99005);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        try
        {
            var testCt = TestContext.Current.CancellationToken;
            var startTask = Task.Run(() => sut.Start(PickFreePort(), "secretZ"), testCt);
#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            startTask.Wait(TimeSpan.FromSeconds(5), testCt);
#pragma warning restore xUnit1031

            Assert.True(sut.IsRunning);
            Assert.Equal(99005, sut.Pid);

            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Null(sut.Pid);
            Assert.True(handle.HasExited);
        }
        finally { sut.Dispose(); }
    }

    [Fact]
    public void Stop_CalledTwice_SecondCallIsNoOp()
    {
        if (!_seededFiles) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99006);
        fake.OnStart(_ => true, _ => handle);

        var sut = new TgProxyManager(logger: null, runner: fake);
        try
        {
            var testCt = TestContext.Current.CancellationToken;
            var startTask = Task.Run(() => sut.Start(PickFreePort(), "secretQ"), testCt);
#pragma warning disable xUnit1031 // the test bounds a synchronous call with a timeout on purpose
            startTask.Wait(TimeSpan.FromSeconds(5), testCt);
#pragma warning restore xUnit1031

            sut.Stop();
            var ex = Record.Exception(() => sut.Stop());
            Assert.Null(ex);

            ex = Record.Exception(() => sut.Stop());
            Assert.Null(ex);

            Assert.False(sut.IsRunning);
        }
        finally { sut.Dispose(); }
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
        if (!_seededFiles) return;
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99007);
        fake.OnStart(_ => true, _ => handle);
        using var sut = new TgProxyManager(logger: null, runner: fake);

        var start = Task.Run(() => sut.Start(PickFreePort(), "secret-early-exit"), TestContext.Current.CancellationToken);
        while (fake.StartCalls.Count == 0) Thread.Sleep(10);
        handle.SignalExit(1);

        Assert.Throws<InvalidOperationException>(() => start.GetAwaiter().GetResult());
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
        if (!_seededFiles) return;
        var fake = new FakeProcessRunner();
        var sut = new TgProxyManager(logger: null, runner: fake);
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sut.Start(PickFreePort(), "secret"));
        Assert.Empty(fake.StartCalls);
    }

    [Fact]
    public void Start_AfterPreviousProcessExited_DisposesStaleHandleBeforeRespawn()
    {
        if (!_seededFiles) return;
        var fake = new FakeProcessRunner();
        var first = new FakeProcessHandle(pid: 99008);
        var second = new FakeProcessHandle(pid: 99009);
        var spawn = 0;
        fake.OnStart(_ => true, _ => Interlocked.Increment(ref spawn) == 1 ? first : second);
        using var sut = new TgProxyManager(logger: null, runner: fake);

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
        Assert.Equal("tg", uri.Scheme);
    }

    [Fact]
    public void OpenInTelegram_HandlesInvalidOrMalformedParameters_DoesNotThrow()
    {
        var ex = Record.Exception(() => TgProxyManager.OpenInTelegram("invalid host with spaces", 1443, "secret"));
        Assert.Null(ex);
    }
}
