#nullable enable
using System.Collections.Concurrent;
using System.Text;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// Thread-safe in-memory fake of <see cref="IFileSystem"/> for unit tests.
/// Storage is a <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by
/// the canonicalised path. Records the full access trail in
/// <see cref="AccessLog"/> so tests can assert "the service touched
/// %ProgramData%/VPNRouter/config.yaml exactly once".
///
/// <para>
/// Path canonicalisation: paths are compared using
/// <see cref="StringComparer.OrdinalIgnoreCase"/> on Windows (where the
/// real file system is case-insensitive) and the same here for
/// cross-platform test stability. Forward and back slashes are normalised
/// to <see cref="Path.DirectorySeparatorChar"/>.
/// </para>
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    /// <summary>
    /// Stored files. Key = normalised path. Value = raw bytes (matches
    /// real on-disk representation). All access goes through this
    /// dictionary's thread-safe operations.
    /// </summary>
    private readonly ConcurrentDictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stored directories (separate from files because a directory can
    /// exist without containing any file). Key = normalised path.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Held lock tags — used by <see cref="TryAcquireExclusiveLockAsync"/>
    /// to detect contention. Disposing the returned handle removes the
    /// entry. Key = normalised path.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _heldLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Append-only log of operations for test assertions. Synchronised
    /// via <see cref="ConcurrentQueue{T}"/> — no manual locking needed.
    /// </summary>
    public ConcurrentQueue<string> AccessLog { get; } = new();

    /// <summary>
    /// Retry interval used by <see cref="TryAcquireExclusiveLockAsync"/>.
    /// Tests can tune this lower than the real 100ms to speed up timeout
    /// scenarios; default mirrors <see cref="RealFileSystem"/>.
    /// </summary>
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
        // Mirror File.ReadAllLines: split on any line separator, drop the
        // single final empty entry caused by a trailing newline.
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
        // A directory also "exists" if any file under it does (matches
        // real FS behaviour where CreateDirectory is implicit via writes).
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
        // mkdir -p: register every parent too so DirectoryExists works
        // for them.
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
            // Non-recursive: skip anything containing a path separator
            // after the prefix.
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
        // MemoryStream over a defensive copy so callers can't mutate
        // backing store. Length is supported natively (no workaround).
        return new MemoryStream((byte[])entry.Bytes.Clone(), writable: false);
    }

    public Stream OpenWrite(string path)
    {
        var key = Norm(path);
        AccessLog.Enqueue($"OpenWrite({key})");
        EnsureParentDir(key);
        // Capturing stream that writes back on dispose.
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
                // Create the lock file so DetectPreviousCrash-style code
                // can read its contents.
                _files.TryAdd(key, new FileEntry(Array.Empty<byte>(), DateTimeOffset.UtcNow));
                return new LockHandle(this, key);
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
        }
    }

    // ── Test-only inspection helpers ──

    /// <summary>
    /// Direct-set a file's content. Bypasses the access log — handy for
    /// "seed the test environment with config.yaml" without polluting the
    /// log used by assertions.
    /// </summary>
    public void Seed(string path, string content)
        => _files[Norm(path)] = new FileEntry(Encoding.UTF8.GetBytes(content), DateTimeOffset.UtcNow);

    /// <summary>Direct-set a file's raw bytes. See <see cref="Seed(string,string)"/>.</summary>
    public void Seed(string path, byte[] content)
        => _files[Norm(path)] = new FileEntry((byte[])content.Clone(), DateTimeOffset.UtcNow);

    /// <summary>Number of stored files (for sanity assertions in tests).</summary>
    public int FileCount => _files.Count;

    /// <summary>
    /// Snapshot of all stored paths. Returns a defensive copy so callers
    /// can iterate without worrying about concurrent mutation.
    /// </summary>
    public IReadOnlyList<string> AllPaths => _files.Keys.ToList();

    // ── Internals ──

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
        // Convert "*" -> ".*", "?" -> ".". Anchored so "*.txt" doesn't
        // match "a.txt.bak".
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

    /// <summary>
    /// Lock-handle returned from <see cref="TryAcquireExclusiveLockAsync"/>.
    /// Mirrors the real impl: dispose releases the lock AND deletes the
    /// underlying file, so a follow-up acquire sees a clean slate.
    /// </summary>
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

    /// <summary>
    /// Memory-backed write stream that flushes its content into the
    /// in-memory store on dispose. Supports Length natively via the
    /// underlying <see cref="MemoryStream"/>.
    /// </summary>
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
