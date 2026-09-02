#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.Core.Services;

internal enum UnixOwnedSignalResult
{
    Signaled,
    TargetGone,
    IdentityMismatch,
    IdentityUnavailable,
    Unsupported,
    AccessDenied,
    Failed
}

/// <summary>
/// Linux-only stable signaling for one already-proven process identity.
/// pidfd_open binds the kernel target before identity is re-read; the signal
/// is then sent through that same pidfd, never through a recyclable PID.
/// </summary>
internal static class UnixOwnedProcessSignal
{
    internal const string HelperFlag = "--vpnrouter-internal-signal-owned-v1";

    private const long SysPidFdOpen = 434;
    private const long SysPidFdSendSignal = 424;
    private const int Esrch = 3;
    private const int Eperm = 1;
    private const int Enosys = 38;

    internal static bool TryHandleHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], HelperFlag, StringComparison.Ordinal))
            return false;

        if (args.Length != 5
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var startedAtUtcTicks)
            || startedAtUtcTicks <= 0
            || string.IsNullOrWhiteSpace(args[3])
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var signal)
            || signal is not (9 or 15))
        {
            Console.Error.WriteLine("Invalid internal owned-process signal request.");
            exitCode = 64;
            return true;
        }

        var expected = new OwnedProcessIdentity(pid, startedAtUtcTicks, args[3]);
        var result = SignalLinux(expected, signal);
        exitCode = result switch
        {
            UnixOwnedSignalResult.Signaled => 0,
            UnixOwnedSignalResult.TargetGone => 0,
            UnixOwnedSignalResult.IdentityMismatch => 0,
            UnixOwnedSignalResult.Unsupported => 69,
            UnixOwnedSignalResult.AccessDenied => 77,
            UnixOwnedSignalResult.IdentityUnavailable => 74,
            _ => 1
        };

        if (exitCode != 0)
            Console.Error.WriteLine($"Owned-process signal refused: {result}.");
        return true;
    }

    internal static UnixOwnedSignalResult SignalLinux(OwnedProcessIdentity expected, int signal)
    {
        if (!OperatingSystem.IsLinux() || signal is not (9 or 15))
            return UnixOwnedSignalResult.Unsupported;

        var rawFd = PidFdOpen(SysPidFdOpen, expected.Pid, 0);
        if (rawFd < 0)
            return MapError(Marshal.GetLastPInvokeError());

        if (rawFd > int.MaxValue)
            return UnixOwnedSignalResult.Failed;
        using var pidFd = new SafePidFd((int)rawFd);

        OwnedProcessIdentity? current;
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            current = ProcessOwnership.TryReadOwnedSingBoxIdentity(process);
        }
        catch (ArgumentException)
        {
            return UnixOwnedSignalResult.TargetGone;
        }
        catch (InvalidOperationException)
        {
            return UnixOwnedSignalResult.TargetGone;
        }
        catch
        {
            return UnixOwnedSignalResult.IdentityUnavailable;
        }

        if (current is null)
            return UnixOwnedSignalResult.IdentityUnavailable;
        if (!ProcessOwnership.IsSameProcessIdentity(expected, current.Value))
            return UnixOwnedSignalResult.IdentityMismatch;

        var rc = PidFdSendSignal(SysPidFdSendSignal, pidFd, signal, IntPtr.Zero, 0);
        return rc == 0
            ? UnixOwnedSignalResult.Signaled
            : MapError(Marshal.GetLastPInvokeError());
    }

    private static UnixOwnedSignalResult MapError(int errno)
        => errno switch
        {
            Esrch => UnixOwnedSignalResult.TargetGone,
            Eperm => UnixOwnedSignalResult.AccessDenied,
            Enosys => UnixOwnedSignalResult.Unsupported,
            _ => UnixOwnedSignalResult.Failed
        };

    private sealed class SafePidFd : SafeHandleMinusOneIsInvalid
    {
        internal SafePidFd(int fd) : base(ownsHandle: true)
            => SetHandle((IntPtr)fd);

        protected override bool ReleaseHandle()
            => Close(handle.ToInt32()) == 0;
    }

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidFdOpen(long number, int pid, uint flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidFdSendSignal(
        long number,
        SafePidFd pidFd,
        int signal,
        IntPtr info,
        uint flags);
}
