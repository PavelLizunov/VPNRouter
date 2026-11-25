using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VPNRouter.App.Services;

namespace VPNRouter.App.Views.Pages;

public partial class SimplePage : UserControl
{
    public SimplePage()
    {
        InitializeComponent();
        // The connecting arc of the status emblem spins only when the system allows animations; with reduced motion
        // it stays a still accent arc and the state word carries the meaning.
        Classes.Set("calm", !UiMotion.Allowed());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
