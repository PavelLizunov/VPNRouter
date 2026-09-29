#nullable enable

using System.Diagnostics;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class LockFileTests
{
    private static string NewLockPath() =>
        @"C:\VPNRouter\test\" + Guid.NewGuid().ToString("N") + ".lock";

    [Fact]
    public async Task AcquireInstance_HappyPath_WritesPidPayloadAndHoldsLock()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();

        Assert.True(fs.FileExists(path));
        var contents = fs.ReadAllText(path);
        var firstLine = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal(Environment.ProcessId.ToString(), firstLine);

        var second = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.Null(second);

        sut.ReleaseInstance();
    }

    [Fact]
    public void AcquireInstance_FromTwoInstances_SecondGetsNoLockButDoesNotThrow()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var first = new LockFile(fs, path);
        var second = new LockFile(fs, path);

        first.AcquireInstance();

        var ex = Record.Exception(() => second.AcquireInstance());
        Assert.Null(ex);

        first.ReleaseInstance();
    }

    [Fact]
    public void ReleaseInstance_AllowsFreshAcquireAfterwards()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var first = new LockFile(fs, path);

        first.AcquireInstance();
        first.ReleaseInstance();

        Assert.False(fs.FileExists(path));

        var second = new LockFile(fs, path);
        second.AcquireInstance();

        Assert.True(fs.FileExists(path),
            "Second instance should have re-created the lock file");

        second.ReleaseInstance();
    }

    [Fact]
    public void AcquireInstance_CalledTwiceOnSameInstance_IsNoOp()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();
        var firstAcquireSnapshot = fs.ReadAllText(path);

        sut.AcquireInstance();
        sut.AcquireInstance();

        var afterRepeats = fs.ReadAllText(path);
        Assert.Equal(firstAcquireSnapshot, afterRepeats);

        sut.ReleaseInstance();
    }

    [Fact]
    public void DetectPreviousCrashInstance_NoLockFile_ReturnsNull()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        var banner = sut.DetectPreviousCrashInstance();

        Assert.Null(banner);
        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_DeadPid_SurfacesCrashedRunBanner()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var deadPid = int.MaxValue;
        var payload = $"{deadPid}\n2026-05-17T12:00:00Z\nC:\\dead.exe\n";
        fs.Seed(path, payload);

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        Assert.NotNull(banner);
        Assert.Contains($"PID {deadPid}", banner!);
        Assert.Contains("did not shut down cleanly", banner);
        Assert.Contains("2026-05-17T12:00:00Z", banner);

        Assert.False(fs.FileExists(path),
            "DetectPreviousCrash must delete the stale lock file");
    }

    [Fact]
    public void DetectPreviousCrashInstance_LivePid_ReturnsNullWithoutBanner()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var livePid = Environment.ProcessId;
        var payload = $"{livePid}\n2026-05-17T12:00:00Z\nC:\\test.exe\n";
        fs.Seed(path, payload);

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        Assert.Null(banner);

        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_UnreadablePayload_SurfacesGenericBanner()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        fs.Seed(path, "this-is-not-a-pid\nrandom garbage\n");

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        Assert.NotNull(banner);
        Assert.Contains("unreadable", banner!, StringComparison.OrdinalIgnoreCase);
        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_ThenAcquire_SucceedsCleanly()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        fs.Seed(path, $"{int.MaxValue}\n2026-05-17\nC:\\dead.exe\n");

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();
        Assert.NotNull(banner);

        sut.AcquireInstance();
        Assert.True(fs.FileExists(path));
        var firstLine = fs.ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal(Environment.ProcessId.ToString(), firstLine);

        sut.ReleaseInstance();
    }

    [Fact]
    public void AcquireInstance_PidPayloadStructure_IsThreeLines()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();

        var lines = fs.ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2,
            $"Expected at least 2 lines (PID + timestamp), got {lines.Length}: {string.Join("|", lines)}");
        Assert.True(int.TryParse(lines[0].Trim(), out _),
            $"Line 0 should be a PID, got '{lines[0]}'");
        Assert.True(DateTime.TryParse(lines[1].Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _),
            $"Line 1 should be ISO timestamp, got '{lines[1]}'");

        sut.ReleaseInstance();
    }
}
