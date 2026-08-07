#nullable enable
using Avalonia.Controls;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
    }

    public SetupWizardWindow(SetupWizardViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += CloseFromViewModel;
        Closed += (_, _) => viewModel.CloseRequested -= CloseFromViewModel;
    }

    private void CloseFromViewModel() => Close();
}
