using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Controls;

public sealed class ServerPickerListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs e)
    {
        if (e is KeyEventArgs or PointerEventArgs
            { Properties.PointerUpdateKind: PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased })
            return SelectFromInput(() => base.UpdateSelectionFromEvent(container, e));
        return base.UpdateSelectionFromEvent(container, e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var previous = SelectedItem;
        SelectFromInput(() =>
        {
            base.OnKeyDown(e);
            return e.Handled && !ReferenceEquals(previous, SelectedItem);
        });
    }

    private bool SelectFromInput(Func<bool> select) => DataContext is MainWindowViewModel vm
        ? vm.SelectServerFromPicker(select, () => SelectedItem as ServerViewModel)
        : select();
}
