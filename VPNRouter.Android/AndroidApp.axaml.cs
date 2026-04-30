using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 0 stub Avalonia App.
///
/// <para>Phase 1.A landed the Kotlin → C# port of VpnRouterService and
/// the Intent dispatch path in AndroidSingBoxRuntime, but the on-device
/// UI here remains the Phase 0 "More coming soon" greeting until libbox
/// is wired (Phase 1.B). Verification of Phase 1.A happens via
/// <c>adb shell dumpsys package com.ninitux.vpnrouter</c> showing the
/// VpnService registered with the BIND_VPN_SERVICE permission.</para>
///
/// <para>Phase 3 will replace this with shared App.axaml from
/// VPNRouter.App. Fully-qualified <c>Avalonia.Application</c> base —
/// disambiguates from <c>Android.App.Application</c> which is also
/// visible here via Mono.Android.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new TextBlock
            {
                Text = "VPNRouter v3.0-android Phase 1.A\n\n" +
                       "VpnRouterService registered.\n" +
                       "libbox tunnel coming in Phase 1.B.",
                Padding = new Thickness(24),
                FontSize = 18,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
