using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 0 stub Avalonia App.
/// Phase 3 will replace with shared App.axaml from VPNRouter.App.
/// Fully-qualified <c>Avalonia.Application</c> base — disambiguates from
/// <c>Android.App.Application</c> which is also visible here.
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Phase 0 stub. Phase 1 will instantiate MainView with
        // VPNRouter.Core's MainWindowViewModel.
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new TextBlock
            {
                Text = "VPNRouter v3.0-android Phase 0\n\nMore coming soon!",
                Padding = new Thickness(24),
                FontSize = 18,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
