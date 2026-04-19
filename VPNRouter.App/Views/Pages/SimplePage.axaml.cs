using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.App.Views.Pages;

public partial class SimplePage : UserControl
{
    public SimplePage()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
