#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Real Linux interprocess mutual exclusion for TUN/sing-box ownership using kernel flock.
///
/// <para><strong>Directory Resolution &amp; Verification:</strong>
/// Production lock path is strictly <c>/run/user/&lt;euid&gt;/vpnrouter-tun.lock</c> (systemd XDG_RUNTIME_DIR),
/// verified to match the current effective UID, private directory permissions (<c>0700</c> with no group or world access),
/// and non-symlink status.
/// Fallback to <c>AppPaths.DataDir</c> is explicitly forbidden by architectural policy to prevent runtime path switches
/// and per-process <c>--data-dir</c> divergence (where distinct instances would lock different files and concurrently conflict
/// on the single host TUN device).</para>
///
/// <para><strong>Actual Platform Limitation:</strong>
/// If <c>/run/user/&lt;euid&gt;</c> is missing (e.g. non-systemd environments, minimalist containers without pam_systemd,
/// or headless cron without a systemd user session), runtime resolution fails closed: <see cref="ProbeOwnership"/>
/// returns <see cref="TunOwnershipStatus.Unavailable"/> and <see cref="TryAcquire"/> returns <c>false</c>.
/// Supported Omarchy desktop environments always provision <c>/run/user/&lt;euid&gt;</c>.
/// Production code must never attempt to create or chmod the runtime directory.</para>
///
/// <para><strong>Security &amp; Invariants:</strong>
/// Files are opened with <c>O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC</c> and mode <c>0600</c>.
/// The opened file descriptor is verified via fstatx (<c>statx</c> with <c>AT_EMPTY_PATH</c>) to validate UID, regular file
/// type (<c>S_IFREG</c>), and private mode (<c>0600</c>), rejecting symlinks, FIFOs, and lax files.
/// This coordinates cooperating processes of the same UID; descriptor checks do not prevent
/// that UID from replacing the lock path, and separate UIDs do not share this lock.
/// An acquired lock holds the file descriptor open with <c>flock(LOCK_EX | LOCK_NB)</c> until released or disposed.
/// Unclean process termination or crash causes the Linux kernel to automatically release the flock.
/// Probes never create directories or files: an absent lock file in a validated parent directory indicates Free,
/// while an unverifiable path, non-existent runtime dir, or permission error indicates Unavailable (fail-closed).</para>
/// </summary>
public sealed class LinuxTunOwnership : IDisposable
{
    public const string LockFileName = "vpnrouter-tun.lock";

    private readonly ILogger _logger;
    private readonly string? _testRuntimeDirectory;
    private readonly object _gate = new();
    private int _heldFd = -1;
    private bool _owned;
    private bool _disposed;
    private string? _heldLockPath;

    /// <summary>
    /// Explicit test hook for isolated testing directories. When null, production resolution is used.
    /// Environment variable overrides are strictly disallowed.
    /// </summary>
    public static string? OverrideRuntimeDirectory { get; set; }

    public LinuxTunOwnership(ILogger? logger = null, string? testRuntimeDirectory = null)
    {
        _logger = logger ?? Log.Logger;
        _testRuntimeDirectory = testRuntimeDirectory;
    }

