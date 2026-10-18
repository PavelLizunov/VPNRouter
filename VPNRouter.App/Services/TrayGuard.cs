using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Controls;

namespace VPNRouter.App.Services;

public enum TrayCloseAction
{
    HideToTray,
    MinimizeToTaskbar
}

// Closing the main window only hides it, so on Windows the window must not be hidden while the tray icon is missing from the notification area: the app
// would keep running with nothing to click. Avalonia marks its icon as added even when Shell_NotifyIcon(NIM_ADD) fails (a shell that is not ready yet at
// autostart) and re-adds it only on a TaskbarCreated broadcast, so the guard asks the shell itself. It finds Avalonia's message window (the owner of the
// icon) among this process's windows and sends NIM_MODIFY for the icon id: the shell answers FALSE when it does not have that icon.
public static class TrayGuard
{
    private const string MessageWindowClassPrefix = "AvaloniaMessageWindow ";
    private const int MaxIconId = 4;
    private const string HintMarkerFileName = "tray-hint-shown";

    private static IntPtr _messageWindow;
    private static int _iconId;

    public static string HintMarkerPath => Path.Combine(VPNRouter.Core.AppPaths.DataDir, HintMarkerFileName);

    // Unknown (null) keeps the old behavior: the window is hidden as before.
    public static TrayCloseAction DecideClose(bool? iconRegistered) =>
        iconRegistered == false ? TrayCloseAction.MinimizeToTaskbar : TrayCloseAction.HideToTray;

    public static bool ShouldShowHint(bool? iconRegistered, bool hintShownBefore) =>
        iconRegistered == true && !hintShownBefore;

    public static bool HintShownBefore(string? path = null)
    {
        try { return File.Exists(path ?? HintMarkerPath); }
        catch { return true; }
    }

    public static void MarkHintShown(string? path = null)
    {
        try { File.WriteAllText(path ?? HintMarkerPath, DateTime.UtcNow.ToString("o")); }
        catch { }
    }

    // True when the shell has the icon, false when it does not even after a re-add, null when this cannot be checked (not Windows, no message window).
    public static bool? EnsureIconRegistered(TrayIcon trayIcon, Serilog.ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var before = IsIconRegistered(trayIcon.ToolTipText);
        if (before != false) return before;

        // A hidden-then-shown icon makes Avalonia send NIM_DELETE and a fresh NIM_ADD for the same id.
        trayIcon.IsVisible = false;
        trayIcon.IsVisible = true;
        var after = IsIconRegistered(trayIcon.ToolTipText);
        logger.Warning("[Tray] icon was missing from the notification area; re-added: {Result}", after?.ToString() ?? "unknown");
        return after;
    }

    public static bool ShowHint(string title, string text)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return ShowBalloonWindows(title, text);
    }

    private static bool? IsIconRegistered(string? toolTip)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return IsIconRegisteredWindows(toolTip);
    }

    [SupportedOSPlatform("windows")]
    private static bool? IsIconRegisteredWindows(string? toolTip)
    {
        try
        {
            var hwnd = FindMessageWindow();
            if (hwnd == IntPtr.Zero) return null;

            // The id Avalonia gave our icon (its counter starts at 1); the first one the shell knows is ours.
            for (var id = _iconId > 0 ? _iconId : 1; id <= MaxIconId; id++)
            {
                var data = NewData(hwnd, id);
                // The same tooltip again: a valid modify that changes nothing.
                data.uFlags = NIF_TIP;
                data.szTip = toolTip ?? "";
                if (Shell_NotifyIconW(NIM_MODIFY, ref data))
                {
                    _iconId = id;
                    return true;
                }
                if (_iconId > 0) break;
            }
            return false;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ShowBalloonWindows(string title, string text)
    {
        try
        {
            var hwnd = FindMessageWindow();
            if (hwnd == IntPtr.Zero || _iconId <= 0) return false;

            var data = NewData(hwnd, _iconId);
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = text;
            data.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
            return Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            return false;
        }
    }

    private static NOTIFYICONDATAW NewData(IntPtr hwnd, int id) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = hwnd,
        uID = id,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    [SupportedOSPlatform("windows")]
    private static IntPtr FindMessageWindow()
    {
        if (_messageWindow != IntPtr.Zero && IsWindow(_messageWindow)) return _messageWindow;

        var ownPid = (uint)Environment.ProcessId;
        var found = IntPtr.Zero;
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != ownPid) return true;
            className.Clear();
            if (GetClassNameW(hwnd, className, className.Capacity) > 0
                && className.ToString().StartsWith(MessageWindowClassPrefix, StringComparison.Ordinal))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        _messageWindow = found;
        return found;
    }

    private const int NIM_MODIFY = 0x1;
    private const int NIF_TIP = 0x4;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;
    private const int NIIF_RESPECT_QUIET_TIME = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
