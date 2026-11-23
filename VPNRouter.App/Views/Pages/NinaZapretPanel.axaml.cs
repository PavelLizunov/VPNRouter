using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.Views.Pages;

public partial class NinaZapretPanel : UserControl
{
    private MainWindowViewModel? _viewModel;
    private CancellationTokenSource? _request;
    private bool _attached;

    public NinaZapretPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(_attached ? DataContext as MainWindowViewModel : null);
        AttachedToVisualTree += async (_, _) =>
        {
            _attached = true;
            Bind(DataContext as MainWindowViewModel);
            var available = await NinaZapretClient.IsAvailableAsync();
            if (_attached) IsVisible = available;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Bind(null);
        };
        RefreshLabels();
    }

    private void Bind(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= ViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += ViewModelChanged;
        RefreshLabels();
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.IsRussian)
            or nameof(MainWindowViewModel.IsZapretMagicButtonEnabled)) RefreshLabels();
    }

    private void RefreshLabels()
    {
        TitleText.Text = Strings.NinaZapretTitle;
        TargetText.Text = Strings.NinaZapretHint;
        RepairButton.Content = Strings.NinaZapretRepair;
        StopButton.Content = Strings.NinaZapretStop;
        CancelButton.Content = Strings.AssistantCancel;
        StopButton.IsEnabled = _request == null && _viewModel != null;
        RepairButton.IsEnabled = StopButton.IsEnabled && _viewModel?.IsZapretMagicButtonEnabled == true;
    }

    private async void RepairClick(object? sender, RoutedEventArgs args) => await RunAsync(NinaZapretClient.Discord);
    private async void StopClick(object? sender, RoutedEventArgs args) => await RunAsync(NinaZapretClient.Stop);

    private void CancelClick(object? sender, RoutedEventArgs args)
    {
        CancelButton.IsEnabled = false;
        ResultText.Text = Strings.NinaZapretCancelling;
        _request?.Cancel();
    }

    private async Task RunAsync(string command)
    {
        if (_viewModel is not { } viewModel || _request != null) return;
        using var request = new CancellationTokenSource();
        _request = request;
        RefreshLabels();
        CancelButton.IsVisible = command == NinaZapretClient.Discord;
        CancelButton.IsEnabled = true;
        ResultText.Text = Strings.NinaZapretChecking;
        var progress = new Progress<string>(profile =>
        {
            if (ReferenceEquals(_request, request) && !request.IsCancellationRequested)
                ResultText.Text = Strings.NinaZapretTrying(profile);
        });
        try
        {
            ResultText.Text = await viewModel.RunNinaZapretAsync(command, progress, request.Token);
        }
        catch (OperationCanceledException)
        {
            ResultText.Text = Strings.NinaZapretCancelled;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
            or InvalidOperationException)
        {
            ResultText.Text = Strings.NinaZapretFailed;
        }
        finally
        {
            _request = null;
            CancelButton.IsVisible = false;
            RefreshLabels();
        }
    }
}
