using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class FreeConfigsPage : UserControl
{
    public FreeConfigsPage()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Double-click on a row triggers Connect immediately (native app convention).
    /// </summary>
    private void ConfigsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel mainVm)
        {
            var fcVm = mainVm.FreeConfigsVm;
            if (fcVm.SelectedItem != null && !fcVm.IsBusy)
            {
                if (fcVm.ApplySelectedCommand.CanExecute(null))
                    fcVm.ApplySelectedCommand.Execute(null);
            }
        }
    }
}
