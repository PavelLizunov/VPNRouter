using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 0 entry point.
///
/// <para>Inherits from <see cref="AvaloniaMainActivity{TApp}"/> so the
/// Avalonia framework spins up our XAML-driven UI inside this Activity's
/// lifecycle. The <c>[Activity]</c> attribute is what Xamarin.Android's
/// build pipeline uses to auto-generate the corresponding
/// <c>&lt;activity&gt;</c> entry inside <c>AndroidManifest.xml</c> — so we
/// don't have to duplicate the registration there.</para>
///
/// <para>For Phase 0 the bound App is a minimal stub
/// (<see cref="AndroidApp"/>) that doesn't actually load the desktop
/// XAML pages yet — that comes in Phase 3. Today this just exists to get
/// the APK to build successfully and prove the toolchain.</para>
/// </summary>
[Activity(
    Label = "VPNRouter",
    MainLauncher = true,
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges =
        ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.Keyboard |
        ConfigChanges.ScreenLayout |
        ConfigChanges.UiMode |
        ConfigChanges.FontScale |
        ConfigChanges.Locale |
        ConfigChanges.Navigation |
        ConfigChanges.Orientation |
        ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<AndroidApp>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
