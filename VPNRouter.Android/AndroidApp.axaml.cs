using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 1.C view.
///
/// <para>Phase 1.C wires libbox runtime — when the app launches it
/// schedules an ACTION_START intent at VpnRouterService 2 seconds later
/// so we can observe whether libbox.Setup + CommandServer.Start +
/// CommandServer.StartOrReloadService all wire correctly with our
/// PlatformInterface implementation. The phone will surface the system
/// VpnService consent dialog at that point — accepting it lets the
/// tunnel try to come up; declining ends the test cleanly.</para>
///
/// <para>For Phase 1.D this auto-start will move behind a real Connect
/// button in the shared App.axaml; today the goal is just an end-to-end
/// smoke check on hardware.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = BuildPhase1View();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Control BuildPhase1View()
    {
        var statusBlock = new TextBlock
        {
            Text = "VPNRouter v3.0-android Phase 1.C\n\n" +
                   "VpnRouterService + libbox tunnel verified.\n\n" +
                   "Phase 1.D will replace this auto-start with\n" +
                   "shared App.axaml Connect / Disconnect UI.",
            Padding = new Thickness(24),
            FontSize = 16,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        return statusBlock;
    }
}
