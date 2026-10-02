using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class ApplicationsPage : UserControl
{
    private static readonly HashSet<string> ExcludedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "Memory Compression",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass"
    };

    private MainWindowViewModel? _subscribedVm;

    public ApplicationsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateLocalizedStrings();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _subscribedVm = DataContext as MainWindowViewModel;
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged += OnVmPropertyChanged;
        }

        UpdateLocalizedStrings();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRussian) ||
            e.PropertyName == nameof(MainWindowViewModel.L_AddCustomAppBtn) ||
            e.PropertyName == nameof(MainWindowViewModel.L_AddAppHint))
        {
            UpdateLocalizedStrings();
        }
    }

    private void UpdateLocalizedStrings()
    {
        SetButtonLabel("BrowseExeButton", "BrowseExeLabel", Strings.BrowseExe, Strings.BrowseExeTooltip);
        SetButtonLabel("RunningProcessesButton", "RunningProcessesLabel", Strings.RunningProcesses, Strings.RunningProcessesTooltip);
    }

    private void SetButtonLabel(string buttonName, string labelName, string text, string tip)
    {
        var button = this.FindControl<Button>(buttonName);
        var label = this.FindControl<TextBlock>(labelName);
        if (label != null) label.Text = text;
        if (button == null) return;
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, text);
    }

    private async void OnBrowseExeClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SelectExecutableDialogTitle,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Strings.ExecutableFileFilter)
                {
                    Patterns = new[] { "*.exe", "*.app" }
                },
                new FilePickerFileType(Strings.AllFilesFilter)
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files.Count > 0)
        {
            var localPath = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(localPath)) return;

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

    private async void OnSelectRunningProcessClicked(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button ?? this.FindControl<Button>("RunningProcessesButton");
        var flyout = btn?.Flyout as MenuFlyout;
        if (btn == null || flyout == null) return;

        var items = flyout.Items;
        items.Clear();
        items.Add(new MenuItem { Header = Strings.LoadingProcesses, IsEnabled = false });

        var procNames = await Task.Run(() =>
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcesses();
            }
            catch
            {
                return new List<string>();
            }

            return procs
                .Select(p =>
                {
                    try
                    {
                        var rawName = p.ProcessName;
                        if (string.IsNullOrWhiteSpace(rawName) || ExcludedSystemProcesses.Contains(rawName))
                            return null;

                        var name = rawName;
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
                .OfType<string>()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .ToList();
        });

        items.Clear();
        if (procNames.Count == 0)
        {
            items.Add(new MenuItem { Header = Strings.NoRunningProcessesFound, IsEnabled = false });
        }
        else
        {
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

        flyout.ShowAt(btn);
    }
}
