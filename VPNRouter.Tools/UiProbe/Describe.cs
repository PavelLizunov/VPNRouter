using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace VPNRouter.Tools.UiProbe;

// Names and text of controls, shared by the lint, the tree dump and the sweeper.
public static class Describe
{
    public static string? TextOf(Control c)
    {
        switch (c)
        {
            case TextBlock tb:
                if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
                if (tb.Inlines is { Count: > 0 })
                    return string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text));
                return null;
            case ContentControl cc when cc.Content is string s:
                return s;
            case TextBox box:
                return box.Text;
            case HeaderedContentControl hc when hc.Header is string h:
                return h;
            default:
                return null;
        }
    }

    private static readonly HashSet<string> Chrome = new(StringComparer.Ordinal)
    {
        "ContentPresenter", "ScrollContentPresenter", "ItemsPresenter", "VirtualizingStackPanel", "StackPanel", "Grid", "Border",
        "Panel", "DockPanel", "WrapPanel", "ScrollViewer", "Decorator", "VisualLayerManager", "AdornerDecorator", "LayoutTransformControl",
        "TransitioningContentControl", "Canvas", "UniformGrid", "RelativePanel", "ContentControl",
    };

    // A readable name for a control: its own text, else the first text or icon inside it, else its automation name.
    public static string? Label(Control c)
    {
        var own = TextOf(c);
        if (!string.IsNullOrWhiteSpace(own)) return Trim(own!, 40);
        foreach (var d in c.GetVisualDescendants())
        {
            if (d is TextBlock tb && !string.IsNullOrWhiteSpace(TextOf(tb))) return Trim(TextOf(tb)!, 40);
        }
        foreach (var d in c.GetVisualDescendants())
        {
            if (d is VPNRouter.App.Controls.Pictogram { Id: { Length: > 0 } id }) return "icon:" + id;
        }
        var automation = Avalonia.Automation.AutomationProperties.GetName(c);
        return string.IsNullOrWhiteSpace(automation) ? null : Trim(automation, 40);
    }

    // Short stable path: the meaningful ancestors only (layout containers and template parts are left out), then the control and its label.
    public static string Path(Control c)
    {
        var parts = new List<string>();
        for (Visual? v = c.GetVisualParent(); v != null && parts.Count < 3; v = v.GetVisualParent())
        {
            if (v is Window) break;
            var type = v.GetType().Name;
            var name = (v as Control)?.Name;
            var isPart = name != null && name.StartsWith("PART_", StringComparison.Ordinal);
            if (Chrome.Contains(type) && string.IsNullOrEmpty(name)) continue;
            if (isPart) continue;
            parts.Add(string.IsNullOrEmpty(name) ? type : type + "#" + name);
        }
        parts.Reverse();
        var self = c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? string.Empty : "#" + c.Name);
        var label = Label(c);
        var text = label == null ? string.Empty : $" \"{label}\"";
        return (parts.Count == 0 ? string.Empty : string.Join(">", parts) + ">") + self + text;
    }

    public static string Trim(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "...";
    }

    // The view model property that holds the command this control runs, e.g. "ConnectCommand".
    public static string? CommandName(Control c)
    {
        ICommand? cmd = c switch
        {
            Button b => b.Command,
            MenuItem m => m.Command,
            _ => null,
        };
        if (cmd == null) return null;
        for (StyledElement? e = c; e != null; e = e.Parent)
        {
            var dc = e.DataContext;
            if (dc == null) continue;
            foreach (var p in dc.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(ICommand).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length != 0) continue;
                try
                {
                    if (ReferenceEquals(p.GetValue(dc), cmd)) return p.Name;
                }
                catch
                {
                    // a throwing getter is not the command we are looking for
                }
            }
        }
        return "(unnamed command)";
    }

    public static bool IsInteractive(Control c) =>
        c is Button or TabItem or ListBoxItem or ComboBox or MenuItem or TextBox or Slider or NumericUpDown or ToggleSwitch;
}
