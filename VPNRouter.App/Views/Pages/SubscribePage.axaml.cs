using System.Linq;
using Avalonia.Controls;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class SubscribePage : UserControl
{
    private MainWindowViewModel? _subscribedVm;

    public SubscribePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // v2.40.0-r5 (audit P1): unsubscribe the OLD VM before wiring the new one
        // (DataContextChanged can fire repeatedly) — else each change leaks a
        // handler, keeps the old VM alive, and double-fires ScrollIntoView.
        if (_subscribedVm is not null)
            _subscribedVm.ActiveServerChanged -= OnActiveServerChanged;
        _subscribedVm = DataContext as MainWindowViewModel;
        if (_subscribedVm is not null)
            _subscribedVm.ActiveServerChanged += OnActiveServerChanged;
    }

    private void OnActiveServerChanged(ServerViewModel? active)
    {
        if (active == null) return;
        var list = this.FindControl<ListBox>("SubList");
        if (list == null || !list.Items.Cast<object?>().Contains(active)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { list.ScrollIntoView(active); } catch { }
        });
    }
}
