using Avalonia.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

public partial class BusinessWindow : Window
{
    public BusinessWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LaunchFailureCounter.MarkStable();
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Dispose();
    }
}
