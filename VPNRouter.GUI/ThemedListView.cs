using System.Runtime.InteropServices;

namespace VPNRouter.GUI;

/// <summary>
/// ListView subclass that fully respects BackColor on dark themes.
///
/// Stock WinForms ListView in Details + OwnerDraw mode only paints item rows
/// via DrawSubItem. Three areas remain painted by Windows native theme using
/// SystemColors.Window (bright white) regardless of BackColor:
/// 1. Empty area below the last item
/// 2. Strip to the right of the last column header
/// 3. Strip in the body to the right of the last column
///
/// Fix:
/// - Double buffering eliminates flicker
/// - Suppress WM_ERASEBKGND so the system can't flash white
/// - Override OnPaint to fill all three problem areas with BackColor after
///   base painting completes
/// - The list's Resize handler should call AutoSizeColumns to size the last
///   column to fill remaining width (handled in LayoutHelper)
/// </summary>
internal sealed class ThemedListView : ListView
{
    public ThemedListView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    private const int WM_ERASEBKGND = 0x0014;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintEmptyAreas(e.Graphics);
    }

    private void PaintEmptyAreas(Graphics g)
    {
        if (View != View.Details) return;

        using var brush = new SolidBrush(BackColor);

        // 1. Compute header height
        int headerHeight = Columns.Count > 0 ? Font.Height + 6 : 0;

        // 2. Compute total column width
        int totalColWidth = 0;
        for (int i = 0; i < Columns.Count; i++) totalColWidth += Columns[i].Width;

        // 3. Compute bottom of last item
        int itemsBottom = headerHeight;
        if (Items.Count > 0)
        {
            var last = Items[Items.Count - 1];
            itemsBottom = Math.Max(itemsBottom, last.Bounds.Bottom);
        }

        // 4. Fill area below last item (across full width)
        if (itemsBottom < ClientSize.Height)
        {
            g.FillRectangle(brush, 0, itemsBottom,
                ClientSize.Width, ClientSize.Height - itemsBottom);
        }

        // 5. Fill strip to right of last column header
        if (totalColWidth < ClientSize.Width && headerHeight > 0)
        {
            g.FillRectangle(brush, totalColWidth, 0,
                ClientSize.Width - totalColWidth, headerHeight);
        }

        // 6. Fill strip to right of last column in the body (between header and items bottom)
        if (totalColWidth < ClientSize.Width && itemsBottom > headerHeight)
        {
            g.FillRectangle(brush, totalColWidth, headerHeight,
                ClientSize.Width - totalColWidth, itemsBottom - headerHeight);
        }
    }
}
