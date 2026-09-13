using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class ApplicationsPage : UserControl
{
    public ApplicationsPage()
    {
        InitializeComponent();
    }

    private async void OnBrowseExeClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables (*.exe, *.app)")
                {
                    Patterns = new[] { "*.exe", "*.app" }
                },
                new FilePickerFileType("All Files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files.Count > 0)
        {
            var localPath = files[0].Path.LocalPath;
            var procName = Path.GetFileName(localPath);
            if (!string.IsNullOrWhiteSpace(procName))
            {
                var input = this.FindControl<TextBox>("CustomAppInput");
                if (input != null) input.Text = procName;

                if (DataContext is MainWindowViewModel vm)
                {
                    vm.AddCustomAppCommand.Execute(procName);
                }
            }
        }
    }

    private void OnSelectRunningProcessClicked(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindControl<Button>("RunningProcessesButton");
        var flyout = btn?.Flyout as MenuFlyout;
        if (flyout == null) return;

        var items = flyout.Items;
        items.Clear();

        var procs = Process.GetProcesses();
        var procNames = procs
            .Select(p =>
            {
                try
                {
                    var name = p.ProcessName;
                    if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name += ".exe";
                    return name;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    p.Dispose();
                }
            })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        foreach (var name in procNames)
        {
            var menuItem = new MenuItem { Header = name };
            menuItem.Click += (_, _) =>
            {
                var input = this.FindControl<TextBox>("CustomAppInput");
                if (input != null) input.Text = name;

                if (DataContext is MainWindowViewModel vm)
                {
                    vm.AddCustomAppCommand.Execute(name);
                }
            };
            items.Add(menuItem);
        }
    }
}
