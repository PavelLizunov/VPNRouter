using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private Border? _aboutOverlay;

    private Border BuildAboutOverlay()
    {
        var overlay = new Border { IsVisible = false };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    private void OnMenuAboutClicked(object? sender, RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        if (_aboutOverlay is null) return;
        _aboutOverlay.Child = BuildAboutContent();
        _aboutOverlay.IsVisible = true;
    }

    private Control BuildAboutContent()
    {
        var logo = new Image
        {
            Source = LoadMascot(),
            Width = 66,
            Height = 66,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(logo, BitmapInterpolationMode.HighQuality);
        var logoTile = new Border
        {
            Width = 76,
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(GetRadius("RadiusLg")),
            ClipToBounds = true,
            Child = logo,
        };
        logoTile.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        var title = new TextBlock
        {
            Text = Localization.AboutTitle,
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
        };
        title.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var brand = new TextBlock
        {
            Text = Localization.AboutBrandName,
            FontSize = UiScale.Fs(18),
            FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        brand.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var tagline = new TextBlock
        {
            Text = Localization.AboutTagline,
            FontSize = UiScale.Fs(11),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        tagline.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var details = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
            Child = MakeAboutDetails(
                (Localization.AboutVersionLabel, $"v{VPNRouter.Core.AppVersion.Version}"),
                (Localization.AboutSingBoxLabel, ReadEmbeddedCoreVersion()),
                (Localization.AboutCreatorLabel, "NiniTux")),
        };
        details.BindToken(Border.BackgroundProperty, "SurfaceSunkenBrush");
        details.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");

        var repo = StyledSecondaryButton(Localization.AboutRepoLabel);
        repo.MinHeight = 44;
        repo.HorizontalAlignment = HorizontalAlignment.Stretch;
        repo.HorizontalContentAlignment = HorizontalAlignment.Center;
        repo.VerticalContentAlignment = VerticalAlignment.Center;
        repo.Click += OnMenuRepoClicked;
        var close = StyledSecondaryButton(Localization.AboutCloseBtn);
        close.MinHeight = 44;
        close.HorizontalAlignment = HorizontalAlignment.Stretch;
        close.HorizontalContentAlignment = HorizontalAlignment.Center;
        close.VerticalContentAlignment = VerticalAlignment.Center;
        close.BorderThickness = new Thickness(0);
        close.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        close.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        close.Click += (_, _) => { if (_aboutOverlay is not null) _aboutOverlay.IsVisible = false; };

        var column = new StackPanel
        {
            Spacing = 14,
            Children = { title, logoTile, brand, tagline, details, repo, close },
        };
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border { MaxWidth = 420, Margin = new Thickness(24, 22), Child = column },
        };
    }

    private static Control MakeAboutDetails(params (string Label, string Value)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, RowSpacing = 8 };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var labelText = new TextBlock { Text = rows[i].Label, FontSize = UiScale.Fs(10), TextWrapping = TextWrapping.Wrap };
            labelText.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
            var valueText = new TextBlock { Text = rows[i].Value, FontSize = UiScale.Fs(11), TextWrapping = TextWrapping.Wrap };
            valueText.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetRow(labelText, i);
            Grid.SetRow(valueText, i);
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(labelText);
            grid.Children.Add(valueText);
        }
        return grid;
    }

    private static string ReadEmbeddedCoreVersion()
    {
        try
        {
            var cls = Java.Lang.Class.ForName("com.ninitux.vpnrouter.LibboxRuntime");
            var method = cls.GetMethod("coreVersion");
            var version = method.Invoke(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.About", $"Embedded core version unavailable: {ex.GetType().Name}");
        }
        return Localization.AboutVersionUnknown;
    }
}
