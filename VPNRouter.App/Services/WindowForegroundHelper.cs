using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace VPNRouter.App.Services;

/// <summary>
/// Reliably bring an Avalonia <see cref="Window"/> to the foreground on
/// Windows. v2.31.7-r2.
///
/// <para>Background — Windows enforces a focus-stealing prevention policy:
/// <c>SetForegroundWindow</c> from a process that does NOT currently own
/// the foreground silently fails (or only flashes the taskbar) per
/// HKEY_CURRENT_USER\Control Panel\Desktop\ForegroundLockTimeout. Avalonia's
/// <c>Window.Activate()</c> calls <c>SetForegroundWindow</c> directly, so
/// it inherits this limitation.</para>
///
/// <para>The standard workaround documented by Microsoft and used by every
/// «restore window» pattern (Notepad++, OBS, foobar2000, etc.) is to
/// temporarily attach our thread input to the current foreground thread
/// — that gives us legitimate "foreground caller" status — call
/// <c>SetForegroundWindow</c>, then detach. We also handle the minimised
/// case (<c>ShowWindow(SW_RESTORE)</c>) and topmost-flicker (<c>SetWindowPos</c>
/// HWND_TOPMOST then HWND_NOTOPMOST) to defeat the «taskbar flash but no
/// foreground» fallback Windows uses when even AttachThreadInput fails.</para>
///
/// <para>On Mac/Linux this falls back to Avalonia's <c>Show</c> +
/// <c>Activate</c> + WindowState reset, which works fine on those
/// platforms (no system-wide focus-stealing prevention).</para>
/// </summary>
public static class WindowForegroundHelper
{
    public static void BringToFront(Window? window)
    {
        if (window == null) return;

        // Avalonia-level steps work cross-platform. Run them first so we
        // recover from minimised state regardless of OS.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();

        if (OperatingSystem.IsWindows())
            BringToFrontWindows(window);
        // Mac: Show+Activate already calls [NSApp activateIgnoringOtherApps:YES]
        // via Avalonia.Native, no additional steps needed.
        // Linux: Avalonia.X11 handles this through _NET_ACTIVE_WINDOW which
        // is the supported mechanism.
    }

    [SupportedOSPlatform("windows")]
    private static void BringToFrontWindows(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            // If minimised at the Win32 level (Avalonia state may have
            // already changed but Windows' show-state lags), restore it.
            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);

            var foregroundHwnd = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
            var ourThread = GetCurrentThreadId();

            // Attach our input queue to the current foreground thread's
            // input queue. While attached, SetForegroundWindow is allowed.
            if (foregroundThread != ourThread)
                AttachThreadInput(ourThread, foregroundThread, true);

            try
            {
                // Topmost-flicker: marks the window topmost briefly so it
                // jumps above everything regardless of z-order, then drops
                // back to normal so it doesn't stay always-on-top.
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(handle, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                SetForegroundWindow(handle);
            }
            finally
            {
                if (foregroundThread != ourThread)
                    AttachThreadInput(ourThread, foregroundThread, false);
            }
        }
        catch
        {
            // P/Invoke errors aren't worth crashing a UI handler over. If
            // we couldn't reach foreground, the user can still click the
            // tray icon — that path doesn't depend on this helper.
        }
    }

    // ─── Win32 P/Invoke ──────────────────────────────────────────────────────

    private const int SW_RESTORE = 9;

    private static readonly IntPtr HWND_TOPMOST   = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
