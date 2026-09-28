#nullable enable

using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class ThrowingFileSystem : IFileSystem
{
    private readonly InMemoryFileSystem _inner = new();

    public Exception? ThrowOnAppendAllLines { get; set; }

    public void Seed(string path, string content) => _inner.Seed(path, content);

    public void Seed(string path, byte[] content) => _inner.Seed(path, content);

    public System.Collections.Concurrent.ConcurrentQueue<string> AccessLog => _inner.AccessLog;

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

    public void AppendAllLines(string path, IEnumerable<string> lines)
    {
        if (ThrowOnAppendAllLines != null) throw ThrowOnAppendAllLines;
        _inner.AppendAllLines(path, lines);
    }
}
