using Avalonia.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class BusinessWindow : Window
{
    public BusinessWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // The Business preset is loaded before its view model is attached;
            // no subscription/editor can mutate this list from the Business UI.
            var vm = DataContext as MainWindowViewModel;
            BusinessServerSelector.ItemsSource = vm?.GetBusinessServerChoices();
            BusinessServerSelector.SetCurrentValue(ComboBox.SelectedItemProperty, vm?.SelectedServer);
        };
        Opened += (_, _) => LaunchFailureCounter.MarkStable();
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }

    private void OnBusinessServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            e.AddedItems.Count == 1 && e.AddedItems[0] is ServerViewModel server)
            vm.SelectBusinessServer(server);
    }
}
