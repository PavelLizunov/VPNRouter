using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretUpdaterAtomicityTests : IDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly Func<string>? _originalTestRoot = ZapretRuntimeDirectory.TestRoot;
    private string Live => Path.Combine(_root, "zapret");

    public ZapretUpdaterAtomicityTests() => ZapretRuntimeDirectory.TestRoot = () => _root;

    public void Dispose()
    {
        ZapretRuntimeDirectory.TestRoot = _originalTestRoot;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FirstOfflineSettingsReadRestoresTheInterruptedPackage()
    {
        var previous = Live + ".previous";
        Directory.CreateDirectory(Path.Combine(previous, "utils"));
        File.WriteAllText(Path.Combine(previous, "utils", "game_filter.enabled"), "udp");
        File.WriteAllText(Path.Combine(previous, "version.txt"), "previous");

        Assert.Equal(ZapretActions.GameFilterMode.UdpOnly, ZapretActions.GetGameFilterMode());
        Assert.Equal("previous", File.ReadAllText(Path.Combine(Live, "version.txt")));
        Assert.False(Directory.Exists(previous));
    }

    [Fact]
    public Task VersionReadWaitsForPublication() =>
        PublishWhileContended(() => Assert.Equal("new", ZapretUpdater.GetLocalVersion()));

    [Fact]
    public async Task GameFilterWriteWaitsForPublication()
    {
        await PublishWhileContended(() => ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.TcpOnly));
        Assert.Equal("tcp", File.ReadAllText(Path.Combine(Live, "utils", "game_filter.enabled")));
    }

    [Fact]
    public async Task InstallerPublishesSeededListsBeforeApplyingAConcurrentSetting()
    {
        var userList = Path.Combine("lists", "list-general-user.txt");
        Directory.CreateDirectory(Path.Combine(Live, "lists"));
        File.WriteAllText(Path.Combine(Live, userList), "saved.example");
        var staged = Directory.CreateDirectory(Path.Combine(_root, "staged")).FullName;
        File.WriteAllText(Path.Combine(staged, "version.txt"), "new");

        var ct = TestContext.Current.CancellationToken;
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var contended = new ManualResetEventSlim();
        var originalObserver = ZapretRuntimeDirectory.TestLockContended;
        ZapretRuntimeDirectory.TestLockContended = contended.Set;
        try
        {
            var write = Task.Run(async () =>
            {
                await detached.Task.WaitAsync(WaitTimeout, ct);
                ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.UdpOnly);
            }, ct);
            var publish = Task.Run(() =>
            {
                using var install = ZapretRuntimeDirectory.LockInstallation(_root);
                ZapretUpdater.PublishVerifiedPackage(staged, () =>
                {
                    detached.SetResult();
                    Assert.True(contended.Wait(WaitTimeout, ct));
                    Assert.False(Directory.Exists(Live));
                    Assert.Equal("saved.example", File.ReadAllText(Path.Combine(staged, userList)));
                });
            }, ct);
            await Task.WhenAll(publish, write).WaitAsync(WaitTimeout + WaitTimeout, ct);
        }
        finally { ZapretRuntimeDirectory.TestLockContended = originalObserver; }

        Assert.Equal("new", File.ReadAllText(Path.Combine(Live, "version.txt")));
        Assert.Equal("saved.example", File.ReadAllText(Path.Combine(Live, userList)));
        Assert.Equal("udp", File.ReadAllText(Path.Combine(Live, "utils", "game_filter.enabled")));
    }

    [Fact]
    public async Task IpSetWriteWaitsForPublication()
    {
        await PublishWhileContended(() => ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None));
        var list = Path.Combine(Live, "lists", "ipset-all.txt");
        Assert.Equal("203.0.113.113/32", File.ReadAllText(list));
        Assert.Equal("192.0.2.0/24", File.ReadAllText(list + ".backup"));
    }

    [Fact]
    public async Task UpdateCheckWriteWaitsForPublication()
    {
        await PublishWhileContended(() => ZapretActions.SetAutoUpdateCheck(true));
        Assert.Equal("ENABLED", File.ReadAllText(Path.Combine(Live, "utils", "check_updates.enabled")));
    }

    [Fact]
    public async Task DownloadedIpSetListWaitsForPublication()
    {
        const string downloaded = "198.51.100.0/24";
        var originalHttp = ZapretActions.Http;
        ZapretActions.Http = new FakeHttpClient().Setup("ipset-service.txt", downloaded);
        try
        {
            await PublishWhileContended(async () =>
            {
                await foreach (var _ in ZapretActions.UpdateIpSetListAsync(TestContext.Current.CancellationToken)) { }
            });
            Assert.Equal(downloaded, File.ReadAllText(Path.Combine(Live, "lists", "ipset-all.txt")));
        }
        finally { ZapretActions.Http = originalHttp; }
    }

    private Task PublishWhileContended(Action accessPackage) => PublishWhileContended(() =>
    {
        accessPackage();
        return Task.CompletedTask;
    });

    private async Task PublishWhileContended(Func<Task> accessPackage)
    {
        Directory.CreateDirectory(Live);
        File.WriteAllText(Path.Combine(Live, "version.txt"), "old");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(staged, "lists"));
        File.WriteAllText(Path.Combine(staged, "version.txt"), "new");
        File.WriteAllText(Path.Combine(staged, "lists", "ipset-all.txt"), "192.0.2.0/24");

        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalObserver = ZapretRuntimeDirectory.TestLockContended;
        ZapretRuntimeDirectory.TestLockContended = () => contended.TrySetResult();
        var access = Task.CompletedTask;
        try
        {
            using var publisher = File.Open(Path.Combine(_root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Directory.Move(Live, Live + ".previous");
            access = Task.Run(accessPackage, TestContext.Current.CancellationToken);
            var first = await Task.WhenAny(contended.Task, access).WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
            Assert.Same(contended.Task, first);
            Assert.False(Directory.Exists(Live));
            Directory.Move(staged, Live);
            Directory.Delete(Live + ".previous", recursive: true);
        }
        finally
        {
            ZapretRuntimeDirectory.TestLockContended = originalObserver;
            await access.WaitAsync(WaitTimeout);
        }
    }

    [Fact]
    public void FailedReplacementRestoresTheCompletePreviousPackage()
    {
        Directory.CreateDirectory(Live);
        var previousFile = Path.Combine(Live, "version.txt");
        File.WriteAllText(previousFile, "previous");

        var staged = Directory.CreateDirectory(Path.Combine(_root, "staged")).FullName;
        var failure = new IOException("fixture publish failure");
        var error = Assert.Throws<IOException>(() => ZapretUpdater.PublishVerifiedPackage(staged, () =>
        {
            Assert.False(Directory.Exists(Live));
            throw failure;
        }));
        Assert.Same(failure, error);
        Assert.Equal("previous", File.ReadAllText(previousFile));
        Assert.False(Directory.Exists(Live + ".previous"));
    }
}
