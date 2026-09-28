#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretActionsTests : IDisposable
{
    private readonly IProcessRunner _originalRunner;

    public ZapretActionsTests()
    {
        _originalRunner = ZapretActions.ProcessRunner;
    }

    public void Dispose()
    {
        ZapretActions.ProcessRunner = _originalRunner;
    }

    private static ProcessResult Ok(string stdout) =>
        new(ExitCode: 0, Stdout: stdout, Stderr: "",
            Duration: TimeSpan.FromMilliseconds(5), TimedOut: false);

    private static ProcessResult Fail(int exitCode, string stderr = "") =>
        new(ExitCode: exitCode, Stdout: "", Stderr: stderr,
            Duration: TimeSpan.FromMilliseconds(5), TimedOut: false);

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
        Assert.Single(fake.RunCalls);
        var req = fake.RunCalls[0];
        Assert.Equal("sc", req.ExecutablePath);
        Assert.Equal(new[] { "query", "BFE" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), req.Timeout);
        Assert.True(req.CaptureStdout, "must capture stdout to parse RUNNING token");
    }

    [Fact]
    public void IsServiceRunning_OutputContainsStoppedOnly_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc", Ok(ScQueryStopped));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsServiceRunning("zapret"));
    }

    [Fact]
    public void IsServiceRunning_RunnerThrows_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsServiceRunning("any-svc"));
    }

    [Fact]
    public void ServiceExists_OutputContainsServiceName_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc", Ok(ScQueryStopped));
        ZapretActions.ProcessRunner = fake;

        Assert.True(ZapretActions.ServiceExists("zapret"));
    }

    [Fact]
    public void ServiceExists_EmptyOrErrorOutput_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "sc",
            Fail(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.ServiceExists("nonexistent-svc"));
    }

    [Fact]
    public void IsAnyServiceMatching_OutputContainsSubstring_ReturnsTrue()
    {
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

        var req = fake.RunCalls[0];
        Assert.Equal(new[] { "query", "state=", "all" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(3), req.Timeout);
    }

    [Fact]
    public void IsAnyServiceMatching_OutputMissesSubstring_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "sc",
            Ok("SERVICE_NAME: DnsCache\r\nSERVICE_NAME: BFE\r\n"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsAnyServiceMatching("zapret"));
    }

    [Fact]
    public async Task RunSc_PassesParsedArgsAndTimeout()
    {
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

    [Fact]
    public void RunNetsh_ZeroExitCode_ReturnsTrueAndPopulatesOutput()
    {
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
        var req = fake.RunCalls[0];
        Assert.Equal("netsh", req.ExecutablePath);
        Assert.Equal(new[] { "interface", "tcp", "show", "global" }, req.Arguments.ToArray());
    }

    [Fact]
    public void RunNetsh_NonzeroExitCode_ReturnsFalseButStillPopulatesOutput()
    {
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

    [Fact]
    public void BuildCygwinLaunchBat_UsesSetBinAndSetLists_NotLiteralPaths()
    {
        const string fakeBinDir = @"C:\ProgramData\VPNRouter\zapret\bin";
        const string fakeListsDir = @"C:\ProgramData\VPNRouter\zapret\lists";
        const string args = "--wf-tcp=443 --dpi-desync=fake,split2";

        var bat = ZapretManager.BuildCygwinLaunchBat(fakeBinDir, fakeListsDir, args);

        Assert.Contains("set \"BIN=", bat);
        Assert.Contains("set \"LISTS=", bat);
        Assert.Contains("cd /d \"%BIN%\"", bat);
        Assert.DoesNotContain($"cd /d \"{fakeBinDir}", bat);
        Assert.Contains(args, bat);
        Assert.Contains($"set \"BIN={fakeBinDir}{System.IO.Path.DirectorySeparatorChar}", bat);
        Assert.Contains($"set \"LISTS={fakeListsDir}{System.IO.Path.DirectorySeparatorChar}", bat);
    }

    [Theory]
    [InlineData("& whoami > C:\\pwn.txt")]
    [InlineData("| powershell.exe -c calc.exe")]
    [InlineData("arg \r\n net user hacker password /add")]
    [InlineData("arg ^ echo hello")]
    [InlineData("arg < input.txt")]
    [InlineData("arg > output.txt")]
    [InlineData("arg %EVIL%")]
    public void BuildCygwinLaunchBat_RejectsShellMetacharacters(string maliciousArgs)
    {
        const string fakeBinDir = @"C:\ProgramData\VPNRouter\zapret\bin";
        const string fakeListsDir = @"C:\ProgramData\VPNRouter\zapret\lists";

        var ex = Assert.Throws<ArgumentException>(() =>
            ZapretManager.BuildCygwinLaunchBat(fakeBinDir, fakeListsDir, maliciousArgs));

        Assert.Contains("Zapret arguments contain disallowed shell metacharacters", ex.Message);
    }

    [Fact]
    public void ExtractWinwsArgsFromLines_SingleLine_StripsExeAndReturnsArgs()
    {
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
        Assert.DoesNotContain("winws.exe", args!);
        Assert.Contains("--wf-tcp=443", args);
        Assert.Contains("--dpi-desync=fake,split2", args);
        Assert.Contains("--dpi-desync-fooling=md5sig", args);
    }

    [Fact]
    public void RunTests_MissingScriptFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => ZapretActions.RunTests());
    }

    [Theory]
    [InlineData("zapret_dir_&_calc_")]
    [InlineData("zapret_dir_|_cmd_")]
    [InlineData("zapret_dir_^_echo_")]
    [InlineData("zapret_dir_%EVIL%_")]
    [InlineData("zapret_dir_\"_quote_")]
    public void RunTests_PathWithMetacharacters_ThrowsArgumentException(string folderPrefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"{folderPrefix}{Guid.NewGuid():N}");
        var utilsDir = Path.Combine(tempDir, "zapret", "utils");
        Directory.CreateDirectory(utilsDir);
        var testPath = Path.Combine(utilsDir, "test zapret.ps1");
        File.WriteAllText(testPath, "# test");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = Path.Combine(appData, "VPNRouter");

        AppPaths.OverrideDataDir(tempDir);
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => ZapretActions.RunTests());
            Assert.Contains("Test path contains disallowed shell metacharacters", ex.Message);
        }
        finally
        {
            AppPaths.OverrideDataDir(dataDir);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OpenServiceMenu_PathWithMetacharacters_ThrowsArgumentException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zapret_dir_&_calc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var servicePath = Path.Combine(tempDir, "service.bat");
        File.WriteAllText(servicePath, "@echo off");

        try
        {
            var ex = Assert.Throws<ArgumentException>(() => ZapretActions.OpenServiceMenu(servicePath));
            Assert.Contains("Service path contains disallowed shell metacharacters", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExtractWinwsArgsFromLines_LineContinuation_JoinsAndSubstitutesPlaceholders()
    {
        var lines = new[]
        {
            "@echo off",
            "start \"\" \"%BIN%winws.exe\" --wf-tcp=443 ^",
            "--dpi-desync=fake,split2 ^",
            "--dpi-desync-fake-tls=\"%LISTS%tls_clienthello_www_google_com.bin\""
        };

        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            lines,
            binPath: "%BIN%",
            listsPath: "/RESOLVED/lists/");

        Assert.NotNull(args);
        Assert.Contains("--wf-tcp=443", args!);
        Assert.Contains("--dpi-desync=fake,split2", args);
        Assert.Contains("/RESOLVED/lists/tls_clienthello_www_google_com.bin", args);
        Assert.DoesNotContain("%LISTS%", args);
    }
}
