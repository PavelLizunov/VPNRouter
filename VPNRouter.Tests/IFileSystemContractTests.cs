#nullable enable
using System.Text;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Contract tests for <see cref="IFileSystem"/>. The 8 cases enumerated in
/// <c>plans/phase2-2D-ifilesystem-2026-05-17.md</c> exercise the
/// <see cref="InMemoryFileSystem"/> fake against the same expected
/// behaviour the real impl must honour. A trailing parallel-write test
/// covers the thread-safety claim in the verification gate.
///
/// <para>
/// These are NOT cross-tested against <see cref="RealFileSystem"/> here —
/// that runs the risk of polluting %ProgramData% during xUnit runs. A
/// single happy-path round-trip against <see cref="RealFileSystem"/>
/// is included in <see cref="RealFileSystem_BasicRoundTrip"/> using
/// <see cref="Path.GetTempPath"/> and a guid-named sub-directory.
/// </para>
/// </summary>
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
    public async Task ReadWriteBytes_RoundTrip()
    {
        var fs = NewFs();
        const string path = @"C:\bin\blob.dat";
        var bytes = new byte[] { 0x00, 0xFF, 0x42, 0x10, 0xAA };
        var ct = TestContext.Current.CancellationToken;

        await fs.WriteAllBytesAsync(path, bytes, ct);
        var roundTripped = await fs.ReadAllBytesAsync(path, ct);

        Assert.Equal(bytes, roundTripped);
        // Defensive-copy on read: mutating result must not affect store.
        roundTripped[0] = 0x77;
        var second = await fs.ReadAllBytesAsync(path, ct);
        Assert.Equal(0x00, second[0]);
    }

    [Fact]
    public void EnumerateFiles_NonRecursive_ReturnsTopLevel()
    {
        var fs = NewFs();
        fs.Seed(@"C:\dir\a.txt", "a");
        fs.Seed(@"C:\dir\b.txt", "b");
        fs.Seed(@"C:\dir\sub\c.txt", "c");

        var top = fs.EnumerateFiles(@"C:\dir", "*.txt", recursive: false)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "a.txt", "b.txt" }, top);
    }

    [Fact]
    public void EnumerateFiles_Recursive_FindsNested()
    {
        var fs = NewFs();
        fs.Seed(@"C:\dir\a.txt", "a");
        fs.Seed(@"C:\dir\sub\b.txt", "b");
        fs.Seed(@"C:\dir\sub\nested\c.txt", "c");
        // Non-matching pattern: should not be returned.
        fs.Seed(@"C:\dir\sub\other.md", "other");

        var all = fs.EnumerateFiles(@"C:\dir", "*.txt", recursive: true)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, all);
    }

    [Fact]
    public void DeleteFile_Missing_NoThrow()
    {
        var fs = NewFs();
        // No file at this path. The contract is "no-throw if missing" —
        // unlike System.IO.File.Delete which actually doesn't throw
        // either, but we exercise the abstraction explicitly.
        var ex = Record.Exception(() => fs.DeleteFile(@"C:\does\not\exist.txt"));
        Assert.Null(ex);
    }

    [Fact]
    public void CreateDirectory_Idempotent()
    {
        var fs = NewFs();
        const string path = @"C:\new\nested\dir";

        fs.CreateDirectory(path);
        fs.CreateDirectory(path); // second call must not throw
        fs.CreateDirectory(path); // third call must not throw

        Assert.True(fs.DirectoryExists(path));
        // Parent directories must also be marked-existing (mkdir -p).
        Assert.True(fs.DirectoryExists(@"C:\new\nested"));
        Assert.True(fs.DirectoryExists(@"C:\new"));
    }

    [Fact]
    public async Task TryAcquireExclusiveLock_HappyPath()
    {
        var fs = NewFs();
        const string path = @"C:\locks\app.lock";
        var ct = TestContext.Current.CancellationToken;

        await using var _ = await ToDisposableAsync(
            fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(100), ct));

        // Second concurrent acquire (without releasing first) returns null.
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
        // Must have waited at least the timeout (with small slop for
        // scheduler jitter — 60ms is conservative).
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(60),
            $"Expected ≥60ms wait, got {elapsed.TotalMilliseconds:F0}ms");

        // Releasing the first lock allows a subsequent acquire to succeed.
        first!.Dispose();
        var third = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(100), ct);
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public async Task InMemoryFileSystem_ParallelWrites_AreThreadSafe()
    {
        // Verifies the thread-safety claim called out in the brief's
        // verification gate. 32 tasks each write 100 unique paths;
        // we must end with exactly 32 * 100 distinct files and no
        // exceptions / lost writes.
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
        // Spot-check a handful by reading them back.
        for (int t = 0; t < taskCount; t += 8)
        {
            for (int i = 0; i < perTask; i += 25)
            {
                var path = $@"C:\par\t{t}\f{i}.txt";
                Assert.Equal($"{t}:{i}", await fs.ReadAllTextAsync(path, ct));
            }
        }
    }

    [Fact]
    public void RealFileSystem_BasicRoundTrip()
    {
        // Smoke test that the real impl wires up. Uses an isolated temp
        // directory and cleans up after itself so xUnit doesn't pollute
        // shared %ProgramData%.
        var fs = new RealFileSystem();
        var tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.IFileSystem.Tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(tempDir, "round.txt");

        try
        {
            fs.CreateDirectory(tempDir);
            Assert.True(fs.DirectoryExists(tempDir));

            fs.WriteAllText(path, "hi");
            Assert.True(fs.FileExists(path));
            Assert.Equal("hi", fs.ReadAllText(path));

            var info = fs.GetFileInfo(path);
            Assert.NotNull(info);
            Assert.Equal(2, info!.Length);

            fs.DeleteFile(path);
            Assert.False(fs.FileExists(path));
        }
        finally
        {
            try { fs.DeleteDirectory(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Wraps the result of <see cref="IFileSystem.TryAcquireExclusiveLockAsync"/>
    /// (which may be null on timeout) in an <see cref="IAsyncDisposable"/>
    /// so the test can use <c>await using</c> idiomatically.
    /// </summary>
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
