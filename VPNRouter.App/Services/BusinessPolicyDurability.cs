using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VPNRouter.App.Services;

internal static class BusinessPolicyDurability
{
    internal static void Replace(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
            if (!MoveFileEx(source, destination, 0x1 | 0x8)) throw Failure();
        }
        else File.Move(source, destination, overwrite: true);
    }

    internal static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return; // Replace uses write-through rename.
        var flags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000
            : OperatingSystem.IsMacOS() ? 0x100000 | 0x100 | 0x1000000
            : throw Failure(); // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        using var handle = OpenDirectory(directory, flags);
        if (handle.IsInvalid) throw Failure();
        while (Sync(handle) != 0)
            if (Marshal.GetLastPInvokeError() != 4) throw Failure(); // EINTR
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(SafeFileHandle descriptor);

    private static IOException Failure() => new("Business policy could not be durably stored.");
}
