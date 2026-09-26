#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.Core.Services;

internal enum SingBoxRuntimeOperation
{
    Inspect,
    Probe,
    Verify,
    Start,
    Restart
}

internal enum SingBoxRuntimeFailure
{
    None = 0,
    Missing,
    Untrusted,
    Changed,
    PrerequisiteUnavailable
}

internal sealed class SingBoxRuntimePolicyException : InvalidOperationException
{
    public SingBoxRuntimeOperation Operation { get; }
    public SingBoxRuntimeFailure Failure { get; }

    public SingBoxRuntimePolicyException(SingBoxRuntimeOperation operation, SingBoxRuntimeFailure failure)
        : base("sing-box runtime is unavailable or untrusted.")
    {
        Operation = operation;
        Failure = failure;
    }
}

/// <summary>
/// Scoped runtime trust policy for the sing-box binary on Linux Headless.
/// Manages operation authorization, file identity verification, single selected path,
/// and safe typed failures (Missing, Untrusted, Changed, PrerequisiteUnavailable).
/// </summary>
internal sealed class SingBoxRuntimePolicy
{
    private static readonly AsyncLocal<SingBoxRuntimePolicy?> s_current = new();

    public static SingBoxRuntimePolicy? Current => s_current.Value;

    /// <summary>
    /// Atomically captures the active policy into <paramref name="retained"/> with sticky retention semantics:
    /// - DefaultProduction dominates current/retained always.
    /// - Latches the first fixture if retained is null.
    /// - A differing fixture never silently switches identity.
    /// Uses an Interlocked CAS loop with Volatile.Read so races cannot overwrite denial with a fixture.
    /// Refuses concurrent identity replacement claims beyond actual capability.
    /// </summary>
    internal static SingBoxRuntimePolicy? Capture(ref SingBoxRuntimePolicy? retained)
    {
        var current = Current;

        while (true)
        {
            var initial = Volatile.Read(ref retained);

            // defaultProduction dominates current/retained always
            if (ReferenceEquals(initial, DefaultProduction))
            {
                return DefaultProduction;
            }

            if (ReferenceEquals(current, DefaultProduction))
            {
                // Current is DefaultProduction: it dominates always and must be latched.
                // Race cannot overwrite denial with fixture.
                if (Interlocked.CompareExchange(ref retained, DefaultProduction, initial) == initial)
                {
                    return DefaultProduction;
                }
                continue;
            }

            // At this point, neither initial nor current is DefaultProduction.
            if (initial == null)
            {
                // Latch first fixture if null
                if (current == null)
                {
                    return null;
                }

                if (Interlocked.CompareExchange(ref retained, current, null) == null)
                {
                    return current;
                }
                continue;
            }

            // initial is already set to a non-null fixture.
            // Differing fixture never silently switches identity; refuse identity replacement.
            return initial;
        }
    }

    public static IDisposable EnterScope(SingBoxRuntimePolicy? policy)
    {
        var previous = s_current.Value;
        if (ReferenceEquals(previous, DefaultProduction))
        {
            // DefaultProduction dominates: keep DefaultProduction if current default (cannot weaken),
            // while restoring prior scope on dispose.
            return new ScopeHolder(previous, changed: true);
        }

        if (policy != null)
        {
            s_current.Value = policy;
            return new ScopeHolder(previous, changed: true);
        }

        return new ScopeHolder(previous, changed: false);
    }

    private sealed class ScopeHolder : IDisposable
    {
        private readonly SingBoxRuntimePolicy? _previous;
        private readonly bool _changed;
        private bool _disposed;

