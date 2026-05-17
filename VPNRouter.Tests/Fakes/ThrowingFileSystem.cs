#nullable enable
// ============================================================================
// ThrowingFileSystem.cs — failure-injection wrapper over InMemoryFileSystem
// ============================================================================
//
// Tests that exercise the catch-blocks of services need a way to make a
// specific filesystem operation throw a specific exception. InMemoryFileSystem
// is faithful to the happy path but not pluggable in this way. This wrapper
// proxies every member to a private InMemoryFileSystem while letting tests
// configure "throw X on member Y" overrides.
//
// Currently exposes one injection point — AppendAllLines — needed by the
// HostsManager failure-mode tests (Phase 2G sub-wave 7a-1). Add more
// `ThrowOn*` properties as future tests need them; keep this thin to avoid
// it growing into a hand-rolled mocking framework.
// ============================================================================

using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// Thin wrapper over <see cref="InMemoryFileSystem"/> that can be configured
/// to throw a caller-supplied exception on selected members. Every other
/// member proxies to the inner fake. Used by failure-mode tests where the
/// service under test must surface I/O errors gracefully.
/// </summary>
public sealed class ThrowingFileSystem : IFileSystem
{
    private readonly InMemoryFileSystem _inner = new();

    /// <summary>If non-null, throw this on the next AppendAllLines call.</summary>
    public Exception? ThrowOnAppendAllLines { get; set; }

    /// <summary>Forward to inner's test-only seed helper.</summary>
    public void Seed(string path, string content) => _inner.Seed(path, content);

    /// <summary>Forward to inner's test-only seed helper (raw bytes).</summary>
    public void Seed(string path, byte[] content) => _inner.Seed(path, content);

    /// <summary>Inspect the inner fake's access log if asserted on.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> AccessLog => _inner.AccessLog;

    // ── Pass-through members ──

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default) => _inner.ReadAllTextAsync(path, ct);
    public Task WriteAllTextAsync(string path, string content, CancellationToken ct = default) => _inner.WriteAllTextAsync(path, content, ct);
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default) => _inner.ReadAllBytesAsync(path, ct);
    public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default) => _inner.WriteAllBytesAsync(path, content, ct);
    public bool FileExists(string path) => _inner.FileExists(path);
    public void DeleteFile(string path) => _inner.DeleteFile(path);
    public void MoveFile(string src, string dst, bool overwrite = false) => _inner.MoveFile(src, dst, overwrite);
    public string[] ReadAllLines(string path) => _inner.ReadAllLines(path);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public void WriteAllText(string path, string content) => _inner.WriteAllText(path, content);
    public void WriteAllLines(string path, IEnumerable<string> lines) => _inner.WriteAllLines(path, lines);
    public FileMetadata? GetFileInfo(string path) => _inner.GetFileInfo(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive = false) => _inner.DeleteDirectory(path, recursive);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false) => _inner.EnumerateFiles(directory, pattern, recursive);
    public Stream OpenRead(string path) => _inner.OpenRead(path);
    public Stream OpenWrite(string path) => _inner.OpenWrite(path);
    public Task<IDisposable?> TryAcquireExclusiveLockAsync(string path, TimeSpan timeout, CancellationToken ct = default) => _inner.TryAcquireExclusiveLockAsync(path, timeout, ct);

    // ── Failure-injection members ──

    /// <summary>
    /// If <see cref="ThrowOnAppendAllLines"/> is configured, throw that
    /// exception instead of delegating to the inner fake. Otherwise proxy
    /// straight through.
    /// </summary>
    public void AppendAllLines(string path, IEnumerable<string> lines)
    {
        if (ThrowOnAppendAllLines != null) throw ThrowOnAppendAllLines;
        _inner.AppendAllLines(path, lines);
    }
}
