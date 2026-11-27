#nullable enable

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VPNRouter.Core.Services;
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private Grid? _quickAddOverlay;
    private Border? _quickAddSheet;
    private TextBlock? _quickAddTitle;
    private Avalonia.Controls.Button? _quickAddQrButton;
    private TextBlock? _quickAddOrLabel;
    private TextBox? _quickAddInput;
    private TextBlock? _quickAddError;
    private Avalonia.Controls.Button? _quickAddPasteButton;
    private Avalonia.Controls.Button? _quickAddSaveButton;

    private static string QuickAddTitleText => Localization.Ru
        ? "Добавить подписку или сервер"
        : "Add subscription or server";

    private static string QuickAddOrLinkText => Localization.Ru
        ? "или вставить ссылку"
        : "or paste a link";

    private static string QuickAddPasteText => Localization.Ru
        ? "Вставить из буфера"
        : "Paste from clipboard";

    private static string QuickAddSubmitText => Localization.Ru
        ? "Добавить"
        : "Add";

    private static bool HasSavedSimpleConfig() =>
        !string.IsNullOrWhiteSpace(AndroidStorage.GetSubscriptionUrl()) ||
        !string.IsNullOrWhiteSpace(AndroidStorage.GetVlessUri()) ||
        !string.IsNullOrWhiteSpace(AndroidStorage.GetCustomConfigJson()) ||
        AndroidStorage.GetSubscriptions().Count > 0 ||
        AndroidStorage.GetServers().Count > 0;

    private void UpdateInputCardVisibility()
    {
        if (_inputCard is not null)
            _inputCard.IsVisible = !HasSavedSimpleConfig();
    }

    private Grid BuildQuickAddOverlay()
    {
        var radiusSm = GetRadius("RadiusSm");
        var radiusMd = GetRadius("RadiusMd");

        var handle = new Border
        {
            Width = 36,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        handle.BindToken(Border.BackgroundProperty, "BorderStrongBrush");

        _quickAddTitle = new TextBlock
        {
            Text = QuickAddTitleText,
            FontSize = UiScale.Fs(14),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _quickAddTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var closeBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.X, 18),
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        closeBtn.Click += (_, _) => HideQuickAddOverlay();

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(_quickAddTitle, 0);
        Grid.SetColumn(closeBtn, 1);
        titleRow.Children.Add(_quickAddTitle);
        titleRow.Children.Add(closeBtn);

        _quickAddQrButton = new Avalonia.Controls.Button
        {
            Content = IconText(UiIcons.ScanQrCode, Localization.SmpScanQrButton, 18),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 48,
            Padding = new Thickness(14, 10),
            FontSize = UiScale.Fs(13),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusMd),
        };
        _quickAddQrButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentBgSubtleBrush");
        _quickAddQrButton.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "AccentBorderBrush");
        _quickAddQrButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _quickAddQrButton.Click += (s, e) =>
        {
            HideQuickAddOverlay();
            OnSimpleQrScanClicked(s, e);
        };

        var leftLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        leftLine.BindToken(Border.BackgroundProperty, "BorderDefaultBrush");
        var rightLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
        rightLine.BindToken(Border.BackgroundProperty, "BorderDefaultBrush");
        _quickAddOrLabel = new TextBlock
        {
            Text = QuickAddOrLinkText,
            FontSize = UiScale.Fs(10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _quickAddOrLabel.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var orRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 10,
        };
        Grid.SetColumn(leftLine, 0);
        Grid.SetColumn(_quickAddOrLabel, 1);
        Grid.SetColumn(rightLine, 2);
        orRow.Children.Add(leftLine);
        orRow.Children.Add(_quickAddOrLabel);
        orRow.Children.Add(rightLine);

        _quickAddInput = new TextBox
        {
            FontSize = UiScale.Fs(12),
            MinHeight = 44,
            Padding = new Thickness(10, 8),
            AcceptsReturn = true,
            MaxLines = 3,
            CornerRadius = new CornerRadius(radiusSm),
            Watermark = Localization.SmpInputWatermark,
        };
        _quickAddInput.TextChanged += (_, _) =>
        {
            if (_quickAddError is not null)
            {
                _quickAddError.Text = string.Empty;
                _quickAddError.IsVisible = false;
            }
        };
        _quickAddInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideQuickAddOverlay();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                OnQuickAddSaveClicked(this, new RoutedEventArgs());
            }
        };

        _quickAddError = new TextBlock
        {
            FontSize = UiScale.Fs(10),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _quickAddError.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");

        _quickAddPasteButton = new Avalonia.Controls.Button
        {
            Content = QuickAddPasteText,
            MinHeight = 44,
            Padding = new Thickness(12, 0),
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _quickAddPasteButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        _quickAddPasteButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextPrimaryBrush");
        _quickAddPasteButton.Click += OnQuickAddPasteClicked;

        _quickAddSaveButton = new Avalonia.Controls.Button
        {
            Content = QuickAddSubmitText,
            MinHeight = 44,
            Padding = new Thickness(20, 0),
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.SemiBold,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _quickAddSaveButton.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _quickAddSaveButton.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _quickAddSaveButton.Click += OnQuickAddSaveClicked;

        var actionsRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(_quickAddPasteButton, 0);
        Grid.SetColumn(_quickAddSaveButton, 2);
        actionsRow.Children.Add(_quickAddPasteButton);
        actionsRow.Children.Add(_quickAddSaveButton);

        var sheetContent = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                handle,
                titleRow,
                _quickAddQrButton,
                orRow,
                _quickAddInput,
                _quickAddError,
                actionsRow,
            },
        };

        _quickAddSheet = new Border
        {
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(20, 20, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 18),
            Child = sheetContent,
        };
        _quickAddSheet.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        _quickAddSheet.BindToken(Border.BorderBrushProperty, "BorderDefaultBrush");
        _quickAddSheet.BindToken(Border.BoxShadowProperty, "ShadowMd");
        _quickAddSheet.PointerPressed += (_, e) => e.Handled = true;

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x04, 0x07, 0x0E)),
            IsVisible = false,
            Children = { _quickAddSheet },
        };
        overlay.PointerPressed += (_, _) => HideQuickAddOverlay();
        return overlay;
    }

    private void OpenQuickAddOverlay()
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        if (_quickAddOverlay is null) return;
        RefreshQuickAddLocalization();
        if (_quickAddInput is not null) _quickAddInput.Text = string.Empty;
        if (_quickAddError is not null)
        {
            _quickAddError.Text = string.Empty;
            _quickAddError.IsVisible = false;
        }
        _quickAddOverlay.IsVisible = true;
    }

    private void HideQuickAddOverlay()
    {
        if (_quickAddOverlay is null || !_quickAddOverlay.IsVisible) return;
        _quickAddOverlay.IsVisible = false;
        HideAndroidSoftKeyboard();
    }

    private void ApplyQuickAddSafeArea(Thickness insets)
    {
        if (_quickAddSheet is not null)
            _quickAddSheet.Padding = new Thickness(16, 10, 16, Math.Max(18.0, insets.Bottom + 16.0));
    }

    private void RefreshQuickAddLocalization()
    {
        var title = QuickAddTitleText;
        if (_quickAddButton is not null)
        {
            ToolTip.SetTip(_quickAddButton, title);
            AutomationProperties.SetName(_quickAddButton, title);
        }
        if (_quickAddTitle is not null) _quickAddTitle.Text = title;
        if (_quickAddQrButton is not null)
            _quickAddQrButton.Content = IconText(UiIcons.ScanQrCode, Localization.SmpScanQrButton, 18);
        if (_quickAddOrLabel is not null) _quickAddOrLabel.Text = QuickAddOrLinkText;
        if (_quickAddInput is not null) _quickAddInput.Watermark = Localization.SmpInputWatermark;
        if (_quickAddPasteButton is not null) _quickAddPasteButton.Content = QuickAddPasteText;
        if (_quickAddSaveButton is not null) _quickAddSaveButton.Content = QuickAddSubmitText;
    }

    private void OnQuickAddPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (_quickAddInput is null) return;
        var text = ReadClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;
        _quickAddInput.Text = text.Trim();
        if (_quickAddError is not null)
        {
            _quickAddError.Text = string.Empty;
            _quickAddError.IsVisible = false;
        }
        _quickAddInput.Focus();
    }

    private static string? ReadClipboardText()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var clipboard = ctx?.GetSystemService(global::Android.Content.Context.ClipboardService)
                            as global::Android.Content.ClipboardManager;
            if (clipboard is null || !clipboard.HasPrimaryClip) return null;
            var item = clipboard.PrimaryClip?.GetItemAt(0);
            return item?.CoerceToText(ctx)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async void OnQuickAddSaveClicked(object? sender, RoutedEventArgs e)
    {
        var raw = (_quickAddInput?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            ShowQuickAddError(Localization.SaveStatusUnknown);
            return;
        }

        var lowered = raw.ToLowerInvariant();
        if (lowered.StartsWith("naive://") ||
            lowered.StartsWith("naive+https://") ||
            lowered.StartsWith("naive+quic://"))
        {
            ShowQuickAddError(Localization.SmpQrNaiveUnsupportedAndroid);
            return;
        }

        if (lowered.StartsWith("http://") || lowered.StartsWith("https://"))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                ShowQuickAddError(Localization.SaveStatusUnknown);
                return;
            }
            HideQuickAddOverlay();
            try
            {
                await ApplyScannedSubscriptionUrlAsync(raw);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VpnRouter.QuickAdd",
                    $"OnQuickAddSaveClicked: subscription apply failed — {ex.GetType().Name}");
                ShowMenuFeedback(Localization.SmpQrSubscriptionFailed);
            }
            UpdateInputCardVisibility();
            return;
        }

        if (_serverInput is null) return;
        var previousInput = _serverInput.Text;
        var command = ReserveConnectCommand();
        if (command is null) return;
        _serverInput.Text = raw;
        if (!TrySaveServerInput(command))
        {
            var err = _serverInputError?.Text;
            _serverInput.Text = previousInput;
            if (_serverInputError is not null) _serverInputError.IsVisible = false;
            ShowQuickAddError(string.IsNullOrWhiteSpace(err) ? Localization.SaveStatusUnknown : err);
            return;
        }

        HideQuickAddOverlay();
        UpdateInputCardVisibility();
        ShowMenuFeedback(Localization.SmpQrConnecting);
        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect(command);
        }, DispatcherPriority.Background);
    }

    private void ShowQuickAddError(string message)
    {
        if (_quickAddError is null) return;
        _quickAddError.Text = message;
        _quickAddError.IsVisible = true;
    }
}
