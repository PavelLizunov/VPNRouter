using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VPNRouter.App.Controls;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DeferredPageMemoryTests
{
    private sealed class TestDeferredContent : UserControl
    {
    }

    [AvaloniaFact]
    public void UnloadIfInactive_ReleasesInactiveContent_KeepsActiveContent_AndReinstantiatesOnReactivation()
    {
        var activePage = new DeferredPage
        {
            PageType = typeof(TestDeferredContent),
            IsActive = true
        };
        var inactivePage = new DeferredPage
        {
            PageType = typeof(TestDeferredContent),
            IsActive = true
        };

        var root = new Grid();
        root.Children.Add(activePage);
        root.Children.Add(inactivePage);

        var window = new Window { Width = 400, Height = 300, Content = root };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var firstActive = Assert.IsType<TestDeferredContent>(activePage.Content);
            var firstInactive = Assert.IsType<TestDeferredContent>(inactivePage.Content);

            inactivePage.IsActive = false;
            Assert.False(inactivePage.IsVisible);
            Assert.Same(firstInactive, inactivePage.Content);

            activePage.UnloadIfInactive();
            inactivePage.UnloadIfInactive();

            Assert.Same(firstActive, activePage.Content);
            Assert.Null(inactivePage.Content);

            inactivePage.IsActive = true;
            Assert.True(inactivePage.IsVisible);
            var reinstantiated = Assert.IsType<TestDeferredContent>(inactivePage.Content);
            Assert.NotSame(firstInactive, reinstantiated);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_ReleaseInactivePages_UnloadsOnlyInactivePagesAndAllowsRehydration()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSimpleMode = false;
        vm.SelectedTabIndex = 0;

        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SelectedTabIndex = 1;
            Dispatcher.UIThread.RunJobs();
            vm.SelectedTabIndex = 2;
            Dispatcher.UIThread.RunJobs();

            var pages = window.GetLogicalDescendants().OfType<DeferredPage>().ToList();
            var serversPage = Assert.Single(pages, p => p.PageType == typeof(ServersPage));
            var subscribePage = Assert.Single(pages, p => p.PageType == typeof(SubscribePage));
            var networkPage = Assert.Single(pages, p => p.PageType == typeof(NetworkPage));

            var originalServers = Assert.IsType<ServersPage>(serversPage.Content);
            Assert.IsType<SubscribePage>(subscribePage.Content);
            var activeNetwork = Assert.IsType<NetworkPage>(networkPage.Content);

            var released = window.ReleaseInactivePages();
            Assert.True(released >= 2, $"Expected at least 2 inactive pages released, got {released}");
            Assert.Null(serversPage.Content);
            Assert.Null(subscribePage.Content);
            Assert.Same(activeNetwork, networkPage.Content);

            Assert.Equal(0, window.ReleaseInactivePages());

            vm.SelectedTabIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var rehydratedServers = Assert.IsType<ServersPage>(serversPage.Content);
            Assert.NotSame(originalServers, rehydratedServers);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TrayMemoryTrimmer_CancelsPendingTrimOnRestore_RunsWhenHidden_AndStopsOnClose()
    {
        var window = new MainWindow();
        var restoredCalls = 0;
        var trimCalls = 0;

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var trimmer = new TrayMemoryTrimmer(
                window,
                onRestored: () => restoredCalls++,
                trimDelay: TimeSpan.FromSeconds(15),
                customTrimActions: () => trimCalls++);

            Assert.False(trimmer.HasPendingTrim);

            window.Hide();
            Assert.True(trimmer.HasPendingTrim);

            window.Show();
            Assert.False(trimmer.HasPendingTrim);
            Assert.Equal(0, trimCalls);
            Assert.Equal(1, restoredCalls);

            // Guard: firing while visible is a no-op.
            trimmer.RunTrimIfStillHidden();
            Assert.Equal(0, trimCalls);

            // Minimizing schedules trim; executing while minimized trims; restoring refreshes runtime status.
            window.WindowState = WindowState.Minimized;
            Assert.True(trimmer.HasPendingTrim);

            trimmer.RunTrimIfStillHidden();
            Assert.False(trimmer.HasPendingTrim);
            Assert.Equal(1, trimCalls);

            window.WindowState = WindowState.Normal;
            Assert.Equal(2, restoredCalls);

            // Closing the window cancels any pending timer cleanly.
            window.Hide();
            Assert.True(trimmer.HasPendingTrim);

            window.Close();
            Assert.False(trimmer.HasPendingTrim);
        }
        finally
        {
            try { window.Close(); } catch { }
        }
    }
}
