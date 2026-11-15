using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyManagerTests
{
    [Fact]
    public void PolicyStartAndRestart_AcknowledgeFreshConfigBeforeStarted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var handles = new List<FakeProcessHandle>();
        var configPaths = new List<string>();
        fixture.Runner.OnStart(_ => true, request =>
        {
            var path = request.Arguments[^1];
            configPaths.Add(path);
            using var config = JsonDocument.Parse(File.ReadAllText(path));
            var log = config.RootElement.GetProperty("log").GetProperty("output").GetString()!;
            File.AppendAllText(log, "INFO sing-box started (0.01s)\n");
            var handle = new FakeProcessHandle(101); // PID reuse must not reuse the receipt.
            handles.Add(handle);
            return handle;
        });
        var accepted = new List<BusinessPolicyLaunch>();
        fixture.Manager.PolicyConfigurationAccepted += accepted.Add;
        fixture.Manager.Started += _ => Assert.Equal(handles.Count, accepted.Count);
        fixture.Manager.StartWithPolicyJson("{}", BusinessPolicyLaunchTests.Policy(), CancellationToken.None);
        var first = Assert.Single(accepted);
        Assert.True(fixture.Manager.IsPolicyLaunchCurrent(first));
        Assert.Equal(first.LogPath, SingBoxManager.DiagnosticLogPath);
        File.AppendAllText(first.LogPath, "ERROR token=synthetic-reader-not-live\n");
        SingBoxManager.PreservePolicyDiagnosticLog();
        Assert.DoesNotContain("synthetic-reader-not-live", File.ReadAllText(AppPaths.SingBoxLogPath));
        Assert.Contains("ERROR", File.ReadAllText(AppPaths.SingBoxLogPath));
        fixture.Manager.Restart();
        Assert.Equal(2, accepted.Count);
        Assert.NotEqual(configPaths[0], configPaths[1]);
        Assert.True(handles[0].HasExited);
        Assert.False(fixture.Manager.IsPolicyLaunchCurrent(first));
        Assert.True(fixture.Manager.IsPolicyLaunchCurrent(accepted[1]));
        Assert.NotEqual(first.LaunchId, accepted[1].LaunchId);
        Assert.False(File.Exists(configPaths[0]));
        fixture.Manager.Stop();
        Assert.False(fixture.Manager.IsPolicyLaunchCurrent(accepted[1]));
        Assert.False(File.Exists(configPaths[1]));
        Assert.Equal(AppPaths.SingBoxLogPath, SingBoxManager.DiagnosticLogPath);
        Assert.Contains("sing-box started", File.ReadAllText(AppPaths.SingBoxLogPath));
    }

    [Fact]
    public void PolicyReload_RejectsStaleHealthWrites_AndRestartsAcknowledgedSites()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        const string oldConfig = "{\"route\":{\"rules\":[{\"domain_suffix\":[\"old.example.com\"],\"outbound\":\"direct\"}]}}";
        const string currentConfig = "{\"route\":{\"rules\":[]}}";
        var loaded = new List<string>();
        fixture.Runner.OnStart(_ => true, request =>
        {
            var json = File.ReadAllText(request.Arguments[^1]);
            loaded.Add(json);
            using var config = JsonDocument.Parse(json);
            File.AppendAllText(config.RootElement.GetProperty("log").GetProperty("output").GetString()!,
                "INFO sing-box started (0.01s)\n");
            return new FakeProcessHandle(100 + loaded.Count);
        });
        var receipts = new List<BusinessPolicyLaunch>();
        fixture.Manager.PolicyConfigurationAccepted += receipts.Add;
        fixture.Manager.StartWithPolicyJson(oldConfig, BusinessPolicyLaunchTests.Policy(7), CancellationToken.None);
        Assert.True(fixture.Manager.ReloadPolicyJsonWithResult(currentConfig, BusinessPolicyLaunchTests.Policy(8),
            CancellationToken.None, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => fixture.Manager.WriteConfigToDisk(oldConfig));
        Assert.False(fixture.Manager.ReloadConfigJsonWithResult(oldConfig, forceRestart: true));
        Assert.False(fixture.Manager.TryReloadConfigJson(oldConfig));
        Assert.Throws<InvalidOperationException>(() => fixture.Manager.StartWithJson(oldConfig));
        Assert.False(fixture.Manager.ReloadPolicyJsonWithResult(oldConfig, BusinessPolicyLaunchTests.Policy(7),
            CancellationToken.None, CancellationToken.None));
        fixture.Manager.Restart();
        Assert.Equal(3, loaded.Count);
        Assert.DoesNotContain("old.example.com", loaded[1]);
        Assert.DoesNotContain("old.example.com", loaded[2]);
        Assert.Equal(8, receipts[^1].Policy.Revision);
        Assert.True(fixture.Manager.IsPolicyLaunchCurrent(receipts[^1]));
    }

    [Fact]
    public void PolicyStart_FailureBeforeRunnerEntered_ReleasesLeaseAndFiles()
    {
        using var fixture = new Fixture();
        Assert.ThrowsAny<JsonException>(() => fixture.Manager.StartWithPolicyJson(
            "not-json", BusinessPolicyLaunchTests.Policy(), CancellationToken.None));
        Assert.Empty(fixture.Runner.StartCalls);
        Assert.Equal(SingBoxState.Stopped, fixture.Manager.State);
        Assert.Empty(Directory.EnumerateDirectories(AppPaths.ConfigDir, "policy-run-*"));
        using var next = new SingBoxManager(new SingBoxSettings { ExecutablePath = fixture.Executable },
            http: new FakeHttpClient(), runner: fixture.Runner);
        // A leaked TUN lease would throw TunOwnershipException instead.
        Assert.ThrowsAny<JsonException>(() => next.StartWithPolicyJson(
            "not-json", BusinessPolicyLaunchTests.Policy(), CancellationToken.None));
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public void PolicyStart_UnconfirmedExactStop_RetainsLeaseAndDiagnosticFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        var handle = new StubbornHandle();
        using var fixture = new Fixture(new StubbornRunner(handle, cancellation.Cancel));
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => fixture.Manager.StartWithPolicyJson(
                "{}", BusinessPolicyLaunchTests.Policy(), cancellation.Token));
            Assert.Equal(SingBoxState.Failed, fixture.Manager.State);
            Assert.True(File.Exists(SingBoxManager.DiagnosticLogPath));
            Assert.Contains("policy-run-", SingBoxManager.DiagnosticLogPath);
            using var next = new SingBoxManager(new SingBoxSettings { ExecutablePath = fixture.Executable },
                http: new FakeHttpClient(), runner: fixture.Runner);
            Assert.Throws<TunOwnershipException>(() => next.StartWithJson("{}"));
        }
        finally
        {
            handle.ExitedNow = true;
            fixture.Manager.Stop();
        }
        Assert.Empty(Directory.EnumerateDirectories(AppPaths.ConfigDir, "policy-run-*"));
    }

    [Fact]
    public void PolicyStart_CancelledAfterSpawn_StopsExactHandleBeforeReleasingLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var handle = new FakeProcessHandle(101);
        fixture.Runner.OnStart(_ => true, _ =>
        {
            cancellation.Cancel();
            return handle;
        });
        var receipts = 0;
        var starts = 0;
        fixture.Manager.PolicyConfigurationAccepted += _ => receipts++;
        fixture.Manager.Started += _ => starts++;
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Manager.StartWithPolicyJson(
            "{}", BusinessPolicyLaunchTests.Policy(), cancellation.Token));
        Assert.Equal(0, receipts);
        Assert.Equal(0, starts);
        Assert.True(handle.HasExited);
        Assert.True(handle.SuppressExitedEventCallCount > 0);
        Assert.Equal(SingBoxState.Stopped, fixture.Manager.State);
        Assert.Empty(Directory.EnumerateDirectories(AppPaths.ConfigDir, "policy-run-*"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _priorDirectory = AppPaths.DataDir;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "policy-manager-test-" + Guid.NewGuid().ToString("N"));
        private readonly IProcessRunner _priorDiagnostics = TunAdapterDiagnostics.Runner;
        internal FakeProcessRunner Runner { get; } = new();
        internal SingBoxManager Manager { get; }
        internal string Executable { get; }

        internal Fixture(IProcessRunner? actualRunner = null)
        {
            TunAdapterDiagnostics.Runner = new FakeProcessRunner().OnRun(_ => true,
                new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, false));
            AppPaths.OverrideDataDir(_directory);
            AppPaths.EnsureDirectories();
            Executable = OperatingSystem.IsWindows() ? Path.Combine(_directory, "synthetic-sing-box.exe") : AppPaths.SingBoxExePath;
            Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
            File.WriteAllText(Executable, "synthetic fixture, never executed");
            Manager = new SingBoxManager(new SingBoxSettings { ExecutablePath = Executable },
                http: new FakeHttpClient(), runner: actualRunner ?? Runner);
        }

        public void Dispose()
        {
            Manager.Dispose();
            TunAdapterDiagnostics.Runner = _priorDiagnostics;
            AppPaths.OverrideDataDir(_priorDirectory);
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class StubbornRunner(StubbornHandle handle, Action started) : IProcessRunner
    {
        public IProcessHandle Start(ProcessRequest request) { started(); return handle; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Unexpected fixture execution.");
    }

    private sealed class StubbornHandle : IProcessHandle
    {
        public int Pid => 101;
        internal bool ExitedNow { get; set; }
        public bool HasExited => ExitedNow;
        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public Task<int> WaitForExitAsync(CancellationToken ct) => ExitedNow ? Task.FromResult(0) :
            Task.FromException<int>(new OperationCanceledException(ct));
        public void Kill(bool entireProcessTree = true) { }
        public void SuppressExitedEvent() { }
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() { }
    }
}