        public ScopeHolder(SingBoxRuntimePolicy? previous, bool changed)
        {
            _previous = previous;
            _changed = changed;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_changed)
                {
                    s_current.Value = _previous;
                }
            }
        }
    }

    /// <summary>
    /// Default production policy for Linux Headless: Unavailable / Untrusted
    /// because no production trusted artifact is yet authorized.
    /// SelectedExecutablePath is strictly null as no trusted binary is selected.
    /// </summary>
    public static SingBoxRuntimePolicy DefaultProduction { get; } = new(
        isProductionUnavailable: true,
        path: null,
        expectedSha256: null,
        prerequisites: null);

    private const long MaxFileSizeBytes = 256L * 1024 * 1024; // 256 MiB

    private readonly bool _isProductionUnavailable;
    private readonly string? _path;
    private readonly string? _expectedSha256;
    private readonly Func<bool>? _prerequisites;
    private readonly object _validationLock = new();
    private bool _isValidating;
    private bool _hasEverMatched;

    internal static Func<uint>? GeteuidOverrideForTests { get; set; }

    private SingBoxRuntimePolicy(
        bool isProductionUnavailable,
        string? path,
        string? expectedSha256,
        Func<bool>? prerequisites)
    {
        _isProductionUnavailable = isProductionUnavailable;
        _path = path;
        _expectedSha256 = expectedSha256;
        _prerequisites = prerequisites;
    }

    /// <summary>
    /// Internal test factory accepting only bounded, regular, non-symlink paths.
    /// Re-hashes on each use and provides typed safe failure diagnostics.
    /// </summary>
    public static SingBoxRuntimePolicy ForTestFile(
        string absolutePath,
        string expectedSha256,
        Func<bool>? prerequisites = null)
    {
        ValidatePathBoundedAndFormatted(absolutePath);
        ValidateExpectedSha256(expectedSha256);

        return new SingBoxRuntimePolicy(
            isProductionUnavailable: false,
            path: absolutePath,
            expectedSha256: expectedSha256.Trim().ToLowerInvariant(),
            prerequisites: prerequisites);
    }

    /// <summary>
    /// Single selected executable path used across availability, start/restart,
    /// feature probes, deep verifiers, and diagnostic metadata.
    /// Strictly null when no production binary is authorized.
    /// </summary>
    public string? SelectedExecutablePath => _isProductionUnavailable ? null : _path;

    /// <summary>
    /// Fork features are strictly false until authorized by approved metadata;
    /// runtime binary execution is not trusted for capability discovery.
    /// </summary>
    public bool AwgAvailable => false;
    public bool XhttpAvailable => false;

    /// <summary>
    /// Availability indicates whether the runtime can be authorized for executable operations.
    /// Unavailable when production policy is active, UID is root (0), or validation fails.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (_isProductionUnavailable)
                return false;

            return TryAuthorize(SingBoxRuntimeOperation.Probe, out _);
        }
    }

    public void Authorize(SingBoxRuntimeOperation operation)
    {
        if (!TryAuthorize(operation, out var failure))
        {
            throw new SingBoxRuntimePolicyException(operation, failure);
        }
    }

    public bool TryAuthorize(SingBoxRuntimeOperation operation, out SingBoxRuntimeFailure failure)
    {
        if (_isProductionUnavailable)
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        lock (_validationLock)
        {
            // Reentrancy guard: deny recursive authorization to prevent stack overflow from callbacks
            if (_isValidating)
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            _isValidating = true;
            try
            {
                return ValidateCore(operation, out failure);
            }
            finally
            {
                _isValidating = false;
            }
        }
    }

    private bool ValidateCore(SingBoxRuntimeOperation operation, out SingBoxRuntimeFailure failure)
    {
        if (string.IsNullOrEmpty(_path))
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        if (!TryGetCurrentEuid(out uint currentEuid))
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        // Refuse root executable operations under restricted policy
        if (operation == SingBoxRuntimeOperation.Probe ||
            operation == SingBoxRuntimeOperation.Verify ||
            operation == SingBoxRuntimeOperation.Start ||
            operation == SingBoxRuntimeOperation.Restart)
        {
            if (currentEuid == 0)
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            return ValidateLinux(out failure);
        }

        if (OperatingSystem.IsWindows())
        {
            return ValidateWindows(out failure);
        }

        // Fail unsupported Unix platforms closed; legacy null callers are unaffected
        failure = SingBoxRuntimeFailure.Untrusted;
        return false;
    }

    private bool ValidateLinux(out SingBoxRuntimeFailure failure)
    {
        try
        {
            // Open with O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC to prevent FIFO hanging and symlink traversal
            const int flags = O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC;
            int fd = Open(_path!, flags, 0);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ENOENT)
                {
                    failure = SingBoxRuntimeFailure.Missing;
                    return false;
                }
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            using var safeHandle = new SafeFileHandle((nint)fd, ownsHandle: true);

            // Descriptor statx AT_EMPTY_PATH regular/type mask/size verification BEFORE read
            int statRes = Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var beforeStat);
            if (statRes != 0)
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            if ((beforeStat.stx_mask & (STATX_TYPE | STATX_SIZE)) != (STATX_TYPE | STATX_SIZE))
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            // Must be regular file (rejects FIFOs, directories, symlinks, sockets, devices)
            if ((beforeStat.stx_mode & S_IFMT) != S_IFREG)
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            // Verify size <= 256 MiB before reading (rejects oversized sparse files without read)
            if (beforeStat.stx_size > (ulong)MaxFileSizeBytes)
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }

            // Check ancestor directories are regular directories and not symlinks
            // Note: Ancestor directory checks verify current path components are not symlinks,
            // but cannot guarantee race-free execution against subsequent path replacement without fexecve/descriptor execution support.
            string? current = Path.GetDirectoryName(_path!);
            while (!string.IsNullOrEmpty(current) && current != "/" && current != Path.GetPathRoot(current))
            {
                if (Statx(AT_FDCWD, current, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_MODE, out var dirBuf) != 0)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }
                if ((dirBuf.stx_mode & S_IFMT) != S_IFDIR)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }
                current = Path.GetDirectoryName(current);
            }

            // Check launch/runtime prerequisites
            if (_prerequisites != null)
            {
                bool ok;
                try { ok = _prerequisites(); } catch { ok = false; }
                if (!ok)
                {
                    failure = SingBoxRuntimeFailure.PrerequisiteUnavailable;
                    return false;
                }
            }

            // Compute hash with bounded read loop (max 256 MiB + 1 even if file grows)
            long totalBytesRead = 0;
            string currentHash;
            using (var stream = new FileStream(safeHandle, FileAccess.Read))
            {
                using var sha = SHA256.Create();
                byte[] buffer = new byte[64 * 1024];

                while (true)
                {
                    int toRead = (int)Math.Min(buffer.Length, (MaxFileSizeBytes + 1) - totalBytesRead);
                    if (toRead <= 0)
                    {
                        failure = SingBoxRuntimeFailure.Untrusted;
                        return false;
                    }

                    int bytesRead = stream.Read(buffer, 0, toRead);
                    if (bytesRead == 0)
                        break;

                    totalBytesRead += bytesRead;
                    if (totalBytesRead > MaxFileSizeBytes)
                    {
                        failure = SingBoxRuntimeFailure.Untrusted;
                        return false;
                    }

                    sha.TransformBlock(buffer, 0, bytesRead, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                currentHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

                // Before/after descriptor metadata verify while fd is still open
                int afterRes = Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var afterStat);
                if (afterRes != 0)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }

                if ((afterStat.stx_mask & (STATX_TYPE | STATX_SIZE)) != (STATX_TYPE | STATX_SIZE))
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }

                if ((afterStat.stx_mode & S_IFMT) != S_IFREG)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }

                if (afterStat.stx_ino != beforeStat.stx_ino ||
                    afterStat.stx_dev_major != beforeStat.stx_dev_major ||
                    afterStat.stx_dev_minor != beforeStat.stx_dev_minor ||
                    afterStat.stx_size != beforeStat.stx_size ||
                    (ulong)totalBytesRead != beforeStat.stx_size ||
                    afterStat.stx_mtime.tv_sec != beforeStat.stx_mtime.tv_sec ||
                    afterStat.stx_mtime.tv_nsec != beforeStat.stx_mtime.tv_nsec)
                {
                    failure = _hasEverMatched ? SingBoxRuntimeFailure.Changed : SingBoxRuntimeFailure.Untrusted;
                    return false;
                }
            }

            if (string.Equals(currentHash, _expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _hasEverMatched = true;
                failure = SingBoxRuntimeFailure.None;
                return true;
            }

            if (_hasEverMatched)
            {
                failure = SingBoxRuntimeFailure.Changed;
                return false;
            }

            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }
        catch (DllNotFoundException)
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }
        catch (Exception)
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }
    }

    private bool ValidateWindows(out SingBoxRuntimeFailure failure)
    {
        if (!File.Exists(_path!))
        {
            if (Directory.Exists(_path!))
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }
            failure = SingBoxRuntimeFailure.Missing;
            return false;
        }

        var fi = new FileInfo(_path!);
        if (fi.LinkTarget != null ||
            fi.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            fi.Attributes.HasFlag(FileAttributes.Directory))
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        if (fi.Length > MaxFileSizeBytes)
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        var dir = fi.Directory;
        while (dir != null && dir.Parent != null)
        {
            if (dir.LinkTarget != null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                failure = SingBoxRuntimeFailure.Untrusted;
                return false;
            }
            dir = dir.Parent;
        }

        if (_prerequisites != null)
        {
            bool ok;
            try { ok = _prerequisites(); } catch { ok = false; }
            if (!ok)
            {
                failure = SingBoxRuntimeFailure.PrerequisiteUnavailable;
                return false;
            }
        }

        string currentHash;
        long totalBytesRead = 0;
        try
        {
            var initialLength = fi.Length;
            var initialWriteTime = fi.LastWriteTimeUtc;

            using var stream = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            byte[] buffer = new byte[64 * 1024];

            while (true)
            {
                int toRead = (int)Math.Min(buffer.Length, (MaxFileSizeBytes + 1) - totalBytesRead);
                if (toRead <= 0)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }

                int bytesRead = stream.Read(buffer, 0, toRead);
                if (bytesRead == 0)
                    break;

                totalBytesRead += bytesRead;
                if (totalBytesRead > MaxFileSizeBytes)
                {
                    failure = SingBoxRuntimeFailure.Untrusted;
                    return false;
                }

                sha.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            currentHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            fi.Refresh();
            if (fi.Length != initialLength || fi.LastWriteTimeUtc != initialWriteTime || totalBytesRead != initialLength)
            {
                failure = _hasEverMatched ? SingBoxRuntimeFailure.Changed : SingBoxRuntimeFailure.Untrusted;
                return false;
            }
        }
        catch (FileNotFoundException)
        {
            failure = SingBoxRuntimeFailure.Missing;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            failure = SingBoxRuntimeFailure.Missing;
            return false;
        }
        catch
        {
            failure = SingBoxRuntimeFailure.Untrusted;
            return false;
        }

        if (string.Equals(currentHash, _expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _hasEverMatched = true;
            failure = SingBoxRuntimeFailure.None;
            return true;
        }

        if (_hasEverMatched)
        {
            failure = SingBoxRuntimeFailure.Changed;
            return false;
        }

        failure = SingBoxRuntimeFailure.Untrusted;
        return false;
    }

    private static void ValidatePathBoundedAndFormatted(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path cannot be empty.", nameof(absolutePath));

        if (absolutePath.Length > 4096)
            throw new ArgumentException("Path exceeds maximum length of 4096 characters.", nameof(absolutePath));

        if (absolutePath.IndexOf('\0') >= 0)
            throw new ArgumentException("Path cannot contain null characters.", nameof(absolutePath));

        if (!Path.IsPathRooted(absolutePath))
            throw new ArgumentException("Path must be an absolute rooted path.", nameof(absolutePath));

        var fullPath = Path.GetFullPath(absolutePath);
        if (!string.Equals(fullPath, absolutePath, StringComparison.Ordinal))
            throw new ArgumentException("Path must be normalized and cannot contain relative traversal segments.", nameof(absolutePath));

        string[] parts = absolutePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length > 255)
                throw new ArgumentException("Path component exceeds maximum length of 255 characters.", nameof(absolutePath));
        }
    }

    private static void ValidateExpectedSha256(string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new ArgumentException("Expected SHA-256 cannot be empty.", nameof(expectedSha256));

        var trimmed = expectedSha256.Trim();
        if (trimmed.Length != 64)
            throw new ArgumentException("Expected SHA-256 must be exactly 64 hexadecimal characters.", nameof(expectedSha256));

        foreach (char c in trimmed)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                throw new ArgumentException("Expected SHA-256 contains non-hexadecimal characters.", nameof(expectedSha256));
        }
    }

    private static bool TryGetCurrentEuid(out uint uid)
    {
        if (GeteuidOverrideForTests != null)
        {
            try
            {
                uid = GeteuidOverrideForTests();
                return true;
            }
            catch
            {
                uid = 0;
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                uid = Geteuid();
                return true;
            }
            catch
            {
                uid = 0;
                return false;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            uid = 1000;
            return true;
        }

        uid = 0;
        return false;
    }

    #region Native Linux Interop

    private const int O_RDONLY = 0x0000;
    private const int O_NONBLOCK = 0x0800;
    private const int O_NOFOLLOW = 0x00020000;
    private const int O_CLOEXEC = 0x00080000;

    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x0100;
    private const int AT_EMPTY_PATH = 0x1000;

    private const uint STATX_TYPE = 0x0001;
    private const uint STATX_MODE = 0x0002;
    private const uint STATX_SIZE = 0x0200;
    private const uint STATX_BASIC_STATS = 0x07FF;

    private const uint S_IFMT = 0xF000;
    private const uint S_IFREG = 0x8000;
    private const uint S_IFDIR = 0x4000;

    private const int ENOENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long tv_sec;
        public uint tv_nsec;
        public int __pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxStruct
    {
        public uint stx_mask;
        public uint stx_blksize;
        public ulong stx_attributes;
        public uint stx_nlink;
        public uint stx_uid;
        public uint stx_gid;
        public ushort stx_mode;
        public ushort __spare0;
        public ulong stx_ino;
        public ulong stx_size;
        public ulong stx_blocks;
        public ulong stx_attributes_mask;
        public StatxTimestamp stx_atime;
        public StatxTimestamp stx_btime;
        public StatxTimestamp stx_ctime;
        public StatxTimestamp stx_mtime;
        public uint stx_rdev_major;
        public uint stx_rdev_minor;
        public uint stx_dev_major;
        public uint stx_dev_minor;
        public ulong spare2_0;
        public ulong spare2_1;
        public ulong spare2_2;
        public ulong spare2_3;
        public ulong spare2_4;
        public ulong spare2_5;
        public ulong spare2_6;
        public ulong spare2_7;
        public ulong spare2_8;
        public ulong spare2_9;
        public ulong spare2_10;
        public ulong spare2_11;
        public ulong spare2_12;
        public ulong spare2_13;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string pathname, int flags, int mode);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int dirfd,
        string pathname,
        int flags,
        uint mask,
        out StatxStruct statxbuf);

    #endregion
}
