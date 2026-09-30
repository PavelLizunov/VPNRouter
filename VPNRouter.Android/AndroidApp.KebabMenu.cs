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
    private Avalonia.Controls.Button MakeMenuItem(
        string label,
        string foregroundKey,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7),
            FontSize = UiScale.Fs(11),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            IsHitTestVisible = onClick is not null,
        };
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, foregroundKey);
        if (onClick is not null) btn.Click += onClick;
        return btn;
    }

    private Avalonia.Controls.Button MakeSegmentButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            FontSize = UiScale.Fs(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        StyleSegmentButton(btn, active);
        btn.Click += onClick;
        return btn;
    }

    private Grid MakeSegmentRow(Avalonia.Controls.Button left, Avalonia.Controls.Button right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(14, 4, 14, 4),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private void AppendMenuSectionWithControls(
        StackPanel stack,
        string headerText,
        Control[] items)
    {
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    private void AppendMenuSection(
        StackPanel stack,
        string headerText,
        Avalonia.Controls.Button[] items)
    {
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    private void AppendMenuDivider(StackPanel stack)
    {
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(4, 4, 4, 4),
        };
        divider.BindToken(Border.BackgroundProperty, "BorderSubtleBrush");
        stack.Children.Add(divider);
    }

    private static void HideSubtreeFromAccessibility(StyledElement element)
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        if (element is ILogical logical)
        {
            foreach (var child in logical.LogicalChildren)
            {
                if (child is StyledElement childElement)
                    HideSubtreeFromAccessibility(childElement);
            }
        }
    }

    private void OnKebabMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        _kebabPopup.IsOpen = !_kebabPopup.IsOpen;
        if (_kebabPopup.IsOpen)
        {
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        }
    }

    private void OnMenuLangRuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Localization.Ru) return;
        ApplyLanguage(true);
    }

    private void OnMenuLangEnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Localization.Ru) return;
        ApplyLanguage(false);
    }

    private void OnMenuThemeLightClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("light");
    }

    private void OnMenuThemeDarkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("dark");
    }

    private void ApplyLanguage(bool ru)
    {
        if (Localization.Ru == ru) return;
        ToggleLanguageAndRefresh();
        RepaintLanguageSegment();
    }

    private void ApplyTheme(string mode)
    {
        var current = AndroidStorage.GetTheme();
        if (current == mode) return;
        AndroidStorage.SetTheme(mode);
        RequestedThemeVariant = mode == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        try
        {
            MainActivity.Instance?.SetSystemBarsAppearance(mode == "dark");
        }
        catch { }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { RebuildSimplePageView(); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"Advanced-shell theme refresh failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
        }, Avalonia.Threading.DispatcherPriority.Background);

        if (_mascotImage is not null)
        {
            _mascotImage.Source = LoadMascot();
        }
        RepaintThemeSegment();
        RepaintLanguageSegment();
        SetVpnChipState(_vpnChipState, force: true);
        SetZapretChipState(_zapretChipState, force: true);
        UpdateConnectionState(MainActivity.IntendedConnected);
    }

    private void RebuildSimplePageView()
    {
        var old = _advShellOverlay;
        if (old is null) return;
        if (old.Parent is not Panel rootPanel) return;
        var idx = rootPanel.Children.IndexOf(old);
        if (idx < 0) return;

        var advancedWasOpen = old.IsVisible;
        var advancedTab = _advShellSelectedTab;

        if (advancedWasOpen && _kebabPopup is not null)
            _kebabPopup.IsOpen = false;

        _advShellTabContent.Clear();
        _advShellTabButtons.Clear();

        var fresh = BuildAdvancedShellOverlay();
        rootPanel.Children[idx] = fresh;
        _advShellOverlay = fresh;

        if (advancedWasOpen)
        {
            try { OpenAdvancedShell(advancedTab); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"Restore Advanced shell after theme rebuild failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
        }
    }

    private void RepaintThemeSegment()
    {
        var isDark = AndroidStorage.GetTheme() == "dark";
        StyleSegmentButton(_menuThemeLight, !isDark);
        StyleSegmentButton(_menuThemeDark, isDark);
    }

    private void RepaintLanguageSegment()
    {
        StyleSegmentButton(_menuLangRu, Localization.Ru);
        StyleSegmentButton(_menuLangEn, !Localization.Ru);
    }

    private void StyleSegmentButton(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty,
            active ? "AccentBgSubtleBrush" : "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty,
            active ? "AccentFgBrush" : "TextSecondaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty,
            active ? "BorderAccentBrush" : "BorderSubtleBrush");
    }

}
