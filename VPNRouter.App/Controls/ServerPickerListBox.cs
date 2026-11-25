using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Controls;

public sealed class ServerPickerListBox : ListBox
{
    internal Action<ServerViewModel>? ConfirmSelection { get; set; }

    protected override Type StyleKeyOverride => typeof(ListBox);

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs e)
    {
        if (e.Source is Control source && (source is Button || source.FindAncestorOfType<Button>() != null))
            return false;

        if (e is KeyEventArgs or PointerEventArgs
            { Properties.PointerUpdateKind: PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased })
            return SelectFromInput(() => base.UpdateSelectionFromEvent(container, e));
        return base.UpdateSelectionFromEvent(container, e);
    }

    private bool SelectFromInput(Func<bool> select)
    {
        if (ConfirmSelection is not { } confirm)
            return DataContext is MainWindowViewModel vm
                ? vm.SelectServerFromPicker(select, () => SelectedItem as ServerViewModel)
                : select();

        var accepted = select();
        if (accepted && SelectedItem is ServerViewModel row)
            confirm(row);
        return accepted;
    }
}
