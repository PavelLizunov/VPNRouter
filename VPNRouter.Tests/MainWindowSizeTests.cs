using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Views;
using Xunit;

namespace VPNRouter.Tests;

// Squeezed far enough the server list used to collapse to nothing (tester screenshot 6); the window has a floor now and a way back to the default size.
public sealed class MainWindowSizeTests
{
    [AvaloniaFact]
    public void Window_HasAFloorThatKeepsTheListsReadable()
    {
        var window = new MainWindow();

        Assert.True(window.MinWidth >= 380);
        Assert.True(window.MinHeight >= 520);
    }

    [AvaloniaFact]
    public void ResetWindowSize_RestoresTheDefaultWidthAndAHeightWithinTheFloorAndTheDefault()
    {
        var window = new MainWindow { Width = 400, Height = 530 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.ResetWindowSize();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(520, window.Width);
        Assert.InRange(window.Height, window.MinHeight, MainWindow.PreferredHeight);
        window.Close();
    }
}
