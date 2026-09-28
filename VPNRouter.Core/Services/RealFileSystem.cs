#nullable enable
namespace VPNRouter.Core.Services;

public sealed class RealFileSystem : IFileSystem
{
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(100);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
        => File.ReadAllTextAsync(path, ct);

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
        => File.WriteAllTextAsync(path, content, ct);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
        => File.ReadAllBytesAsync(path, ct);

    public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default)
        => File.WriteAllBytesAsync(path, content, ct);

    public bool FileExists(string path) => File.Exists(path);

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void MoveFile(string src, string dst, bool overwrite = false)
        => File.Move(src, dst, overwrite);

    public void AppendAllLines(string path, IEnumerable<string> lines)
        => File.AppendAllLines(path, lines);

    public string[] ReadAllLines(string path) => File.ReadAllLines(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public void WriteAllLines(string path, IEnumerable<string> lines)
        => File.WriteAllLines(path, lines);

    public FileMetadata? GetFileInfo(string path)
    {
        var info = new System.IO.FileInfo(path);
        if (!info.Exists) return null;
        return new FileMetadata(
            Length: info.Length,
            LastWriteTimeUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            IsReadOnly: info.IsReadOnly);
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path, bool recursive = false)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, pattern, opt);
    }

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream OpenWrite(string path) => File.Create(path);

    public async Task<IDisposable?> TryAcquireExclusiveLockAsync(
        string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fs = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.None);
                return new LockHandle(fs, path);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                try { await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _path;
        private int _disposed;

        public LockHandle(FileStream stream, string path)
        {
            _stream = stream;
            _path = path;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _stream.Dispose(); } catch { }
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }
}
