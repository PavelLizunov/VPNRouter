using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "policy-launch-test-" + Guid.NewGuid().ToString("N"));

    public BusinessPolicyLaunchTests() => Directory.CreateDirectory(_directory);

    internal static BusinessPolicySnapshot Policy(long revision = 7) => new(
        "test-policy", "fixture-key", revision, 1794657600, true,
        [new BusinessPolicyRule("example.com", "direct", "")], new string('A', 64));

    [Fact]
    public async Task Launch_EarlyCoreMarker_AcknowledgesExactPrivateConfig()
    {
        const string source = "{\"route\":{\"final\":\"proxy\"},\"log\":{\"disabled\":true}}";
        using var launch = new BusinessPolicyLaunch(source, Policy(), _directory);
        using var handle = new FakeProcessHandle(100);
        launch.Bind(handle);
        // sing-box can write this before ProcessRunner.Start returns its handle.
        File.AppendAllText(launch.LogPath, "INFO sing-box started (0.12s)\n");
        await launch.WaitForAcknowledgementAsync(() => launch.IsOwnedLive(handle), TestContext.Current.CancellationToken);
        Assert.True(launch.Acknowledged);
        var bytes = File.ReadAllBytes(launch.ConfigPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), launch.ConfigurationDigest);
        using var effective = JsonDocument.Parse(bytes);
        Assert.Equal("proxy", effective.RootElement.GetProperty("route").GetProperty("final").GetString());
        var log = effective.RootElement.GetProperty("log");
        Assert.False(log.GetProperty("disabled").GetBoolean());
        Assert.False(log.GetProperty("timestamp").GetBoolean());
        Assert.Equal("info", log.GetProperty("level").GetString());
        Assert.Equal(launch.LogPath, log.GetProperty("output").GetString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(launch.ConfigPath)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(launch.ConfigPath));
        }
    }

    [Fact]
    public async Task Launch_OldLogAndSamePid_DoNotAcknowledgeReplacement()
    {
        using var prior = new BusinessPolicyLaunch("{}", Policy(), _directory);
        using var next = new BusinessPolicyLaunch("{}", Policy(8), _directory);
        using var priorHandle = new FakeProcessHandle(100);
        using var nextHandle = new FakeProcessHandle(100);
        prior.Bind(priorHandle);
        next.Bind(nextHandle);
        File.AppendAllText(prior.LogPath, "INFO sing-box started (0.12s)\n");
        await prior.WaitForAcknowledgementAsync(() => prior.IsOwnedLive(priorHandle), TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var pending = next.WaitForAcknowledgementAsync(() => next.IsOwnedLive(nextHandle), cancel.Token);
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(next.Acknowledged);
        Assert.NotEqual(prior.LaunchId, next.LaunchId);
        Assert.NotEqual(prior.ConfigurationDigest, next.ConfigurationDigest);
        Assert.False(next.IsOwnedLive(priorHandle));
    }

    [Theory]
    [InlineData("INFO sing-box started (0.12s)", true)]
    [InlineData("INFO sing-box started (0s)", true)]
    [InlineData("ERROR sing-box started (0.12s)", false)]
    [InlineData("INFO outbound/proxy: sing-box started (0.12s)", false)]
    [InlineData("INFO sing-box started (NaNs)", false)]
    [InlineData("INFO sing-box started (-1s)", false)]
    [InlineData("INFO sing-box started (1e6s)", false)]
    [InlineData("INFO sing-box started (0.12s) extra", false)]
    public void Marker_RequiresExactCoreSuccessfulStart(string line, bool expected) =>
        Assert.Equal(expected, BusinessPolicyLaunch.IsAcknowledgement(line));

    [Fact]
    public async Task Launch_ExitOrOwnershipChange_RejectsEvenWithMarker()
    {
        using var launch = new BusinessPolicyLaunch("{}", Policy(), _directory);
        using var handle = new FakeProcessHandle(100);
        launch.Bind(handle);
        File.AppendAllText(launch.LogPath, "INFO sing-box started (0.12s)\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => launch.WaitForAcknowledgementAsync(
            () => false, TestContext.Current.CancellationToken));
        handle.SignalExit(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => launch.WaitForAcknowledgementAsync(
            () => true, TestContext.Current.CancellationToken));
        Assert.False(launch.Acknowledged);
    }

    [Fact]
    public async Task Launch_NoMarker_DeadlineDoesNotReportAcceptance()
    {
        using var launch = new BusinessPolicyLaunch("{}", Policy(), _directory);
        using var handle = new FakeProcessHandle(100);
        launch.Bind(handle);
        await Assert.ThrowsAsync<InvalidDataException>(() => launch.WaitForAcknowledgementAsync(
            () => true, TestContext.Current.CancellationToken, timeout: TimeSpan.Zero));
        Assert.False(launch.Acknowledged);
    }

    [Fact]
    public async Task Launch_LogWithoutCompleteMarkerOrOverLimit_FailsClosed()
    {
        using var launch = new BusinessPolicyLaunch("{}", Policy(), _directory);
        using var handle = new FakeProcessHandle(100);
        launch.Bind(handle);
        File.AppendAllText(launch.LogPath, "INFO sing-box started (0.12s)");
        using var cancel = new CancellationTokenSource();
        var pending = launch.WaitForAcknowledgementAsync(() => true, cancel.Token);
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        File.AppendAllText(launch.LogPath, new string('x', 1024 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => launch.WaitForAcknowledgementAsync(
            () => true, TestContext.Current.CancellationToken));
        Assert.False(launch.Acknowledged);
    }

    [Fact]
    public void DiagnosticTail_IsBoundedAndRedacted_WithoutChangingAcknowledgementLog()
    {
        using var launch = new BusinessPolicyLaunch("{}", Policy(), _directory);
        const string secret = "synthetic-private-reader-not-live";
        File.WriteAllText(launch.LogPath, new string('x', 70 * 1024) + "\nINFO token=" + secret +
            "\nERROR synthetic network failure\n");
        var tail = launch.ReadRedactedDiagnosticTail();
        Assert.DoesNotContain(secret, tail);
        Assert.Contains("synthetic network failure", tail);
        Assert.True(tail.Length <= 64 * 1024);
        Assert.Contains(secret, File.ReadAllText(launch.LogPath));
        Assert.False(launch.Acknowledged);
    }

    [Fact]
    public void Launch_DisposeRemovesOnlyItsOwnedFiles()
    {
        var retained = Path.Combine(_directory, "unrelated.json");
        File.WriteAllText(retained, "{}");
        var launch = new BusinessPolicyLaunch("{}", Policy(), _directory);
        var config = launch.ConfigPath;
        var log = launch.LogPath;
        launch.Dispose();
        launch.Dispose();
        Assert.False(File.Exists(config));
        Assert.False(File.Exists(log));
        Assert.True(File.Exists(retained));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
