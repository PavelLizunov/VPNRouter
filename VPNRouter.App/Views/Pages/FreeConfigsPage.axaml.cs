using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
}
