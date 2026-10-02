using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;

namespace VPNRouter.Tools.UiProbe;

// Text outline of what is on screen: the meaningful controls with their text, window-relative bounds and state.
public static class UiTree
{
    public static string Dump(Window window, int maxLines = 600)
    {
        var sb = new StringBuilder();
        var lines = 0;

        void Walk(Visual v, int depth)
        {
            if (lines >= maxLines) return;
            var next = depth;
            if (v is Control c)
            {
                if (!c.IsEffectivelyVisible) return;
                var text = Describe.TextOf(c);
                var keep = !string.IsNullOrEmpty(c.Name) || !string.IsNullOrWhiteSpace(text) ||
                           Describe.IsInteractive(c) || c is Pictogram;
                if (keep)
                {
                    sb.Append(new string(' ', Math.Min(depth, 24) * 2)).Append(c.GetType().Name);
                    if (!string.IsNullOrEmpty(c.Name)) sb.Append('#').Append(c.Name);
                    if (c is Pictogram p) sb.Append(" icon=").Append(p.Id);
                    if (!string.IsNullOrWhiteSpace(text)) sb.Append(" \"").Append(Describe.Trim(text!, 60)).Append('"');
                    var origin = c.TranslatePoint(new Point(0, 0), window);
                    if (origin.HasValue)
                        sb.Append($" [{origin.Value.X:0},{origin.Value.Y:0} {c.Bounds.Width:0}x{c.Bounds.Height:0}]");
                    if (!c.IsEffectivelyEnabled) sb.Append(" disabled");
                    if (c is ToggleButton { IsChecked: true }) sb.Append(" checked");
                    if (c is TabItem { IsSelected: true } or ListBoxItem { IsSelected: true }) sb.Append(" selected");
                    var cmd = Describe.CommandName(c);
                    if (cmd != null) sb.Append(" cmd=").Append(cmd);
                    if (Describe.IsInteractive(c)) sb.Append(" *");
                    sb.AppendLine();
                    lines++;
                    next = depth + 1;
                }
            }
            foreach (var child in v.GetVisualChildren()) Walk(child, next);
        }

        Walk(window, 0);
        if (lines >= maxLines) sb.AppendLine($"... truncated at {maxLines} lines");
        return sb.ToString();
    }
}
