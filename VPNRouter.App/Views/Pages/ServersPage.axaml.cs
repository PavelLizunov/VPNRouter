using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class ServersPage : UserControl
{
    private MainWindowViewModel? _subscribedVm;

    public ServersPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedVm is not null)
            _subscribedVm.ActiveServerChanged -= OnActiveServerChanged;
        _subscribedVm = DataContext as MainWindowViewModel;
        if (_subscribedVm is not null)
            _subscribedVm.ActiveServerChanged += OnActiveServerChanged;
    }

    private void OnActiveServerChanged(ServerViewModel? active)
    {
        if (active == null) return;
        var list = this.FindControl<ListBox>("ServerList");
        if (list == null || !list.Items.Cast<object?>().Contains(active)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { list.ScrollIntoView(active); } catch { }
        });
    }

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
