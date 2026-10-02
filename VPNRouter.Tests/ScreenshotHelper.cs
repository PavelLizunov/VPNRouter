using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;

namespace VPNRouter.Tests;

public static class ScreenshotHelper
{
    public static string ScreenshotsDir { get; } = ResolveScreenshotsDir();

    private static string ResolveScreenshotsDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(ScreenshotHelper).Assembly.Location)!;
        var repoTestDir = Path.GetFullPath(Path.Combine(asmDir, "..", "..", ".."));
        var path = Path.Combine(repoTestDir, "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Capture(Window window, string name)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));

        window.Show();
        try
        {
            var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException(
                    "CaptureRenderedFrame returned null — verify TestAppBuilder uses .UseSkia() and UseHeadlessDrawing=false.");

            var path = Path.Combine(ScreenshotsDir, $"{name}.png");

            Exception? lastEx = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    bitmap.Save(path, PngBitmapEncoderOptions.Default);
                    return path;
                }
                catch (IOException ex)
                {
                    lastEx = ex;
                    Thread.Sleep(50 * (1 << attempt));
                }
            }
            throw lastEx!;
        }
        finally
        {
            window.Close();
        }
    }

    public static string CapturePage(UserControl page, string name, int width = 1200, int height = 800, bool appBackground = false)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = page
        };
        // The app window paints SurfaceApp behind its pages; design captures do the same so cards read as in the app.
        if (appBackground)
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("SurfaceAppBrush"));
        return Capture(window, name);
    }
}
