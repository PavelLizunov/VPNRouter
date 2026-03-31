using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VPNRouter.Mac.ViewModels;

namespace VPNRouter.Mac.Views;

public partial class MainWindow : Window
{
    public MainWindow()
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
}
