using Avalonia;
using Avalonia.Media;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    /// <summary>
    /// The tonal role of the U4 button system: accent text on a light accent tint, for secondary actions that sit inside
    /// cards (the plain default grey button looked disabled), with a 44 dp minimum hit area.
    /// </summary>
    private static void StyleTonalButton(Avalonia.Controls.Button btn)
    {
        btn.MinHeight = 44;
        btn.Padding = new Thickness(14, 8);
        btn.FontWeight = FontWeight.SemiBold;
        btn.BorderThickness = new Thickness(0);
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
    }
}
