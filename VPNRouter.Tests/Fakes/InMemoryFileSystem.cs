#nullable enable
using System.Collections.Concurrent;
using System.Text;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _heldLocks = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> AccessLog { get; } = new();

    public TimeSpan LockRetryDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default)
        => Task.FromResult(ReadAllText(path));

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        WriteAllText(path, content);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"ReadAllBytesAsync({key})");
        if (!_files.TryGetValue(key, out var entry))
            throw new FileNotFoundException($"File not found: {path}", path);
        return Task.FromResult((byte[])entry.Bytes.Clone());
    }

    public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"WriteAllBytesAsync({key}, {content.Length}B)");
        EnsureParentDir(key);
        _files[key] = new FileEntry((byte[])content.Clone(), DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public bool FileExists(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"FileExists({key})");
        return _files.ContainsKey(key);
    }

    public void DeleteFile(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"DeleteFile({key})");
        _files.TryRemove(key, out _);
    }

    public void MoveFile(string src, string dst, bool overwrite = false)
    {
        var srcKey = Norm(src);
        var dstKey = Norm(dst);
        AccessLog.Enqueue($"MoveFile({srcKey} -> {dstKey}, overwrite={overwrite})");
        if (!_files.TryGetValue(srcKey, out var entry))
            throw new FileNotFoundException($"File not found: {src}", src);
        if (!overwrite && _files.ContainsKey(dstKey))
            throw new IOException($"Destination file exists: {dst}");
        EnsureParentDir(dstKey);
        _files[dstKey] = entry;
        _files.TryRemove(srcKey, out _);
    }

    public void AppendAllLines(string path, IEnumerable<string> lines)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"AppendAllLines({key})");
        EnsureParentDir(key);
        var sb = new StringBuilder();
        if (_files.TryGetValue(key, out var existing))
            sb.Append(Encoding.UTF8.GetString(existing.Bytes));
        foreach (var line in lines)
        {
            sb.Append(line);
            sb.Append(Environment.NewLine);
        }
        _files[key] = new FileEntry(Encoding.UTF8.GetBytes(sb.ToString()), DateTimeOffset.UtcNow);
    }

    public string[] ReadAllLines(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"ReadAllLines({key})");
        if (!_files.TryGetValue(key, out var entry))
            throw new FileNotFoundException($"File not found: {path}", path);
        var text = Encoding.UTF8.GetString(entry.Bytes);
        var parts = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (parts.Length > 0 && parts[^1].Length == 0)
            return parts[..^1];
        return parts;
    }

    public string ReadAllText(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"ReadAllText({key})");
        if (!_files.TryGetValue(key, out var entry))
            throw new FileNotFoundException($"File not found: {path}", path);
        return Encoding.UTF8.GetString(entry.Bytes);
    }

    public void WriteAllText(string path, string content)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"WriteAllText({key}, {content.Length}c)");
        EnsureParentDir(key);
        _files[key] = new FileEntry(Encoding.UTF8.GetBytes(content), DateTimeOffset.UtcNow);
    }

    public void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"WriteAllLines({key})");
        EnsureParentDir(key);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line);
            sb.Append(Environment.NewLine);
        }
        _files[key] = new FileEntry(Encoding.UTF8.GetBytes(sb.ToString()), DateTimeOffset.UtcNow);
    }

    public FileMetadata? GetFileInfo(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"GetFileInfo({key})");
        return _files.TryGetValue(key, out var entry)
            ? new FileMetadata(entry.Bytes.Length, entry.LastWriteTimeUtc, IsReadOnly: false)
            : null;
    }

    public bool DirectoryExists(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"DirectoryExists({key})");
        if (_directories.ContainsKey(key)) return true;
        var prefix = key + Path.DirectorySeparatorChar;
        foreach (var f in _files.Keys)
        {
            if (f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void CreateDirectory(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"CreateDirectory({key})");
        var cur = key;
        while (!string.IsNullOrEmpty(cur))
        {
            _directories.TryAdd(cur, 0);
            var parent = Path.GetDirectoryName(cur);
            if (parent == cur || string.IsNullOrEmpty(parent)) break;
            cur = parent;
        }
    }

    public void DeleteDirectory(string path, bool recursive = false)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"DeleteDirectory({key}, recursive={recursive})");
        if (recursive)
        {
            var prefix = key + Path.DirectorySeparatorChar;
            foreach (var f in _files.Keys)
            {
                if (f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    _files.TryRemove(f, out _);
            }
            foreach (var d in _directories.Keys)
            {
                if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || d.Equals(key, StringComparison.OrdinalIgnoreCase))
                    _directories.TryRemove(d, out _);
            }
        }
        _directories.TryRemove(key, out _);
    }

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
    {
        var key = Norm(directory);
        AccessLog.Enqueue($"EnumerateFiles({key}, {pattern}, recursive={recursive})");
        var prefix = key + Path.DirectorySeparatorChar;
        var matcher = WildcardToRegex(pattern);
        foreach (var f in _files.Keys)
        {
            if (!f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = f[prefix.Length..];
            if (!recursive && rel.Contains(Path.DirectorySeparatorChar)) continue;
            var name = Path.GetFileName(f);
            if (matcher.IsMatch(name)) yield return f;
        }
    }

    public Stream OpenRead(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"OpenRead({key})");
        if (!_files.TryGetValue(key, out var entry))
            throw new FileNotFoundException($"File not found: {path}", path);
        return new MemoryStream((byte[])entry.Bytes.Clone(), writable: false);
    }

    public Stream OpenWrite(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"OpenWrite({key})");
        EnsureParentDir(key);
        return new CapturingStream(this, key);
    }

    public async Task<IDisposable?> TryAcquireExclusiveLockAsync(
        string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"TryAcquireExclusiveLockAsync({key}, timeout={timeout})");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_heldLocks.TryAdd(key, 0))
            {
                EnsureParentDir(key);
                _files.TryAdd(key, new FileEntry(Array.Empty<byte>(), DateTimeOffset.UtcNow));
                return new LockHandle(this, key);
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
        }
    }

    public void Seed(string path, string content)
        => _files[Norm(path)] = new FileEntry(Encoding.UTF8.GetBytes(content), DateTimeOffset.UtcNow);

    public void Seed(string path, byte[] content)
        => _files[Norm(path)] = new FileEntry((byte[])content.Clone(), DateTimeOffset.UtcNow);

    public int FileCount => _files.Count;

    public IReadOnlyList<string> AllPaths => _files.Keys.ToList();

    private static string Norm(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private void EnsureParentDir(string normalisedKey)
    {
        var parent = Path.GetDirectoryName(normalisedKey);
        if (string.IsNullOrEmpty(parent)) return;
        _directories.TryAdd(parent, 0);
    }

    private static System.Text.RegularExpressions.Regex WildcardToRegex(string pattern)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return new System.Text.RegularExpressions.Regex(rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void ReleaseLock(string key)
    {
        _heldLocks.TryRemove(key, out _);
        _files.TryRemove(key, out _);
    }

    private sealed record FileEntry(byte[] Bytes, DateTimeOffset LastWriteTimeUtc);

    private sealed class LockHandle : IDisposable
    {
        private readonly InMemoryFileSystem _fs;
        private readonly string _key;
        private int _disposed;

        public LockHandle(InMemoryFileSystem fs, string key)
        {
            _fs = fs;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _fs.ReleaseLock(_key);
        }
    }

    private sealed class CapturingStream : MemoryStream
    {
        private readonly InMemoryFileSystem _fs;
        private readonly string _key;
        private int _flushed;

        public CapturingStream(InMemoryFileSystem fs, string key)
        {
            _fs = fs;
            _key = key;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _flushed, 1) == 0)
            {
                _fs._files[_key] = new FileEntry(ToArray(), DateTimeOffset.UtcNow);
            }
            base.Dispose(disposing);
        }
    }
}
