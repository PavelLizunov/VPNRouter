using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace VPNRouter.App.Services;

public static class WindowForegroundHelper
{
    public static void BringToFront(Window? window)
    {
        if (window == null) return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();

        if (OperatingSystem.IsWindows())
            BringToFrontWindows(window);
    }

    [SupportedOSPlatform("windows")]
    private static void BringToFrontWindows(Window window)
    {
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);

            var foregroundHwnd = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
            var ourThread = GetCurrentThreadId();

            if (foregroundThread != ourThread)
                AttachThreadInput(ourThread, foregroundThread, true);

            try
            {
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
        }
    }

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
