#nullable enable

using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class HostsManagerProcessRunnerWireShapeTests
{
    private const string FakeHostsPath = @"C:\Test\Windows\System32\drivers\etc\hosts";

    private static HostsManager NewManager(InMemoryFileSystem fs, FakeProcessRunner runner)
        => new(fs, FakeHostsPath, http: null, runner: runner);

    private static ProcessResult Ok() =>
        new(ExitCode: 0, Stdout: "Windows IP Configuration\r\nSuccessfully flushed the DNS Resolver Cache.\r\n",
            Stderr: "", Duration: TimeSpan.FromMilliseconds(50), TimedOut: false);

    [Fact]
    public void Install_OnSuccess_CallsIpconfigFlushdnsWithExpectedArgv()
    {
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

    [Fact]
    public void Uninstall_OnSuccess_AlsoCallsIpconfigFlushdns()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, Ok());

        var sut = NewManager(fs, fake);
        sut.InstallInstance();
        var (ok, _) = sut.UninstallInstance();

        Assert.True(ok);
        Assert.Equal(2, fake.RunCalls.Count);
        foreach (var call in fake.RunCalls)
        {
            Assert.Equal("ipconfig", call.ExecutablePath);
            Assert.Equal(new[] { "/flushdns" }, call.Arguments.ToArray());
        }
    }

    [Fact]
    public void Install_WhenIpconfigTimesOut_StillReportsSuccess()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(ExitCode: -1, Stdout: "", Stderr: "",
                Duration: TimeSpan.FromMilliseconds(5000), TimedOut: true));

        var (ok, _) = NewManager(fs, fake).InstallInstance();

        Assert.True(ok, "DNS flush timeout must not propagate as install failure");
        Assert.Contains("# === VPNRouter Discord hosts START ===", fs.ReadAllText(FakeHostsPath));
    }

    [Fact]
    public void Install_AlreadyInstalled_DoesNotInvokeIpconfig()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, Ok());

        var sut = NewManager(fs, fake);
        sut.InstallInstance();
        var (ok, msg) = sut.InstallInstance();

        Assert.True(ok);
        Assert.Equal("Already installed", msg);
        Assert.Single(fake.RunCalls);
    }
}
