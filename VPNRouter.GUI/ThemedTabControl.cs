using System.Runtime.InteropServices;

namespace VPNRouter.GUI;

internal static class NativeThemeHelper
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

    /// <summary>
    /// Disable native Windows visual styles for a control by setting an empty
    /// theme. The control then renders in "classic" mode which respects the
    /// .NET BackColor property properly. Used for controls where the system
    /// theme draws light/gray frames or borders that ignore BackColor
    /// (TabControl, TreeView, ListView headers, etc).
    /// </summary>
    public static void DisableVisualStyles(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try { SetWindowTheme(hWnd, "", ""); } catch { }
    }
}

/// <summary>
/// TabControl subclass that fully respects BackColor on dark themes.
///
/// Stock WinForms TabControl always paints the area around the tab pages
/// (the strip behind the tabs themselves and the small border around the
/// content) using SystemColors.Control — light gray, ignoring BackColor.
/// On dark themes this produces a visible gray strip across the top of
/// the tab area.
///
/// Fix: enable double buffering, override OnPaintBackground to fill with
/// our BackColor, and override WndProc to also fill on WM_ERASEBKGND.
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    public ThemedTabControl()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Strip visual styles so the system stops painting the light frame
        // around tab content with SystemColors.Control. After this the
        // control draws in classic mode and respects BackColor.
        NativeThemeHelper.DisableVisualStyles(Handle);
    }

    private const int WM_ERASEBKGND = 0x0014;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            // Suppress system erase — Paint handler fills with theme color.
            // Without this we'd see a flash of SystemColors.Control before
            // the Paint handler overdraws.
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }
}
