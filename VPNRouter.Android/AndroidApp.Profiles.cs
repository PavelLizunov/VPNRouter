using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private Border BuildProfilesOverlay()
    {
        _profilesOverlayTitle = new TextBlock
        {
            Text = Localization.ProfilesOverlayTitle,
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _profilesOverlayTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _profilesCloseBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.X, 22),
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _profilesCloseBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _profilesCloseBtn.Click += OnProfilesCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_profilesOverlayTitle, 0);
        Grid.SetColumn(_profilesCloseBtn, 1);
        titleBar.Children.Add(_profilesOverlayTitle);
        titleBar.Children.Add(_profilesCloseBtn);

        var titleBarBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };
        titleBarBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        titleBarBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        _profilesOverlayIntro = new TextBlock
        {
            Text = Localization.ProfilesIntro,
            FontSize = UiScale.Fs(11),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _profilesOverlayIntro.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _profilesList = new StackPanel
        {
            Spacing = 10,
        };

        var inner = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(16, 12, 16, 16),
            Children = { _profilesOverlayIntro, _profilesList },
        };

        var scroller = new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.BindToken(ScrollViewer.BackgroundProperty, "SurfaceAppBrush");

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(scroller);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }





    private void OnProfilesCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profilesOverlay is not null) _profilesOverlay.IsVisible = false;
    }


}
