using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Views;

/// <summary>
/// Routing profiles dialog (F-10 kebab parity, 2026-05-09). Desktop counterpart
/// to Android's <c>BuildProfilesOverlay</c>. Renders <see cref="BuiltInProfiles"/>
/// as cards; tapping a card toggles the matching AppGroup on the parent
/// MainWindowViewModel so the desktop routing model picks up the change
/// through the existing Apps-tab path. Read-only — apply only, no edit.
/// </summary>
public partial class RoutingProfilesDialog : Window
{
    public RoutingProfilesDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var list = this.FindControl<StackPanel>("ProfilesList");
        if (list == null) return;
        list.Children.Clear();

        var profiles = BuiltInProfiles.Get().Profiles;
        var activeNames = vm.GetActiveProfileNames();

        list.Children.Add(BuildCard(vm, null, activeNames));
        foreach (var profile in profiles)
        {
            list.Children.Add(BuildCard(vm, profile, activeNames));
        }
    }

    /// <summary>
    /// One profile card — title, description, app-count chip, "Active"
    /// badge when this profile (or "No profile") is the current selection.
    /// Click → applies the profile via MainWindowViewModel.ApplyProfileFromDialog.
    /// </summary>
    private Border BuildCard(MainWindowViewModel vm, Profile? profile, string[] activeNames)
    {
        bool isActive = profile is null
            ? activeNames.Length == 0 || (activeNames.Length == 1 && string.IsNullOrWhiteSpace(activeNames[0]))
            : activeNames.Any(n => string.Equals(n.Trim(), profile.Name, StringComparison.OrdinalIgnoreCase));

        var titleText = profile?.Name ?? Strings.ProfilesNoneTitle;
        var descText = profile?.Description ?? Strings.ProfilesNoneDescription;

        var titleBlock = new TextBlock
        {
            Text = titleText,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
        };
        titleBlock.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextPrimaryBrush"));

        var descBlock = new TextBlock
        {
            Text = descText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        descBlock.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSecondaryBrush"));

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(titleBlock);
        stack.Children.Add(descBlock);

        if (profile?.Processes != null && profile.Processes.Count > 0)
        {
            var appsCount = new TextBlock
            {
                Text = profile.Processes.Count == 1
                    ? Strings.ProfilesAppsCountOne
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        Strings.ProfilesAppsCount, profile.Processes.Count),
                FontSize = 10,
                Margin = new Thickness(0, 6, 0, 0),
            };
            appsCount.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextMutedBrush"));
            stack.Children.Add(appsCount);
        }

        if (isActive)
        {
            var activeBadge = new TextBlock
            {
                Text = Strings.ProfilesActiveBadge,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            };
            activeBadge.Bind(TextBlock.ForegroundProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AccentFgBrush"));
            stack.Children.Add(activeBadge);
        }

        var border = new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = stack,
        };
        border.Bind(Border.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                isActive ? "AccentBgSubtleBrush" : "SurfaceSunkenBrush"));
        border.Bind(Border.BorderBrushProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                isActive ? "BorderAccentBrush" : "BorderSubtleBrush"));

        // Tap → apply. Wrapping the Border in a Button keeps the visual
        // (no chrome on the card itself) while picking up keyboard/click
        // semantics for free.
        var clickWrap = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = border,
        };
        clickWrap.Click += (_, _) =>
        {
            try
            {
                vm.ApplyProfileFromDialog(profile);
            }
            catch { /* surfaced by VM logger — keep dialog responsive */ }
            Close();
        };
        return new Border { Child = clickWrap };
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
