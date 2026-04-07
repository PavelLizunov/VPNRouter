using System.Runtime.InteropServices;

namespace VPNRouter.GUI;

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
        // Double buffering eliminates flicker when the parent paints over
        // the tab strip via the Paint event. ResizeRedraw forces full repaint
        // on resize so the background fill always covers the entire area.
        SetStyle(ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
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
