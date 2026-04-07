namespace VPNRouter.GUI;

/// <summary>
/// Pure layout calculation helpers — extracted for testability.
/// No WinForms control dependencies in calculation methods.
/// </summary>
internal static class LayoutHelper
{
    /// <summary>
    /// Calculate right-to-left X positions for a row of links.
    /// Items are placed from right edge leftward with a gap between each.
    /// </summary>
    /// <param name="rightEdge">Right boundary X coordinate.</param>
    /// <param name="gap">Gap between items in pixels.</param>
    /// <param name="widths">Width of each item (in order: rightmost first).</param>
    /// <returns>X positions for each item.</returns>
    public static int[] CalculateRightToLeftPositions(int rightEdge, int gap, int[] widths)
    {
        var positions = new int[widths.Length];
        int x = rightEdge;
        for (int i = 0; i < widths.Length; i++)
        {
            x -= widths[i];
            positions[i] = Math.Max(0, x);
            x -= gap;
        }
        return positions;
    }

    /// <summary>
    /// Calculate proportional column widths for a ListView.
    /// </summary>
    public static int[] CalculateProportionalWidths(int totalWidth, int scrollBarWidth, int[] proportions)
    {
        int available = Math.Max(0, totalWidth - scrollBarWidth);
        int totalProportion = 0;
        foreach (var p in proportions) totalProportion += p;
        if (totalProportion == 0) totalProportion = 1;

        var widths = new int[proportions.Length];
        for (int i = 0; i < proportions.Length; i++)
            widths[i] = Math.Max(20, available * proportions[i] / totalProportion);
        return widths;
    }

    /// <summary>
    /// Calculate action panel button layout (Start/Stop + Apply).
    /// </summary>
    /// <returns>(startStopWidth, applyX, applyWidth) — applyX/applyWidth are 0 if not visible.</returns>
    public static (int startStopWidth, int applyX, int applyWidth) CalculateActionLayout(
        int panelWidth, int margin, int gap, int minApplyWidth, int applyTextWidth, bool applyVisible)
    {
        int available = panelWidth - margin * 2;
        if (!applyVisible || available <= 0)
            return (Math.Max(100, available), 0, 0);

        int applyWidth = Math.Max(minApplyWidth, applyTextWidth + 30);
        int startWidth = Math.Max(100, available - applyWidth - gap);
        int applyX = margin + startWidth + gap;
        return (startWidth, applyX, applyWidth);
    }

    /// <summary>
    /// Apply proportional column widths to a ListView.
    /// Call from ListView.Resize handler.
    /// </summary>
    public static void AutoSizeColumns(System.Windows.Forms.ListView lv, int[] proportions)
    {
        if (lv.Columns.Count == 0 || proportions.Length == 0) return;
        var widths = CalculateProportionalWidths(
            lv.ClientSize.Width, SystemInformation.VerticalScrollBarWidth, proportions);
        for (int i = 0; i < lv.Columns.Count && i < widths.Length; i++)
            lv.Columns[i].Width = widths[i];
    }
}
