using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.Headless.Protocol;

internal static class UnixSocketSecurity
{
    internal const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string DefaultSocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathFullyQualified(runtime))
            throw new IOException("A private XDG_RUNTIME_DIR is required.");
        RequirePrivateDirectory(runtime);
        return Path.Combine(runtime, "vpnrouter-headless", "control.sock");
    }

    internal static void RequirePrivateDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(path) != PrivateDirectory)
            throw new IOException("Runtime directory must be private and must not be a symlink.");
    }

    internal static void CheckPeer(Socket socket)
    {
        uint size = 12;
        if (getsockopt(socket.Handle.ToInt32(), 1, 17, out var credentials, ref size) != 0 ||
            size != 12 || credentials.Uid != geteuid())
            throw new IOException("Socket peer has a different owner.");
    }

    internal static FileStream AcquireLock(string path)
    {
        // O_NOFOLLOW also rejects dangling symlinks, atomically at open.
        int fd = open(path, 2 | 0x40 | 0x20000 | 0x80000, 0x180);
        if (fd < 0) throw new IOException("Cannot open private session lock.");
        var stream = new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: true), FileAccess.ReadWrite);
        if (flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0)
        {
            stream.Dispose();
            throw new IOException("Another session service owns this directory.");
        }
        return stream;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public int Pid; public uint Uid; public uint Gid; }

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int fd, int level, int option, out Credentials value, ref uint length);
    [DllImport("libc")]
    private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
}
