using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using VPNRouter.App.Views;

namespace VPNRouter.Tests;

public class HeadlessGuiTests
{
    [AvaloniaFact]
    public void MainWindow_Opens_WithoutExceptions()
    {
        var window = new MainWindow();
        Assert.NotNull(window);
        Assert.IsType<MainWindow>(window);
    }

    [AvaloniaFact]
    public void MainWindow_Shows_WithNonZeroBounds()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.True(window.Bounds.Width > 0,
                $"MainWindow.Bounds.Width should be positive after Show(), got {window.Bounds.Width}");
            Assert.True(window.Bounds.Height > 0,
                $"MainWindow.Bounds.Height should be positive after Show(), got {window.Bounds.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AboutWindow_Opens_WithoutExceptions()
    {
        var window = new AboutWindow();
        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void SetupWizardWindow_Shows_WithBindingsResolved()
    {
        var viewModel = new VPNRouter.App.ViewModels.SetupWizardViewModel(
            VPNRouter.Core.Models.TunSettings.DefaultMtu,
            true,
            (_, _) => { },
            () => [],
            () => System.Threading.Tasks.Task.CompletedTask);
        var window = new SetupWizardWindow(viewModel);
        try
        {
            window.Show();
            Assert.True(window.Bounds.Width > 0);
            Assert.True(window.Bounds.Height > 0);
            Assert.Equal(viewModel.TitleText, window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(520, "mainwindow-520")]
    [InlineData(440, "mainwindow-440")]
    [InlineData(360, "mainwindow-360")]
    [InlineData(300, "mainwindow-300")]
    public void MainWindow_FullApp_Narrow(int width, string name)
    {
        var window = new MainWindow { Width = width, Height = 700 };
        window.DataContext = new VPNRouter.App.ViewModels.MainWindowViewModel();
        ScreenshotHelper.Capture(window, name);
    }

    [AvaloniaFact]
    public void Button_Click_InputRouting_Works()
    {
        var clickCount = 0;
        var button = new Button { Content = "Test", Name = "TestBtn" };
        button.Click += (_, _) => clickCount++;

        var window = new Window
        {
            Width = 200,
            Height = 100,
            Content = button
        };

        try
        {
            window.Show();

            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, clickCount);
        }
        finally
        {
            window.Close();
        }
    }
}
