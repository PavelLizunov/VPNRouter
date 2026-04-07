using System.Runtime.InteropServices;

namespace VPNRouter.GUI;

/// <summary>
/// ListView subclass that fully respects BackColor on dark themes.
///
/// Stock WinForms ListView in Details + OwnerDraw mode only paints item rows
/// via DrawSubItem. The empty area below items, the area to the right of the
/// last column header, and the column header strip beyond the last column are
/// painted by Windows native theme using SystemColors.Window — bright white,
/// regardless of BackColor. This produces visible white rectangles on dark
/// themes.
///
/// Fix:
/// 1. Enable double buffering to eliminate flicker
/// 2. Override OnPaint to fill the empty body area below items with BackColor
/// 3. Override WndProc to suppress NM_CUSTOMDRAW WM_ERASEBKGND with our color
/// </summary>
internal sealed class ThemedListView : ListView
{
    public ThemedListView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // Fill the area below the last item with our BackColor.
        // OwnerDraw subitems handle the row areas, but anything below the last
        // row remains the system white. Paint over it.
        int top = 0;
        if (Items.Count > 0)
        {
            var last = Items[Items.Count - 1];
            top = last.Bounds.Bottom;
        }
        else if (View == View.Details && Columns.Count > 0)
        {
            // Header height
            top = Font.Height + 6;
        }

        if (top < ClientSize.Height)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, 0, top, ClientSize.Width, ClientSize.Height - top);
        }

        // Fill the strip to the right of the last column in the body
        if (View == View.Details && Items.Count > 0 && Columns.Count > 0)
        {
            int totalColWidth = 0;
            for (int i = 0; i < Columns.Count; i++) totalColWidth += Columns[i].Width;
            if (totalColWidth < ClientSize.Width)
            {
                using var brush = new SolidBrush(BackColor);
                int headerHeight = Font.Height + 6;
                e.Graphics.FillRectangle(brush, totalColWidth, headerHeight,
                    ClientSize.Width - totalColWidth, top - headerHeight);
            }
        }
    }

    // ── WndProc: paint header right-of-last-column strip ─────────────────────

    private const int WM_PAINT = 0x000F;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_PAINT && View == View.Details && Columns.Count > 0)
        {
            // After base painted the column headers, paint over the white strip
            // to the right of the last column header
            using var g = CreateGraphics();
            int totalColWidth = 0;
            for (int i = 0; i < Columns.Count; i++) totalColWidth += Columns[i].Width;
            if (totalColWidth < ClientSize.Width)
            {
                int headerHeight = Font.Height + 6;
                using var brush = new SolidBrush(BackColor);
                g.FillRectangle(brush, totalColWidth, 0,
                    ClientSize.Width - totalColWidth, headerHeight);
            }
        }
    }
}
