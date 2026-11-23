using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal sealed class AssistantWindowAutomation(Window window, Control assistant)
{
    internal const string AutomationIdPrefix = "AssistantAction.";
    private readonly Dictionary<int, Entry> _entries = [];
    private sealed record Entry(Control Control, Control? Parent, object? DataContext,
        object? Parameter, AssistantControl Description);

    internal IReadOnlyList<AssistantControl> Observe(int firstId = 1)
    {
        _entries.Clear();
        foreach (var control in AppAutomationDriver.ControlsIn(window))
        {
            if (!Available(control) || !TryDescribe(control, _entries.Count + firstId, out var description))
                continue;

            _entries.Add(description.Id, new Entry(control, control.GetVisualParent() as Control,
                control.DataContext, (control as Button)?.CommandParameter, description));
            if (description.Id == 32) break;
        }

        return _entries.Values.Select(entry => entry.Description).ToArray();
    }

    internal string? Execute(AssistantControl advertised, string? text, bool? toggle)
    {
        if (!_entries.Remove(advertised.Id, out var entry) || entry.Description != advertised
            || !Available(entry.Control) || !ReferenceEquals(entry.Parent, entry.Control.GetVisualParent())
            || !ReferenceEquals(entry.DataContext, entry.Control.DataContext)
            || !Equals(entry.Parameter, (entry.Control as Button)?.CommandParameter)
            || !TryDescribe(entry.Control, advertised.Id, out var current) || current != advertised)
            return null;

        _entries.Clear();
        var peer = ControlAutomationPeer.CreatePeerForElement(entry.Control);
        switch (advertised.Kind)
        {
            case "press" when text == null && toggle == null && peer.GetProvider<IInvokeProvider>() is { } press:
                press.Invoke();
                break;
            case "toggle" when text == null && toggle is { } desired
                               && peer.GetProvider<IToggleProvider>() is { } check:
                if (advertised.Active != desired) check.Toggle();
                break;
            case "select" when text == null && toggle == null
                               && peer.GetProvider<ISelectionItemProvider>() is { } selection:
                selection.Select();
                break;
            case "text" when toggle == null && text != null && ValidText(text)
                             && peer.GetProvider<IValueProvider>() is { IsReadOnly: false } input:
                input.SetValue(text);
                break;
            default:
                return null;
        }

        return advertised.Name;
    }

    private bool Available(Control control) =>
        control.IsEffectivelyVisible && control.IsEffectivelyEnabled
        && ReferenceEquals(TopLevel.GetTopLevel(control), window)
        && !ReferenceEquals(control, assistant)
        && !control.GetVisualAncestors().Any(parent => ReferenceEquals(parent, assistant));

    private static bool TryDescribe(Control control, int id, out AssistantControl description)
    {
        description = null!;
        var automationId = AutomationProperties.GetAutomationId(control);
        if (automationId == null || !automationId.StartsWith(AutomationIdPrefix, StringComparison.Ordinal))
            return false;

        string name = automationId[AutomationIdPrefix.Length..];
        if (name is not { Length: >= 1 and <= 72 }
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            return false;

        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        (string? Kind, bool? Active) operation = control switch
        {
            TextBox { IsReadOnly: false, PasswordChar: '\0' } when peer.GetProvider<IValueProvider>() != null
                => ("text", null),
            _ when peer.GetProvider<ISelectionItemProvider>() is { } select
                => ("select", select.IsSelected),
            _ when peer.GetProvider<IToggleProvider>() != null && control is Avalonia.Controls.Primitives.ToggleButton toggle
                => ("toggle", toggle.IsChecked),
            _ when peer.GetProvider<IInvokeProvider>() != null => ("press", null),
            _ => (null, null),
        };
        if (operation.Kind == null) return false;
        description = new AssistantControl(id, name, operation.Kind, operation.Active);
        return true;
    }

    private static bool ValidText(string text) =>
        Encoding.UTF8.GetByteCount(text) <= 384 && !text.Any(c => char.IsControl(c)
            || c is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069');
}
