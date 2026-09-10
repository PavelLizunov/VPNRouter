using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>Real pages with synthetic state; never captures the installed desktop.</summary>
[Collection(SafeModeStateCollection.Name)]
public class DesktopPictogramScreenshotTests
{
    [AvaloniaTheory]
    [InlineData(false, 360)]
    [InlineData(false, 520)]
    [InlineData(true, 360)]
    [InlineData(true, 520)]
    public void AcceptedHeroes_RenderInBothThemes(bool dark, int width)
    {
        var app = Avalonia.Application.Current!;
        var previous = app.RequestedThemeVariant;
        try
        {
            using var vm = new MainWindowViewModel(new InMemorySettingsStore());
            // The real VM applies its saved theme during construction.
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Assert.IsType<VPNRouter.App.App>(app);
            Assert.NotEmpty(app.Styles);
            Assert.Equal(app.RequestedThemeVariant, app.ActualThemeVariant);
            Assert.True(app.TryGetResource("AccentFgBrush", app.ActualThemeVariant, out var accent));
            Assert.Equal(Avalonia.Media.Color.Parse(dark ? "#FF67E8F9" : "#FF0369A1"),
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(accent).Color);
            var theme = dark ? "dark" : "light";
            UserControl[] pages = [new DpiBypassPage(), new TelegramPage()];
            foreach (var page in pages)
            {
                page.DataContext = vm;
                ScreenshotHelper.CapturePage(page,
                    $"pictograms-{page.GetType().Name}-{theme}-{width}",
                    width: width, height: 900);
            }
        }
        finally
        {
            app.RequestedThemeVariant = previous;
        }
    }
}
