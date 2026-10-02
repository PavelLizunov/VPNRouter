using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.App.Views.Pages;

public partial class SimplePage : UserControl
{
    public SimplePage()
    {
        InitializeComponent();
        // The connecting arc of the status emblem spins only when the system allows animations; with reduced motion
        // it stays a still accent arc and the state word carries the meaning.
        Classes.Set("calm", PrefersReducedMotion());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Windows: "Show animations in Windows" (SPI_GETCLIENTAREAANIMATION). Other systems expose no setting Avalonia can
    // read, so the animation stays on there.
    private static bool PrefersReducedMotion()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
        }
        catch
        {
            return false;
        }
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
}
