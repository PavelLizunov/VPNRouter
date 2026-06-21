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
    // ── v2.32.0 (AND-PROFILES, 2026-05-08) Profiles overlay ─────────────
    //
    // Fullscreen Border layered over the main ScrollViewer (same pattern as
    // Settings / Free Configs / Server list overlays). Top: title bar with
    // close ✕. Body: scrolling StackPanel of profile cards rebuilt on each
    // open so the active-profile indicator reflects the latest persisted
    // state.
    //
    // Tap-to-apply semantics: tapping any card calls ApplyProfile() which
    // routes through ProfileApplication.Plan() (Core, unit-tested) → writes
    // to AndroidStorage → refreshes per-app form count + form radios →
    // closes overlay → surfaces feedback toast. Per the prompt scope
    // (view + apply only; edit deferred), there's no "edit profile" /
    // "duplicate" / "delete" surface — those become a follow-up chip.

    private Border BuildProfilesOverlay()
    {
        _profilesOverlayTitle = new TextBlock
        {
            Text = Localization.ProfilesOverlayTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _profilesOverlayTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _profilesCloseBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
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
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _profilesOverlayIntro.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // Body StackPanel — populated on each ShowProfilesOverlay() call so
        // the active-state highlight reflects the current AndroidStorage
        // value without an event-bus subscription. Idempotent: rebuilding
        // 8 cards is essentially free.
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

    /// <summary>
    /// Build a single profile card. <paramref name="profile"/> = null →
    /// the "No profile" pseudo-card that clears the active selection
    /// and switches back to full-tunnel.
    /// </summary>
    private Border BuildProfileCard(VPNRouter.Core.Models.Profile? profile, string? activeName)
    {
        // Determine active state — null active ↔ null profile is the
        // "No profile" highlight; otherwise compare names case-insensitively
        // (storage uses the original casing but a stale lower-cased entry
        // shouldn't break the highlight).
        bool isActive;
        if (profile is null)
        {
            isActive = string.IsNullOrEmpty(activeName);
        }
        else
        {
            isActive = !string.IsNullOrEmpty(activeName)
                       && string.Equals(activeName, profile.Name, StringComparison.OrdinalIgnoreCase);
        }

        var titleText = profile?.Name ?? Localization.ProfilesNoneTitle;
        var descText = profile?.Description ?? Localization.ProfilesNoneDescription;

        var titleBlock = new TextBlock
        {
            Text = titleText,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        titleBlock.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var descBlock = new TextBlock
        {
            Text = descText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        descBlock.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // Active-state header pill (rendered when this card is the
        // currently-applied profile). Uses Success accent so the user can
        // spot the active card at a glance even when scrolled.
        TextBlock? activeBadge = null;
        if (isActive)
        {
            activeBadge = new TextBlock
            {
                Text = Localization.ProfilesActiveBadge,
                FontWeight = FontWeight.SemiBold,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 4),
            };
            activeBadge.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
        }

        // Metadata chips — apps count + DNS mode + (optional) block-on-fail.
        // Hidden for the "No profile" pseudo-card (no metadata to show).
        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (profile is not null)
        {
            var pkgCount = profile.AndroidPackages?.Count ?? 0;
            var pkgLabel = pkgCount == 1
                ? Localization.ProfilesAppsCountOne
                : string.Format(Localization.ProfilesAppsCount, pkgCount);
            chipRow.Children.Add(BuildProfileChip(pkgLabel, "AccentBgSubtleBrush", "AccentFgBrush"));

            if (!string.IsNullOrWhiteSpace(profile.DnsMode))
            {
                chipRow.Children.Add(BuildProfileChip(
                    string.Format(Localization.ProfilesDnsModeChip, profile.DnsMode),
                    "SurfaceSunkenBrush", "TextSecondaryBrush"));
            }

            if (profile.BlockOnVpnFail)
            {
                chipRow.Children.Add(BuildProfileChip(
                    Localization.ProfilesBlockOnFailChip, "WarningBgBrush", "WarningFgBrush"));
            }
        }

        var stack = new StackPanel { Spacing = 0 };
        if (activeBadge is not null) stack.Children.Add(activeBadge);
        stack.Children.Add(titleBlock);
        stack.Children.Add(descBlock);
        if (profile is not null) stack.Children.Add(chipRow);

        var card = new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(GetRadius("RadiusMd")),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            Child = stack,
        };
        card.BindToken(Border.BackgroundProperty, isActive ? "AccentBgSubtleBrush" : "SurfaceBaseBrush");
        card.BindToken(Border.BorderBrushProperty, isActive ? "BorderAccentBrush" : "BorderSubtleBrush");

        // Tap anywhere on the card → apply. PointerPressed fires before
        // PointerReleased on Avalonia's mobile pointer pipeline; using
        // Pressed feels snappier and matches the radio-card / checkbox-
        // card pattern in the Settings overlay.
        card.PointerPressed += (_, __) => ApplyProfile(profile);

        return card;
    }

    private Border BuildProfileChip(string text, string bgKey, string fgKey)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
        };
        label.BindToken(TextBlock.ForegroundProperty, fgKey);

        var chip = new Border
        {
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(GetRadius("RadiusPill")),
            Child = label,
        };
        chip.BindToken(Border.BackgroundProperty, bgKey);
        return chip;
    }

    private void OnMenuProfilesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowProfilesOverlay();
    }

    private void ShowProfilesOverlay()
    {
        if (_profilesOverlay is null || _profilesList is null) return;

        // Rebuild the card list each open so the active-profile highlight
        // reflects whatever's currently in storage. Cheap (8 entries) and
        // avoids a manual invalidate-on-storage-change wiring.
        _profilesList.Children.Clear();
        var active = AndroidStorage.GetActiveProfile();

        // "No profile" pseudo-card first — provides a clear escape hatch
        // back to full-tunnel without forcing the user to find the form's
        // tunnel-mode radio.
        _profilesList.Children.Add(BuildProfileCard(null, active));

        var catalog = VPNRouter.Core.Services.BuiltInAndroidProfiles.Get();
        foreach (var profile in catalog.Profiles)
        {
            _profilesList.Children.Add(BuildProfileCard(profile, active));
        }

        _profilesOverlay.IsVisible = true;
    }

    private void OnProfilesCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_profilesOverlay is not null) _profilesOverlay.IsVisible = false;
    }

    /// <summary>
    /// Apply the user's profile pick. Routes through
    /// <see cref="VPNRouter.Core.Services.ProfileApplication.Plan"/> (pure
    /// function, unit-tested) so the storage writes here are the only
    /// Android-side concern. Refreshes the form's per-app count + tunnel-
    /// mode radios so the user sees the new state on close, and surfaces
    /// a feedback banner so the apply isn't invisible.
    /// </summary>
    private void ApplyProfile(VPNRouter.Core.Models.Profile? profile)
    {
        var plan = VPNRouter.Core.Services.ProfileApplication.Plan(profile);

        AndroidStorage.SetActiveProfile(plan.ActiveProfileName);
        if (plan.RoutingMode is not null)
            AndroidStorage.SetRoutingMode(plan.RoutingMode);
        if (plan.AndroidPackages is not null)
            AndroidStorage.SetPerAppPackages(plan.AndroidPackages);
        if (plan.PerAppMode is not null)
            AndroidStorage.SetPerAppMode(plan.PerAppMode);
        if (plan.PerAppLastMode is not null)
            AndroidStorage.SetPerAppLastMode(plan.PerAppLastMode);
        if (plan.BlockOnVpnFail is not null)
            AndroidStorage.SetBlockOnVpnFail(plan.BlockOnVpnFail.Value);

        // Form radios may be visible behind the overlay — re-seed so
        // dismissing reveals the right state. Settings overlay re-seeds
        // its own controls in ShowSettings, so no work needed there.
        var routing = AndroidStorage.GetRoutingMode();
        if (_splitRadio is not null) _splitRadio.IsChecked = routing == "split";
        if (_fullRadio is not null) _fullRadio.IsChecked = routing == "full";
        UpdatePerAppFormCountLabel();

        // Toast feedback. Profile name embedded verbatim — catalog names
        // are ASCII underscore-separated (Discord_Privacy / Work_Suite)
        // so the localized format string still reads cleanly in RU/EN.
        var msg = profile is null
            ? Localization.ProfilesClearedToast
            : string.Format(Localization.ProfilesAppliedToast, profile.Name);
        ShowMenuFeedback(msg);

        if (_profilesOverlay is not null) _profilesOverlay.IsVisible = false;
    }

}
