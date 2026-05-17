#nullable enable
namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over <see cref="System.IO.File"/> + <see cref="System.IO.Directory"/>
/// + <see cref="System.IO.Path"/> for unit-testability. Sufficient for VPNRouter's
/// actual usage: settings.yaml read/write, log file rotation, lock file
/// create/delete, JSON cache read/write, %ProgramData%\VPNRouter\* layout
/// management.
///
/// <para>
/// Production code injects <see cref="RealFileSystem"/>; unit tests inject
/// <c>InMemoryFileSystem</c> (in <c>VPNRouter.Tests/Fakes/</c>) to avoid
/// hitting %ProgramData% during xUnit runs. Phase 2D scope (see
/// <c>plans/phase2-2D-ifilesystem-2026-05-17.md</c>): interface + POC
/// refactor of <see cref="LockFile"/> + <see cref="HostsManager"/>.
/// Phase 2G migrates remaining services.
/// </para>
/// </summary>
public interface IFileSystem
{
    // ── File operations ──

    /// <summary>Read entire text file as UTF-8.</summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken ct = default);

    /// <summary>Write entire string as UTF-8, overwriting any existing file.</summary>
    Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);

    /// <summary>Read entire file as raw bytes.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);

    /// <summary>Write raw bytes, overwriting any existing file.</summary>
    Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>True iff a regular file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Delete the file at <paramref name="path"/>. No-throw if the file is
    /// already missing — matches <see cref="System.IO.File.Delete(string)"/>
    /// semantics (which does throw on permission errors; callers should
    /// catch those if they care).
    /// </summary>
    void DeleteFile(string path);

    /// <summary>Move a file. <paramref name="overwrite"/>=true mirrors .NET 8 <c>File.Move(src,dst,overwrite)</c>.</summary>
    void MoveFile(string src, string dst, bool overwrite = false);

    /// <summary>
    /// Append text lines to a file (creating it if missing). Mirrors
    /// <see cref="System.IO.File.AppendAllLines(string, IEnumerable{string})"/>.
    /// Used by <see cref="HostsManager"/> to add VPNRouter-marker blocks.
    /// </summary>
    void AppendAllLines(string path, IEnumerable<string> lines);

    /// <summary>
    /// Read every line of a file. Mirrors
    /// <see cref="System.IO.File.ReadAllLines(string)"/>. Used by
    /// <see cref="HostsManager"/> when reading the hosts file for marker
    /// detection.
    /// </summary>
    string[] ReadAllLines(string path);

    /// <summary>
    /// Read entire text file. Sync version for hot paths where
    /// <see cref="ReadAllTextAsync"/>'s state machine overhead is wasteful
    /// (e.g. startup config probes).
    /// </summary>
    string ReadAllText(string path);

    /// <summary>
    /// Write entire string, overwriting. Sync version (see
    /// <see cref="ReadAllText"/>).
    /// </summary>
    void WriteAllText(string path, string content);

    /// <summary>
    /// Write every line of <paramref name="lines"/>. Mirrors
    /// <see cref="System.IO.File.WriteAllLines(string, IEnumerable{string})"/>.
    /// </summary>
    void WriteAllLines(string path, IEnumerable<string> lines);

    /// <summary>
    /// Returns <see cref="FileMetadata"/> for the file at
    /// <paramref name="path"/>, or null if the file is missing.
    /// </summary>
    FileMetadata? GetFileInfo(string path);

    // ── Directory operations ──

    /// <summary>True iff a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Create the directory (and any missing parents). No-throw on
    /// pre-existing directory — mirrors
    /// <see cref="System.IO.Directory.CreateDirectory(string)"/>.
    /// </summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Delete a directory. With <paramref name="recursive"/>=true, removes
    /// subdirectories and files too.
    /// </summary>
    void DeleteDirectory(string path, bool recursive = false);

    /// <summary>
    /// Enumerate files under <paramref name="directory"/> matching
    /// <paramref name="pattern"/>. Returns absolute paths. With
    /// <paramref name="recursive"/>=true, descends into subdirectories.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false);

    // ── Stream operations ──

    /// <summary>
    /// Open a read-only stream over the file. Caller disposes. Used for
    /// large reads / hash computation where buffering the whole file would
    /// waste memory.
    /// </summary>
    Stream OpenRead(string path);

    /// <summary>
    /// Open a write stream, truncating any existing file. Caller disposes.
    /// </summary>
    Stream OpenWrite(string path);

    // ── Lock-file operations ──

    /// <summary>
    /// Attempt to acquire an exclusive lock on <paramref name="path"/>.
    /// Returns an <see cref="IDisposable"/> that releases the lock (and
    /// deletes the file) on disposal, or null on timeout. Used by
    /// <see cref="LockFile"/> for the anti-double-launch guard.
    ///
    /// <para>
    /// The real implementation opens a file with
    /// <see cref="System.IO.FileShare.None"/>; if another process already
    /// holds the file open, the call will retry every ~100ms until either
    /// the lock is acquired or <paramref name="timeout"/> elapses.
    /// </para>
    /// </summary>
    Task<IDisposable?> TryAcquireExclusiveLockAsync(string path, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of file metadata at a point in time. Records are immutable —
/// callers re-read via <see cref="IFileSystem.GetFileInfo"/> for fresh
/// values.
/// </summary>
public sealed record FileMetadata(long Length, DateTimeOffset LastWriteTimeUtc, bool IsReadOnly);
