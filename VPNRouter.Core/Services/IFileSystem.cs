#nullable enable
namespace VPNRouter.Core.Services;

public interface IFileSystem
{
    Task<string> ReadAllTextAsync(string path, CancellationToken ct = default);

    Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);

    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);

    Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default);

    bool FileExists(string path);

    void DeleteFile(string path);

    void MoveFile(string src, string dst, bool overwrite = false);

    void AppendAllLines(string path, IEnumerable<string> lines);

    string[] ReadAllLines(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string content);

    void WriteAllLines(string path, IEnumerable<string> lines);

    FileMetadata? GetFileInfo(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    void DeleteDirectory(string path, bool recursive = false);

    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false);

    Stream OpenRead(string path);

    Stream OpenWrite(string path);

    Task<IDisposable?> TryAcquireExclusiveLockAsync(string path, TimeSpan timeout, CancellationToken ct = default);
}

public sealed record FileMetadata(long Length, DateTimeOffset LastWriteTimeUtc, bool IsReadOnly);