    public bool HasOwnership => _owned;
    public string? HeldLockPath => _heldLockPath;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_owned && _heldFd >= 0)
                return true;

            var (dir, isValid, details) = ResolveRuntimeDirectory(forCreate: true, _testRuntimeDirectory);
            if (!isValid || string.IsNullOrEmpty(dir))
            {
                _logger.Warning("[LinuxTunOwnership] Cannot acquire lock: runtime directory is invalid or unverifiable ({Details})", details);
                return false;
            }

            var lockPath = Path.Combine(dir, LockFileName);
            uint currentUid = Geteuid();

            // O_RDWR | O_CREAT | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, mode 0600
            const int flags = O_RDWR | O_CREAT | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC;
            const int createMode = (int)MODE_0600;

            int fd = Open(lockPath, flags, createMode);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                _logger.Warning("[LinuxTunOwnership] Failed to open lock file at {Path} (errno {Errno})", lockPath, err);
                return false;
            }

            try
            {
                // Verify opened file directly via fstatx: must be regular file, owned by current UID, and private
                int statRes = Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var statxBuf);
                if (statRes != 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    _logger.Warning("[LinuxTunOwnership] fstatx failed on lock fd (errno {Errno})", err);
                    Close(fd);
                    return false;
                }

                uint fileType = statxBuf.stx_mode & S_IFMT;
                if (fileType != S_IFREG)
                {
                    _logger.Warning("[LinuxTunOwnership] Lock target {Path} is not a regular file (type 0x{Type:X}); rejecting", lockPath, fileType);
                    Close(fd);
                    return false;
                }

                if (statxBuf.stx_uid != currentUid)
                {
                    _logger.Warning("[LinuxTunOwnership] Lock target {Path} is owned by UID {Uid} (expected {ExpectedUid}); rejecting", lockPath, statxBuf.stx_uid, currentUid);
                    Close(fd);
                    return false;
                }

                // Check mode: reject if group or other permissions are present
                if ((statxBuf.stx_mode & S_IRWXG_O) != 0)
                {
                    _logger.Warning("[LinuxTunOwnership] Lock target {Path} has non-private permissions (mode 0{Mode}); rejecting", lockPath, Convert.ToString(statxBuf.stx_mode & S_PERM_MASK, 8).PadLeft(3, '0'));
                    Close(fd);
                    return false;
                }

                // Attempt exclusive non-blocking lock
                int flockRes = Flock(fd, LOCK_EX | LOCK_NB);
                if (flockRes == 0)
                {
                    _heldFd = fd;
                    _owned = true;
                    _heldLockPath = lockPath;
                    _logger.Information("[LinuxTunOwnership] Acquired exclusive flock on {Path} (fd {Fd})", lockPath, fd);
                    return true;
                }

                int flockErr = Marshal.GetLastPInvokeError();
                if (flockErr == EWOULDBLOCK || flockErr == EAGAIN)
                {
                    _logger.Information("[LinuxTunOwnership] Lock file {Path} is currently held by another process (contention)", lockPath);
                }
                else
                {
                    _logger.Warning("[LinuxTunOwnership] flock failed with unexpected errno {Errno} on {Path}", flockErr, lockPath);
                }

                Close(fd);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxTunOwnership] Exception during TryAcquire");
                Close(fd);
                return false;
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!_owned && _heldFd < 0) return;

            try
            {
                if (_heldFd >= 0)
                {
                    int fd = _heldFd;
                    _heldFd = -1;
                    Flock(fd, LOCK_UN);
                    Close(fd);
                    _logger.Information("[LinuxTunOwnership] Released flock on {Path} (fd {Fd})", _heldLockPath, fd);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxTunOwnership] Exception releasing lock fd {Fd}", _heldFd);
            }
            finally
            {
                _owned = false;
                _heldLockPath = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Release();
        }
    }

    /// <summary>
    /// Probe ownership without acquiring the lock or creating directories/files.
    /// Never creates or chmods files or directories.
    /// </summary>
    public static TunOwnershipStatus ProbeOwnership()
    {
        var (dir, isValid, _) = ResolveRuntimeDirectory(forCreate: false);
        if (!isValid || string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return TunOwnershipStatus.Unavailable;
        }

        var lockPath = Path.Combine(dir, LockFileName);
        uint currentUid = Geteuid();

        // Open existing file non-blocking, without creating, no symlinks, close on exec
        int fd = Open(lockPath, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err == ENOENT)
            {
                // Lock file does not exist in validated private directory -> Free
                return TunOwnershipStatus.Free;
            }
            // ELOOP (symlink), EACCES, etc. -> fail closed
            return TunOwnershipStatus.Unavailable;
        }

        try
        {
            // Verify opened fd directly via fstatx to prevent TOCTOU and reject FIFOs/symlinks/wrong owner/lax permissions
            int statRes = Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var statxBuf);
            if (statRes != 0)
            {
                return TunOwnershipStatus.Unavailable;
            }

            // Must be regular file (rejects FIFOs, sockets, directories, devices)
            if ((statxBuf.stx_mode & S_IFMT) != S_IFREG)
            {
                return TunOwnershipStatus.Unavailable;
            }

            // Must be owned by current EUID
            if (statxBuf.stx_uid != currentUid)
            {
                return TunOwnershipStatus.Unavailable;
            }

            // Must have private permissions (no group/other access)
            if ((statxBuf.stx_mode & S_IRWXG_O) != 0)
            {
                return TunOwnershipStatus.Unavailable;
            }

            int flockRes = Flock(fd, LOCK_EX | LOCK_NB);
            if (flockRes == 0)
            {
                // Lock was free! Immediately unlock
                Flock(fd, LOCK_UN);
                return TunOwnershipStatus.Free;
            }

            int flockErr = Marshal.GetLastPInvokeError();
            if (flockErr == EWOULDBLOCK || flockErr == EAGAIN)
            {
                return TunOwnershipStatus.Owned;
            }

            return TunOwnershipStatus.Unavailable;
        }
        finally
        {
            Close(fd);
        }
    }

    /// <summary>
    /// Resolves and validates the Linux runtime directory for the lock file.
    /// Production path is strictly /run/user/&lt;euid&gt;.
    /// No fallback to AppPaths.DataDir is permitted to prevent runtime path switches and --data-dir divergence.
    /// If /run/user/&lt;euid&gt; is absent or invalid, returns IsValid = false (missing runtime dir =&gt; Unavailable/false).
    /// Production never creates or chmods the runtime directory.
    /// </summary>
    public static (string? Directory, bool IsValid, string? Details) ResolveRuntimeDirectory(bool forCreate, string? instanceOverride = null)
    {
        uint currentUid = Geteuid();

        var overrideDir = instanceOverride ?? OverrideRuntimeDirectory;
        if (overrideDir is not null)
        {
            if (Directory.Exists(overrideDir))
            {
                if (TryValidateDirectory(overrideDir, currentUid, out var failReason))
                    return (overrideDir, true, null);
                return (overrideDir, false, failReason);
            }

            return (overrideDir, false, "Test override directory does not exist");
        }

        // Production: strictly verified /run/user/<euid> (never create or chmod runtime dir)
        var runUserDir = $"/run/user/{currentUid}";
        if (!Directory.Exists(runUserDir))
        {
            return (runUserDir, false, $"Production runtime directory {runUserDir} does not exist");
        }

        if (!TryValidateDirectory(runUserDir, currentUid, out var dirFailReason))
        {
            return (runUserDir, false, dirFailReason);
        }

        return (runUserDir, true, null);
    }

    private static bool TryValidateDirectory(string dirPath, uint expectedUid, out string? failureReason)
    {
        int statRes = Statx(AT_FDCWD, dirPath, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out var statxBuf);
        if (statRes != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            failureReason = $"Directory statx failed with errno {err}";
            return false;
        }

        uint type = statxBuf.stx_mode & S_IFMT;
        if (type == S_IFLNK)
        {
            failureReason = "Directory is a symbolic link";
            return false;
        }

        if (type != S_IFDIR)
        {
            failureReason = "Path is not a directory";
            return false;
        }

        if (statxBuf.stx_uid != expectedUid)
        {
            failureReason = $"Directory owner UID {statxBuf.stx_uid} does not match expected UID {expectedUid}";
            return false;
        }

        if ((statxBuf.stx_mode & S_IRWXG_O) != 0)
        {
            failureReason = $"Directory permissions are not private (mode 0{Convert.ToString(statxBuf.stx_mode & S_PERM_MASK, 8).PadLeft(3, '0')})";
            return false;
        }

        failureReason = null;
        return true;
    }

    #region Native Interop

    private const int O_RDONLY   = 0x0000;
    private const int O_WRONLY   = 0x0001;
    private const int O_RDWR     = 0x0002;
    private const int O_CREAT    = 0x0040;
    private const int O_NONBLOCK = 0x0800;
    private const int O_NOFOLLOW = 0x00020000;
    private const int O_CLOEXEC  = 0x00080000;

    private const int LOCK_SH = 0x0001;
    private const int LOCK_EX = 0x0002;
    private const int LOCK_NB = 0x0004;
    private const int LOCK_UN = 0x0008;

    private const int AT_FDCWD            = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x0100;
    private const int AT_EMPTY_PATH       = 0x1000;
    private const uint STATX_BASIC_STATS  = 0x07FF;

    private const uint S_IFMT   = 0xF000;
    private const uint S_IFSOCK = 0xC000;
    private const uint S_IFLNK  = 0xA000;
    private const uint S_IFREG  = 0x8000;
    private const uint S_IFBLK  = 0x6000;
    private const uint S_IFDIR  = 0x4000;
    private const uint S_IFCHR  = 0x2000;
    private const uint S_IFIFO  = 0x1000;

    private const uint S_IRWXU = 0x01C0; // user rwx (0700)
    private const uint S_IRUSR = 0x0100; // user read (0400)
    private const uint S_IWUSR = 0x0080; // user write (0200)
    private const uint S_IXUSR = 0x0040; // user execute (0100)

    private const uint S_IRWXG = 0x0038; // group rwx (0070)
    private const uint S_IRGRP = 0x0020; // group read (0040)
    private const uint S_IWGRP = 0x0010; // group write (0020)
    private const uint S_IXGRP = 0x0008; // group execute (0010)

    private const uint S_IRWXO = 0x0007; // other rwx (0007)
    private const uint S_IROTH = 0x0004; // other read (0004)
    private const uint S_IWOTH = 0x0002; // other write (0002)
    private const uint S_IXOTH = 0x0001; // other execute (0001)

    private const uint S_IRWXG_O = S_IRWXG | S_IRWXO; // 0x003F (octal 0077)
    private const uint S_PERM_MASK = 0x01FF;          // 0x01FF (octal 0777)
    private const uint MODE_0600 = S_IRUSR | S_IWUSR; // 0x0180
    private const uint MODE_0700 = S_IRWXU;           // 0x01C0

    private const int ENOENT      = 2;
    private const int EAGAIN      = 11;
    private const int EWOULDBLOCK = 11;
    private const int ELOOP       = 40;

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

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

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
