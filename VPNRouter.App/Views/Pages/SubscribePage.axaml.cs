using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class SubscribePage : UserControl
{
    private readonly TextBlock _refreshBlockedReason;
    private MainWindowViewModel? _viewModel;
    private bool _refreshQueued;

    public SubscribePage()
    {
        AvaloniaXamlLoader.Load(this);
        _refreshBlockedReason = this.FindControl<TextBlock>("SubscribeRefreshBlockedReason")!;
        this.FindControl<SubscriptionServerPicker>("AdvancedPicker")!.LinkTransferred +=
            this.FindControl<SubscriptionLinkEditor>("AdvancedLink")!.SetDraft;
        AttachedToVisualTree += (_, _) => Bind(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => Bind(null);
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree()) Bind(DataContext as MainWindowViewModel);
        };
    }

    private void Bind(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_viewModel, vm)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnSourceChanged;
        _viewModel = vm;
        if (_viewModel != null) _viewModel.PropertyChanged += OnSourceChanged;
        RefreshBlockedState();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(MainWindowViewModel.SmpCanRefreshSubscriptions) or
            nameof(MainWindowViewModel.SmpSubscriptionsRefreshing) or nameof(MainWindowViewModel.IsRussian)) || _refreshQueued)
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshBlockedState();
        });
    }

    private void RefreshBlockedState()
    {
        var reason = _viewModel?.SmpRefreshBlockedReason ?? string.Empty;
        _refreshBlockedReason.Text = reason;
        _refreshBlockedReason.IsVisible = reason.Length > 0;
    }
}
