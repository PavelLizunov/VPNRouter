#nullable enable

using System.Diagnostics;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretActionsTests : IDisposable
{
    private readonly IProcessRunner _originalRunner = ZapretActions.ProcessRunner;
    private readonly Action<ProcessStartInfo> _originalLauncher = ZapretActions.ProcessLauncher;
    private readonly Func<string> _originalSystem = ZapretActions.GetSystemDirectory;
    private readonly Func<string> _originalPreview = ZapretActions.GetHostsPreviewDirectory;
    private readonly IHttpClient _originalHttp = ZapretActions.Http;
    private readonly string _root = Directory.CreateTempSubdirectory("vpnrouter-zapret-tools-").FullName;
    private readonly List<ProcessStartInfo> _launched = new();
    private readonly FakeHttpClient _http = new();
    private const string HostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";

    private string WindowsDir => Path.Combine(_root, "Windows R&D (test)");
    private string SystemDir => Path.Combine(WindowsDir, "System32");
    private string HostsPath => Path.Combine(SystemDir, "drivers", "etc", "hosts");
    private string PreviewDir => Path.Combine(_root, "Preview %value% O'Brien");

    public ZapretActionsTests()
    {
        ZapretActions.GetSystemDirectory = () => SystemDir;
        ZapretActions.GetHostsPreviewDirectory = () => PreviewDir;
        ZapretActions.ProcessLauncher = _launched.Add;
        ZapretActions.Http = _http;
    }

    public void Dispose()
    {
        ZapretActions.ProcessRunner = _originalRunner;
        ZapretActions.ProcessLauncher = _originalLauncher;
        ZapretActions.GetSystemDirectory = _originalSystem;
        ZapretActions.GetHostsPreviewDirectory = _originalPreview;
        ZapretActions.Http = _originalHttp;
        Directory.Delete(_root, recursive: true);
    }

    private static async Task<List<string>> ReadLinesAsync(IAsyncEnumerable<string> action)
    {
        var lines = new List<string>();
        await foreach (var line in action) lines.Add(line);
        return lines;
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
            r => r.ExecutablePath == Path.Combine(SystemDir, "sc.exe")
              && r.Arguments.Count == 2
              && r.Arguments[0] == "query"
              && r.Arguments[1] == "BFE",
            Ok(ScQueryRunning));
        ZapretActions.ProcessRunner = fake;

        var actual = ZapretActions.IsServiceRunning("BFE");

        Assert.True(actual);
        Assert.Single(fake.RunCalls);
        var req = fake.RunCalls[0];
        Assert.Equal(Path.Combine(SystemDir, "sc.exe"), req.ExecutablePath);
        Assert.Equal(new[] { "query", "BFE" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), req.Timeout);
        Assert.True(req.CaptureStdout, "must capture stdout to parse RUNNING token");
    }

    [Fact]
    public void IsServiceRunning_OutputContainsStoppedOnly_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == Path.Combine(SystemDir, "sc.exe"), Ok(ScQueryStopped));
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
        fake.OnRun(r => r.ExecutablePath == ZapretActions.ScExecutablePath, Ok(ScQueryStopped));
        ZapretActions.ProcessRunner = fake;

        Assert.True(ZapretActions.ServiceExists("zapret"));
    }

    [Fact]
    public void ServiceExists_EmptyOrErrorOutput_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == ZapretActions.ScExecutablePath,
            Fail(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.ServiceExists("nonexistent-svc"));
    }

    [Fact]
    public void IsAnyServiceMatching_OutputContainsSubstring_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == Path.Combine(SystemDir, "sc.exe")
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
        fake.OnRun(r => r.ExecutablePath == Path.Combine(SystemDir, "sc.exe"),
            Ok("SERVICE_NAME: DnsCache\r\nSERVICE_NAME: BFE\r\n"));
        ZapretActions.ProcessRunner = fake;

        Assert.False(ZapretActions.IsAnyServiceMatching("zapret"));
    }

    [Fact]
    public async Task RunSc_PassesParsedArgsAndTimeout()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == ZapretActions.ScExecutablePath, Ok(""));
        ZapretActions.ProcessRunner = fake;

        await ZapretActions.RunSc("stop zapret");

        Assert.Single(fake.RunCalls);
        var req = fake.RunCalls[0];
        Assert.Equal(ZapretActions.ScExecutablePath, req.ExecutablePath);
        Assert.Equal(new[] { "stop", "zapret" }, req.Arguments.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(5), req.Timeout);
    }

    [Theory]
    [InlineData(0, "RFC 1323 Timestamps                 : enabled\r\n", "✓ TCP timestamps enabled")]
    [InlineData(0, "Timestamps: disabled", "⚠ TCP timestamps disabled")]
    [InlineData(1, "Timestamps: enabled", "? Couldn't read TCP timestamps")]
    public async Task Diagnostics_UsesOnlyPinnedReadOnlyQueries(int exitCode, string output, string status)
    {
        var sc = Path.Combine(SystemDir, "sc.exe");
        var netsh = Path.Combine(SystemDir, "netsh.exe");
        var runner = new FakeProcessRunner()
            .OnRun(r => r.ExecutablePath == sc, Ok(ScQueryStopped))
            .OnRun(r => r.ExecutablePath == netsh, Ok(output) with { ExitCode = exitCode });
        ZapretActions.ProcessRunner = runner;

        var lines = await ReadLinesAsync(ZapretActions.RunDiagnosticsAsync(TestContext.Current.CancellationToken));

        Assert.Contains(lines, line => line.StartsWith(status, StringComparison.Ordinal));
        Assert.All(runner.RunCalls, r =>
        {
            Assert.True(Path.IsPathFullyQualified(r.ExecutablePath));
            Assert.Equal(SystemDir, r.WorkingDirectory);
            Assert.True(r.ClearInheritedEnvironment);
            Assert.Equal($"{SystemDir};{WindowsDir}", r.EnvironmentOverrides!["PATH"]);
        });
        var tcp = Assert.Single(runner.RunCalls, r => r.ExecutablePath == netsh);
        Assert.Equal(new[] { "interface", "tcp", "show", "global" }, tcp.Arguments.ToArray());
        string[][] queries = [
            ["query", "BFE"], ["query", "Killer"], ["query", "GoodbyeDPI"], ["query", "TracSrvWrapper"],
            ["query", "EPWD"], ["query", "state=", "all"], ["query", "WinDivert"]
        ];
        var calls = runner.RunCalls.Where(r => r.ExecutablePath == sc).ToArray();
        Assert.Equal(queries.Length + 1, runner.RunCalls.Count);
        Assert.Equal(queries.Length, calls.Length);
        for (var i = 0; i < queries.Length; i++) Assert.Equal(queries[i], calls[i].Arguments.ToArray());
        Assert.Empty(runner.StartCalls);
        Assert.Empty(_launched);
    }

    [Fact]
    public void WinwsArguments_KeepEachOptionAndResolveOnlyPackagePlaceholders()
    {
        var actual = ZapretManager.ExpandArguments("--wf-tcp=80,443 --hostlist=%LISTS%domains.txt", fromPackage: false);
        Assert.Equal(new[] { "--wf-tcp=80,443", "--hostlist=" + Path.Combine(ZapretUpdater.ListsDir, "domains.txt") }, actual);
    }

    [Theory]
    [InlineData("--unrecognized=1")]
    [InlineData("--hostlist=../outside.txt")]
    public void WinwsArguments_RejectUnsupportedCustomInputs(string text)
    {
        Assert.Throws<ArgumentException>(() => ZapretManager.ExpandArguments(text, fromPackage: false));
    }

    private static readonly ZapretServiceOwnership MenuPaths = new(
        Path.Combine(Path.GetTempPath(), "legacy", "bin"), Path.Combine(Path.GetTempPath(), "protected runtime", "bin"));

    private static string MenuImage(string option) => ZapretActions.ServiceImage(
        Path.Combine(MenuPaths.ProtectedBin, "winws.exe"), [option]);

    [Fact]
    public void ServiceMenu_CreatesZapretFromVerifiedImageWhenVacant()
    {
        var image = MenuImage("--wf-tcp=443");
        var created = new FakeRegistration(new(image, 0x10, 2));
        var names = new List<string>();
        var outcome = ZapretActions.ApplyServiceStrategy(MenuPaths, image,
            (name, writable) => { Assert.False(writable); names.Add(name); return null; },
            supplied => { Assert.Equal(image, supplied); return created; });

        Assert.Equal("zapret", names[0]);
        Assert.Equal(ZapretActions.ServiceApply.Created, outcome);
        Assert.True(created.IsActive);
    }

    [Theory]
    [InlineData("zapret")]
    [InlineData("WinDivert")]
    public void ServiceMenu_ForeignRegistrationPreventsChanges(string foreign)
    {
        var other = new FakeRegistration(new("foreign", foreign == "zapret" ? 0x10u : 1u, 2)) { IsActive = true };
        var outcome = ZapretActions.ApplyServiceStrategy(MenuPaths, MenuImage("--wf-tcp=443"),
            (name, writable) => { Assert.False(writable); return name == foreign ? other : null; },
            _ => throw new InvalidOperationException("Unexpected create"));
        Assert.Equal(ZapretActions.ServiceApply.Conflict, outcome);
        Assert.True(other.IsActive);
        Assert.Equal(0, other.Changes);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void ServiceMenu_PreservesExistingStartupModeAndActivity(int start, bool active)
    {
        var service = new FakeRegistration(new(MenuImage("--wf-tcp=80"), 0x10, (uint)start)) { IsActive = active };
        var image = MenuImage("--wf-tcp=443");
        var outcome = ZapretActions.ApplyServiceStrategy(MenuPaths, image,
            (name, _) => name == "zapret" ? service : null,
            _ => throw new InvalidOperationException("Unexpected create"));
        Assert.Equal(start == 4 ? ZapretActions.ServiceApply.Disabled :
            active ? ZapretActions.ServiceApply.Running : ZapretActions.ServiceApply.Stopped, outcome);
        Assert.Equal(new ServiceConfig(image, 0x10, (uint)start), service.Config);
        Assert.Equal(active, service.IsActive);
        Assert.Equal(1, service.Changes);
    }

    [Fact]
    public void ServiceMenu_PendingMigrationIsHandledByTheServiceOwner()
    {
        var service = new FakeRegistration(new(MenuImage("--wf-tcp=80"), 0x10, 4));
        service.Values["VPNRouterMigrationV1"] = Array.Empty<string>();
        var outcome = ZapretActions.ApplyServiceStrategy(MenuPaths, MenuImage("--wf-tcp=443"),
            (name, writable) => { Assert.False(writable); return name == "zapret" ? service : null; },
            _ => throw new InvalidOperationException("Unexpected create"));
        Assert.Equal(ZapretActions.ServiceApply.Migrate, outcome);
        Assert.Equal(0, service.Changes);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void ServiceMenu_QuotesTheSystemImageAndEachVerifiedOption()
    {
        var exe = Path.Combine(MenuPaths.ProtectedBin, "winws.exe");
        string[] args = ["--wf-tcp=443", "--hostlist=protected runtime/list.txt"];
        Assert.Equal($"\"{exe}\" \"--wf-tcp=443\" \"--hostlist=protected runtime/list.txt\"",
            ZapretActions.ServiceImage(exe, args));
        Assert.Throws<InvalidDataException>(() => ZapretActions.ServiceImage(exe, ["--hostlist=%LISTS%"]));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateHosts_OpensPreviewAndSelectsSystemHostsWithPinnedTools(bool editorUnavailable)
    {
        const string current = "# existing hosts";
        const string download = "# Flowseal\n127.0.0.1 example.test\n";
        Directory.CreateDirectory(Path.GetDirectoryName(HostsPath)!);
        File.WriteAllText(HostsPath, current);
        _http.Setup(HostsUrl, download);
        ZapretActions.ProcessLauncher = info =>
        {
            _launched.Add(info);
            if (editorUnavailable && _launched.Count == 1) throw new System.ComponentModel.Win32Exception(2);
        };

        var lines = await ReadLinesAsync(ZapretActions.UpdateHostsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HostsUrl, Assert.Single(_http.SentRequests).Uri.AbsoluteUri);
        Assert.Equal(2, _launched.Count);
        Assert.All(_launched, info =>
        {
            Assert.True(Path.IsPathFullyQualified(info.FileName));
            Assert.False(info.UseShellExecute);
            Assert.Equal(SystemDir, info.WorkingDirectory);
            Assert.Equal($"{SystemDir};{WindowsDir}", info.Environment["PATH"]);
        });
        var editor = _launched[0];
        Assert.Equal(Path.Combine(SystemDir, "notepad.exe"), editor.FileName);
        var preview = Assert.Single(editor.ArgumentList);
        Assert.Empty(editor.Arguments);
        Assert.Equal(PreviewDir, Path.GetDirectoryName(preview));
        Assert.Matches(@"^zapret_hosts_[0-9a-f]{32}\.txt$", Path.GetFileName(preview));
        Assert.Equal(download, File.ReadAllText(preview));
        Assert.Contains(lines, line => line.Contains(preview, StringComparison.Ordinal) &&
            line.StartsWith(editorUnavailable ? "✗" : "→", StringComparison.Ordinal));

        var explorer = _launched[1];
        Assert.Equal(Path.Combine(WindowsDir, "explorer.exe"), explorer.FileName);
        Assert.Equal($"/select,\"{HostsPath}\"", explorer.Arguments);
        Assert.Empty(explorer.ArgumentList);
        Assert.Equal(current, File.ReadAllText(HostsPath));
    }

    [Fact]
    public async Task UpdateHosts_CurrentEntriesSkipPreviewAndDesktopTools()
    {
        const string entries = "127.0.0.1 example.test\n";
        Directory.CreateDirectory(Path.GetDirectoryName(HostsPath)!);
        File.WriteAllText(HostsPath, entries);
        _http.Setup(HostsUrl, entries);

        var lines = await ReadLinesAsync(ZapretActions.UpdateHostsAsync(TestContext.Current.CancellationToken));

        Assert.Contains(lines, line => line.Contains("up to date", StringComparison.Ordinal));
        Assert.False(Directory.Exists(PreviewDir));
        Assert.Empty(_launched);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("invalid\"directory")]
    [InlineData("invalid\ndirectory")]
    public async Task UpdateHosts_RejectsInvalidSystemDirectoryBeforeLaunching(string directory)
    {
        _http.Setup(HostsUrl, "127.0.0.1 example.test\n");
        ZapretActions.GetSystemDirectory = () => directory == "relative" ? directory : Path.Combine(_root, directory);

        await Assert.ThrowsAsync<IOException>(() =>
            ReadLinesAsync(ZapretActions.UpdateHostsAsync(TestContext.Current.CancellationToken)));

        Assert.Empty(_launched);
        Assert.False(Directory.Exists(PreviewDir));
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
