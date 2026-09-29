#nullable enable
using System.Text;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class IFileSystemContractTests
{
    private static InMemoryFileSystem NewFs() => new();

    [Fact]
    public async Task ReadWriteText_RoundTrip()
    {
        var fs = NewFs();
        const string path = @"C:\test\file.txt";
        const string content = "hello, мир\n2nd line";
        var ct = TestContext.Current.CancellationToken;

        await fs.WriteAllTextAsync(path, content, ct);
        var roundTripped = await fs.ReadAllTextAsync(path, ct);

        Assert.Equal(content, roundTripped);
        Assert.True(fs.FileExists(path));
        var info = fs.GetFileInfo(path);
        Assert.NotNull(info);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), info!.Length);
    }

    [Fact]
    public async Task TryAcquireExclusiveLock_HappyPath()
    {
        var fs = NewFs();
        const string path = @"C:\locks\app.lock";
        var ct = TestContext.Current.CancellationToken;

        await using var _ = await ToDisposableAsync(
            fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(100), ct));

        var second = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(50), ct);
        Assert.Null(second);
    }

    [Fact]
    public async Task TryAcquireExclusiveLock_AlreadyHeld_ReturnsNullAfterTimeout()
    {
        var fs = NewFs();
        const string path = @"C:\locks\busy.lock";
        var ct = TestContext.Current.CancellationToken;

        var first = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(100), ct);
        Assert.NotNull(first);

        var start = DateTime.UtcNow;
        var second = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(80), ct);
        var elapsed = DateTime.UtcNow - start;

        Assert.Null(second);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(60),
            $"Expected ≥60ms wait, got {elapsed.TotalMilliseconds:F0}ms");

        first!.Dispose();
        var third = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(100), ct);
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public async Task InMemoryFileSystem_ParallelWrites_AreThreadSafe()
    {
        var fs = NewFs();
        const int taskCount = 32;
        const int perTask = 100;
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, taskCount).Select(t =>
            Task.Run(async () =>
            {
                for (int i = 0; i < perTask; i++)
                {
                    var path = $@"C:\par\t{t}\f{i}.txt";
                    await fs.WriteAllTextAsync(path, $"{t}:{i}", ct);
                }
            }, ct)).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(taskCount * perTask, fs.FileCount);
        for (int t = 0; t < taskCount; t += 8)
        {
            for (int i = 0; i < perTask; i += 25)
            {
                var path = $@"C:\par\t{t}\f{i}.txt";
                Assert.Equal($"{t}:{i}", await fs.ReadAllTextAsync(path, ct));
            }
        }
    }

    private static async Task<IAsyncDisposable> ToDisposableAsync(Task<IDisposable?> task)
    {
        var handle = await task;
        Assert.NotNull(handle);
        return new AsyncDisposableWrapper(handle!);
    }

    private sealed class AsyncDisposableWrapper : IAsyncDisposable
    {
        private readonly IDisposable _inner;
        public AsyncDisposableWrapper(IDisposable inner) => _inner = inner;
        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
