using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace VPNRouter.Tests;

/// <summary>
/// Renders an Avalonia control to PNG via the headless Skia backend.
///
/// <para>Used by per-page smoke tests (see <c>PageScreenshotTests</c>) to
/// drop a PNG snapshot of each page into <c>VPNRouter.Tests/screenshots/</c>
/// every time the suite runs. The folder is git-ignored (see <c>.gitignore</c>)
/// — screenshots are an inspection artefact for me and the reviewer, not
/// something we want in commits. If we later want visual-diff regression
/// testing we can pin a baseline PNG into the repo and compare pixel-for-
/// pixel here, but right now the goal is just "give the reviewer something
/// to look at per release without opening the GUI by hand".</para>
/// </summary>
public static class ScreenshotHelper
{
    /// <summary>
    /// Directory where captured PNGs land. Resolved relative to the test
    /// assembly so it works under <c>dotnet test</c> from any CWD and the
    /// path is predictable for humans opening the folder.
    /// </summary>
    public static string ScreenshotsDir { get; } = ResolveScreenshotsDir();

    private static string ResolveScreenshotsDir()
    {
        // bin/Debug/net8.0 → repo/VPNRouter.Tests/screenshots
        var asmDir = Path.GetDirectoryName(typeof(ScreenshotHelper).Assembly.Location)!;
        var repoTestDir = Path.GetFullPath(Path.Combine(asmDir, "..", "..", ".."));
        var path = Path.Combine(repoTestDir, "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Render <paramref name="window"/> at its current size and save as
    /// <c>{ScreenshotsDir}/{name}.png</c>. Requires the headless Skia
    /// backend (<c>UseHeadlessDrawing = false</c> in <c>TestAppBuilder</c>);
    /// throws if the frame came back null, which is our signal that the
    /// backend was misconfigured and the test should fail loudly rather
    /// than silently skip a regression-catching screenshot.
    /// </summary>
    public static string Capture(Window window, string name)
    {
        if (window == null) throw new ArgumentNullException(nameof(window));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required", nameof(name));

        // Ensure the window has laid out and rendered at least one frame.
        // Without this first pass, CaptureRenderedFrame can return an empty
        // bitmap because the templates haven't applied yet.
        window.Show();
        try
        {
            var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException(
                    "CaptureRenderedFrame returned null — verify TestAppBuilder uses .UseSkia() and UseHeadlessDrawing=false.");

            var path = Path.Combine(ScreenshotsDir, $"{name}.png");
            bitmap.Save(path);
            return path;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Host a standalone <see cref="UserControl"/> (like a page) inside a
    /// sized <see cref="Window"/> and screenshot that. Saves the caller from
    /// repeating the boilerplate of wrapping every page in a window just to
    /// get it rendered.
    /// </summary>
    public static string CapturePage(UserControl page, string name, int width = 1200, int height = 800)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = page
        };
        return Capture(window, name);
    }
}
