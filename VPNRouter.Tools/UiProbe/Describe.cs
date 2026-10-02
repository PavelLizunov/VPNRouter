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

    public static string Path(Control c)
    {
        var parts = new List<string>();
        for (Visual? v = c; v != null && parts.Count < 7; v = v.GetVisualParent())
        {
            if (v is Window) break;
            var name = v.GetType().Name;
            if (v is Control ctl && !string.IsNullOrEmpty(ctl.Name)) name += "#" + ctl.Name;
            parts.Add(name);
        }
        parts.Reverse();
        var text = TextOf(c);
        return string.Join(">", parts) + (string.IsNullOrWhiteSpace(text) ? "" : $" \"{Trim(text!, 40)}\"");
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
