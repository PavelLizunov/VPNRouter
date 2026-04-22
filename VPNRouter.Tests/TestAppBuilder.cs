using Avalonia;
using Avalonia.Headless;
using VPNRouter.Tests;
// Alias the App class to avoid the namespace/type collision with VPNRouter.App.
using VPNRouterApp = VPNRouter.App.App;

// Register the headless Avalonia application for the test assembly. xUnit picks
// this up via the assembly attribute; every [AvaloniaFact]/[AvaloniaTheory] in
// the suite runs on the dispatcher thread owned by this AppBuilder.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace VPNRouter.Tests;

/// <summary>
/// Wires up Avalonia in headless mode for xUnit tests. Reuses VPNRouter.App's
/// <see cref="Program.BuildAvaloniaApp"/> so tests see the same App composition
/// (styles, resources, ViewLocator) as a real desktop launch — just without a
/// window surface.
///
/// <para>Consumers:
/// <list type="bullet">
///   <item><c>[AvaloniaFact]</c> — standard xUnit fact that runs on the UI thread.</item>
///   <item><c>[AvaloniaTheory]</c> — parameterised variant.</item>
///   <item><c>HeadlessUnitTestSession</c> — manual session for ViewModel-only tests.</item>
/// </list>
/// </para>
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VPNRouterApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // No frame rendering / image capture needed for our current
                // smoke tests. Flip this to true if a test needs to assert
                // on visual output (screenshots, pixel diffs).
                UseHeadlessDrawing = true
            });
}
