using Avalonia;
using Avalonia.Headless;
using VPNRouter.Tests;
using VPNRouterApp = VPNRouter.App.App;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace VPNRouter.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VPNRouterApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
}
