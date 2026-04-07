using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class ServersPage : UserControl
{
    public ServersPage()
    {
        InitializeComponent();
    }

    private void CustomConfigList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedCustomConfig != null)
        {
            vm.SetActiveCustomConfigCommand.Execute(vm.SelectedCustomConfig);
        }
    }

    // Right click on a server item → open detail editor for THAT item.
    // ContextRequested fires for right-click universally and walks the visual tree.
    private void ServerList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Source is ILogical logical)
        {
            var item = logical.GetSelfAndLogicalAncestors()
                              .OfType<ListBoxItem>()
                              .FirstOrDefault();
            if (item?.DataContext is ServerViewModel server)
            {
                vm.DetailServer = server;
                e.Handled = true;
            }
        }
    }
}
