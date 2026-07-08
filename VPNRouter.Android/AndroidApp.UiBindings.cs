using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace VPNRouter.Android;

/// <summary>
/// Phase 2C (Wave 9, 2026-05-18) — Settings tab UI builders extracted
/// from <c>AndroidApp.axaml.cs</c>. Contains all <c>BuildSettings*</c>
/// section builders and the shared <c>Make*</c> card helpers that turn
/// CheckBox / RadioButton primitives into the desktop-parity card
/// layouts. This is the bulk of the Settings/Network tab's visual tree
/// construction.
///
/// <para><strong>What's here</strong>:</para>
/// <list type="bullet">
///   <item><see cref="BuildNetworkTabContent"/> — master-detail Grid
///   (side-nav + content pane + footer).</item>
///   <item><see cref="BuildSettingsSideNav"/> +
///   <see cref="MakeSettingsSubSectionButton"/> /
///   <see cref="SettingsSubSectionLabel"/> /
///   <see cref="StyleSettingsSubSectionButton"/> — left-column nav.</item>
///   <item><see cref="BuildSettingsContentPane"/> +
///   <see cref="WrapSubSectionScroller"/> /
///   <see cref="SelectSettingsSubSection"/> — content pane host that
///   swaps between the 6 sub-section panels.</item>
///   <item>The 6 <see cref="BuildSettingsRoutingSection"/> /
///   <see cref="BuildSettingsRulesSection"/> /
///   <see cref="BuildSettingsLeakSection"/> /
///   <see cref="BuildSettingsContentSection"/> /
///   <see cref="BuildSettingsUpdatesSection"/> /
///   <see cref="BuildSettingsAutostartSection"/> builders themselves,
///   each producing a scrollable Control matching the desktop
///   NetworkPage sub-section.</item>
///   <item><see cref="BuildSettingsFooterBar"/> — Apply bar swap target.</item>
///   <item><see cref="BuildDpiBypassCard"/> — DPI-bypass selector card
///   inside the Routing section.</item>
///   <item>Card-shape helpers shared across sections:
///   <see cref="MakeSectionTitle"/>, <see cref="WrapSection"/>,
///   <see cref="MakeRadioCard"/>, <see cref="MakeCheckboxCard"/>,
///   <see cref="MakeLabeledCheckboxRow"/>,
///   <see cref="MakeAutostartRow"/>.</item>
/// </list>
///
/// <para>Event handlers for the controls built here
/// (<c>OnSettings*Changed</c>, <c>OnReliability*Clicked</c>) stay in
/// other partials (main <c>AndroidApp.axaml.cs</c> for the Settings
/// handlers, <c>AndroidApp.Permissions.cs</c> for the Reliability deep-
/// link handlers) — this partial is intentionally just the View-side
/// construction.</para>
/// </summary>
public partial class AndroidApp
{
    // ── Settings tab body (Network tab inside the Advanced shell) ──────
    //
    // Phase C (2026-05-10) restructures this tab from a flat scrollable
    // stack to a master-detail layout with side-nav + per-sub-section
    // content pane + footer Apply bar — matching desktop NetworkPage's
    // shape. Pre-Phase-C the four sub-sections (Routing / Leak / Updates /
    // Autostart) were stacked in one ScrollViewer; AND-MIGRATE-OVERLAYS
    // (2026-05-09) had brought them into the Advanced shell as the
    // "Network" tab. The flat layout shipped fine functionally but
    // structurally diverged from desktop, so Phase C restores parity.

    /// <summary>
    /// Phase C (2026-05-10) — Settings tab body. Mirrors desktop
    /// NetworkPage.axaml's master-detail layout: a left side-nav listing
    /// the six sub-sections (Routing / Rules / Leak / Content / Updates /
    /// Autostart) + a right scrollable content pane swapped by the active
    /// sub-section, with a footer Apply bar carrying the "✓ Auto-saved"
    /// badge or the [Apply] button depending on whether there are pending
    /// changes that need a tunnel reload to take effect.
    ///
    /// <para>Index order matches desktop's <c>SelectedSettingsIndex</c>
    /// (NetworkPage.axaml:202-211 + MainWindowViewModel.IsSettings*Selected
    /// at line 1710-1715) so user muscle-memory carries between platforms.
    /// On Android the desktop's standalone Reliability section (Always-on
    /// VPN + battery + auto-reconnect) is folded into the Autostart sub-
    /// section per the parity plan's platform-impossible item table —
    /// Always-on VPN IS the Android replacement for Windows-Service-on-boot,
    /// so it naturally belongs there.</para>
    /// </summary>
    private Control BuildNetworkTabContent()
    {
        var sideNav = BuildSettingsSideNav();
        var contentPane = BuildSettingsContentPane();
        var footerBar = BuildSettingsFooterBar();

        // Master-detail Grid: two-column body row + full-width footer row.
        // Side-nav width matches desktop's 140 dp (NetworkPage.axaml:190
        // ColumnDefinitions="140,*"). On Android dp ≈ logical pixel for
        // Avalonia layout, so we use the same value.
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Background = GetBrush("SurfaceAppBrush"),
        };
        Grid.SetRow(sideNav, 0);
        Grid.SetColumn(sideNav, 0);
        Grid.SetRow(contentPane, 0);
        Grid.SetColumn(contentPane, 1);
        Grid.SetRow(footerBar, 1);
        Grid.SetColumn(footerBar, 0);
        Grid.SetColumnSpan(footerBar, 2);
        body.Children.Add(sideNav);
        body.Children.Add(contentPane);
        body.Children.Add(footerBar);

        return body;
    }

    /// <summary>
    /// Left-column side-nav. Six button rows (Routing / Rules / Leak /
    /// Content / Updates / Autostart). Active row paints with
    /// <c>AccentBgSubtleBrush</c> + <c>AccentFgBrush</c> + a 2 dp left
    /// underline (vertical bar) to match desktop NetworkPage's ListBoxItem
    /// active style. Inactive rows use <c>TextMutedBrush</c>.
    /// </summary>
    private Border BuildSettingsSideNav()
    {
        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(0, 6, 0, 6),
        };
        for (int i = 0; i < 6; i++)
        {
            var button = MakeSettingsSubSectionButton(i);
            _settingsSubSectionButtons[i] = button;
            stack.Children.Add(button);
        }

        var scroller = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };

        return new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background = GetBrush("SurfaceSunkenBrush"),
            Child = scroller,
        };
    }

    /// <summary>
    /// One side-nav row. Tap selects the sub-section + flips the content
    /// pane. Persists the choice via <see cref="AndroidStorage.SetSettingsActiveSubSection"/>
    /// so reopening Advanced > Settings restores the same pane.
    /// <para>POL-1: dropped the 2 dp left BorderThickness marker — desktop
    /// NetworkPage uses Avalonia's default ListBoxItem:selected styling
    /// (AccentBgSubtle bg + AccentFg fg, no left bar). The marker was an
    /// Android-only invention and made the side-nav read inconsistently
    /// vs Apps category list / Public sub-tabs which already match
    /// desktop's flat styling.</para>
    /// </summary>
    private Avalonia.Controls.Button MakeSettingsSubSectionButton(int index)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = SettingsSubSectionLabel(index),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
        };
        btn.Click += (_, _) => SelectSettingsSubSection(index);
        StyleSettingsSubSectionButton(btn, index == _settingsSelectedSubSection);
        return btn;
    }

    private static string SettingsSubSectionLabel(int index) => index switch
    {
        0 => Localization.SettingsSectionRouting,
        1 => Localization.SettingsSectionRules,
        2 => Localization.SettingsSectionLeak,
        3 => Localization.SettingsSectionContent,
        4 => Localization.SettingsSectionUpdates,
        5 => Localization.SettingsSectionAutostart,
        _ => string.Empty,
    };

    /// <summary>Active = AccentBgSubtle bg + AccentFg fg (matches desktop
    /// ListBoxItem:selected default); inactive = muted text, transparent bg.
    /// POL-1: BorderBrush no longer assigned — left bar dropped.</summary>
    private void StyleSettingsSubSectionButton(Avalonia.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.Background = GetBrush("AccentBgSubtleBrush");
            btn.Foreground = GetBrush("AccentFgBrush");
        }
        else
        {
            btn.Background = Brushes.Transparent;
            btn.Foreground = GetBrush("TextMutedBrush");
        }
    }

    /// <summary>
    /// Right-column content pane. One scroller per sub-section, all built
    /// up-front + held in <see cref="_settingsSubSectionPanels"/>. Selection
    /// flips IsVisible on each child rather than rebuilding the tree, so
    /// scroll position survives sub-section switches.
    /// </summary>
    private Control BuildSettingsContentPane()
    {
        // Initial selected sub-section comes from persisted state; default
        // to Routing (index 0) on first open.
        _settingsSelectedSubSection = AndroidStorage.GetSettingsActiveSubSection();

        var host = new Grid
        {
            Background = GetBrush("SurfaceAppBrush"),
        };

        _settingsSubSectionPanels[0] = WrapSubSectionScroller(BuildSettingsRoutingSection());
        _settingsSubSectionPanels[1] = WrapSubSectionScroller(BuildSettingsRulesSection());
        _settingsSubSectionPanels[2] = WrapSubSectionScroller(BuildSettingsLeakSection());
        _settingsSubSectionPanels[3] = WrapSubSectionScroller(BuildSettingsContentSection());
        _settingsSubSectionPanels[4] = WrapSubSectionScroller(BuildSettingsUpdatesSection());

        // Autostart pane on Android merges desktop's Autostart + Reliability —
        // see BuildSettingsAutostartSection comment for rationale.
        _settingsSubSectionPanels[5] = WrapSubSectionScroller(BuildSettingsAutostartSection());

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is null) continue;
            panel.IsVisible = i == _settingsSelectedSubSection;
            host.Children.Add(panel);
        }

        return host;
    }

    /// <summary>
    /// Wrap a sub-section's content stack in a ScrollViewer + outer padding
    /// matching desktop's NetworkPage right-pane chrome (Padding="0,10,0,12"
    /// + inner Margin="14,0,14,0"). The sunken background stays on the
    /// outer host; this scroller is transparent.
    /// </summary>
    private ScrollViewer WrapSubSectionScroller(Control content)
    {
        var inner = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(14, 10, 14, 12),
            Children = { content },
        };

        return new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };
    }

    /// <summary>
    /// Switch the active sub-section. Persists the index, flips IsVisible
    /// on the panel set, repaints the side-nav buttons. Idempotent —
    /// re-selecting the active section is a no-op.
    /// </summary>
    private void SelectSettingsSubSection(int index)
    {
        if (index < 0 || index >= 6) return;
        _settingsSelectedSubSection = index;
        AndroidStorage.SetSettingsActiveSubSection(index);

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is not null) panel.IsVisible = i == index;
        }
        for (int i = 0; i < _settingsSubSectionButtons.Length; i++)
        {
            var btn = _settingsSubSectionButtons[i];
            if (btn is not null) StyleSettingsSubSectionButton(btn, i == index);
        }
    }

    /// <summary>
    /// Footer Apply bar. Mirrors desktop NetworkPage.axaml:2213-2243 — left
    /// side hosts the "✓ Auto-saved" badge (resting state), right side
    /// hosts the "Apply now (reload VPN)" button. Per the Phase C spec the
    /// two swap based on <see cref="_settingsDirty"/>: the badge shows when
    /// no pending changes exist, the button takes its place when there are.
    /// </summary>
    private Border BuildSettingsFooterBar()
    {
        // ✓ Auto-saved badge — small SuccessFg pill stating the obvious so
        // the user doesn't go hunting for a Save button. Mirrors desktop's
        // L_SettingsAutosaved row.
        var checkGlyph = new TextBlock
        {
            Text = "✓",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessSolidBrush"),
        };
        var badgeText = new TextBlock
        {
            Text = Localization.SettingsAutosaved,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessFgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _settingsAutoSavedBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Children = { checkGlyph, badgeText },
            },
        };

        // [Apply] button — swaps in when _settingsDirty is true. Click
        // clears the dirty flag and, if currently connected, kicks a
        // disconnect/reconnect cycle so the running tunnel picks up the
        // new config. When not connected the click just clears the badge
        // (next Connect will rebuild from fresh storage anyway).
        _settingsApplyButton = new Avalonia.Controls.Button
        {
            Content = Localization.ApplyNowReloadVpn,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
        };
        _settingsApplyButton.Click += OnSettingsApplyClicked;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_settingsAutoSavedBadge, 0);
        Grid.SetColumn(_settingsApplyButton, 1);
        grid.Children.Add(_settingsAutoSavedBadge);
        grid.Children.Add(_settingsApplyButton);

        return new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            Background = GetBrush("SurfaceSunkenBrush"),
            Child = grid,
        };
    }

    /// <summary>
    /// Routing sub-section: split/full radio cards + Russian-traffic bypass.
    /// Mirrors desktop NetworkPage.axaml lines 237-309 (Routing block).
    /// </summary>
    private Control BuildSettingsRoutingSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRouting);
        var description = new TextBlock
        {
            Text = Localization.RoutingDescription,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var routingMode = AndroidStorage.GetRoutingMode();

        _settingsSplitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "split",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsSplitRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var splitCard = MakeRadioCard(_settingsSplitRadio,
            Localization.SplitTunnelTitle, Localization.SplitTunnelSubtitle);

        _settingsFullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "full",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsFullRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var fullCard = MakeRadioCard(_settingsFullRadio,
            Localization.FullTunnelTitle, Localization.FullTunnelSubtitle);

        _settingsBypassRu = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBypassRussianTraffic(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBypassRu.IsCheckedChanged += OnSettingsBypassRuChanged;
        var bypassCard = MakeCheckboxCard(_settingsBypassRu,
            Localization.BypassRussianTrafficLabel, Localization.BypassRussianTrafficHint);

        // 2026-05-15 (Bug-AND-004, brat live-test): DPI bypass (Zapret)
        // card removed from Routing tab on Android. Zapret is Windows-
        // only — the card was showing a non-functional picker with a
        // confusing «...в отличие от Windows-версии Zapret» footnote.
        // Same rationale as Bug-AND-002/003: platform-not-applicable
        // features hidden, not shown as stubs. BuildDpiBypassCard()
        // method retained in case a future Android-native DPI bypass
        // implementation lands.

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, description, splitCard, fullCard, bypassCard }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET, 2026-05-07) — Routing-section card for the DPI
    /// bypass strategy picker. Three-value ComboBox (Off / Standard /
    /// Aggressive) + descriptive hint + warning blurb. Uses the same
    /// SurfaceSunkenBrush card chrome as the bypass-RU checkbox card so
    /// the section reads as one consistent block.
    /// </summary>
    private Border BuildDpiBypassCard()
    {
        var titleText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassLabel,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var hintText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassHint,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsDpiBypassMode = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 11,
            ItemsSource = new[]
            {
                Localization.SettingsDpiBypassOff,
                Localization.SettingsDpiBypassStandard,
                Localization.SettingsDpiBypassAggressive,
            },
            SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
            {
                "standard" => 1,
                "aggressive" => 2,
                _ => 0,
            },
        };
        _settingsDpiBypassMode.SelectionChanged += OnSettingsDpiBypassModeChanged;

        var warning = new TextBlock
        {
            Text = Localization.SettingsDpiBypassWarning,
            FontSize = 9,
            Foreground = GetBrush("WarningFgBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        return new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { titleText, hintText, _settingsDpiBypassMode, warning }
            }
        };
    }

    /// <summary>
    /// Phase C (2026-05-10) — Rules sub-section. Mirrors desktop
    /// NetworkPage.axaml's Rules block (around line 322) but on Android the
    /// CustomRulesParser pipeline isn't wired into AndroidConfigBuilder yet
    /// (custom routing rules are a desktop-only knob today). Rather than
    /// shipping a no-op text editor that pretends to take effect, we surface
    /// a placeholder explainer that points the user to the Apps tab as the
    /// current way to choose what goes through VPN. The side-nav slot exists
    /// so visual parity with desktop is preserved + a future port can fill
    /// in the editor without re-doing the chrome.
    /// </summary>
    private Control BuildSettingsRulesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRules);

        var note = new TextBlock
        {
            Text = Localization.AdvSettingsRulesAndroidNote,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        // Sunken Border mirrors the per-section card chrome the other
        // sub-sections use — without it the placeholder reads as a stray
        // paragraph instead of a deliberate empty state.
        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = note,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Leak protection sub-section. Desktop NetworkPage:1779-1859 packs four
    /// inline 24,* checkbox rows inside a single SurfaceSunken Border. We
    /// mirror that chrome but surface only the controls that map cleanly to
    /// the Android stack — block_on_vpn_fail (VpnService.setBlocking) and
    /// the DNS strategy combo. StrictMode / ForceIpv4 / FlushDns / StrictDns
    /// are desktop-only (Windows firewall + DNS cache flush) and intentionally
    /// not exposed; they would be no-ops on Android. The Block-on-VPN-fail
    /// checkbox is the Android equivalent of desktop's firewall-netsh-based
    /// kill switch — same UI, different mechanism (VpnService.setBlocking
    /// instead of netsh AdvFirewall) — so we keep it visible and document
    /// the platform difference in the hint copy.
    /// </summary>
    private Control BuildSettingsLeakSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionLeak);

        _settingsBlockOnVpnFail = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockOnVpnFail(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _settingsBlockOnVpnFail.IsCheckedChanged += OnSettingsBlockOnVpnFailChanged;

        // Inline 24,* checkbox row inside a SurfaceSunken Border, matching
        // desktop NetworkPage:1804-1857. Label TextBlock sits in the * col
        // with TextWrapping=Wrap so long localised labels reflow inside the
        // card width instead of pushing the parent past the ScrollViewer.
        var blockLabel = new TextBlock
        {
            Text = Localization.BlockOnVpnFailLabel,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
        };
        var blockGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(_settingsBlockOnVpnFail, 0);
        Grid.SetColumn(blockLabel, 1);
        blockGrid.Children.Add(_settingsBlockOnVpnFail);
        blockGrid.Children.Add(blockLabel);

        var blockHint = new TextBlock
        {
            Text = Localization.BlockOnVpnFailHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(30, 0, 0, 0),
        };

        var leakInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { blockGrid, blockHint }
            }
        };

        // DNS strategy combo lives in a sibling SurfaceSunken Border so the
        // visual grouping reads "two leak-protection cards", same as the
        // desktop pattern of stacking SurfaceSunken Borders inside a section.
        _settingsDnsStrategy = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 12,
            ItemsSource = new[]
            {
                Localization.DnsStrategyIpv4Only,
                Localization.DnsStrategyPreferIpv4,
                Localization.DnsStrategyPreferIpv6,
            },
            SelectedIndex = AndroidStorage.GetDnsStrategy() switch
            {
                "prefer_ipv4" => 1,
                "prefer_ipv6" => 2,
                _ => 0,
            },
        };
        _settingsDnsStrategy.SelectionChanged += OnSettingsDnsStrategyChanged;

        var dnsHeader = new TextBlock
        {
            Text = Localization.DnsStrategyHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };
        var dnsHint = new TextBlock
        {
            Text = Localization.DnsStrategyHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var dnsInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { dnsHeader, _settingsDnsStrategy, dnsHint }
            }
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, leakInner, dnsInner }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Content sub-section. Mirrors desktop NetworkPage:1861-1879 — a single
    /// checkbox-card for AdGuard DNS / ad blocking. Persists the toggle today
    /// so future overlays read consistent state; the AndroidConfigBuilder
    /// integration (geosite-ads route → reject + AdGuard DoH override) is a
    /// follow-up. Visually identical to desktop's "checkbox-card" pattern.
    /// </summary>
    private Control BuildSettingsContentSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionContent);

        _settingsBlockAds = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockAds(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBlockAds.IsCheckedChanged += OnSettingsBlockAdsChanged;
        var card = MakeCheckboxCard(_settingsBlockAds,
            Localization.SettingsBlockAdsLabel,
            Localization.SettingsBlockAdsHint);

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    // Phase C (2026-05-10): BuildSettingsReliabilitySection was removed —
    // its three rows (Always-on VPN, battery optimization, auto-reconnect)
    // moved into BuildSettingsAutostartSection above so the side-nav has
    // exactly six entries matching desktop. UpdateBatteryOptimizationStatus
    // + the OnReliability* event handlers are still wired (see below);
    // they're now invoked from inside the Autostart pane instead.

    /// <summary>
    /// Updates sub-section: prerelease channel toggle + current version
    /// label + manual check button. Mirrors desktop NetworkPage 1881-1928.
    /// On Android the Check button reuses the same placeholder behaviour
    /// as the kebab > Diagnostics > "Check for updates" entry — Android
    /// auto-update is out of v3.0 alpha scope (handbook §6).
    /// </summary>
    private Control BuildSettingsUpdatesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionUpdates);

        // Channel sub-card. Desktop NetworkPage:1885-1899 wraps the channel
        // header + prerelease checkbox in a SurfaceSunken Border. Mirroring
        // the chrome here keeps Android's stacked-section layout matching
        // desktop's master-detail pane visually.
        var channelHeader = new TextBlock
        {
            Text = Localization.UpdateChannelHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsReceivePrereleases = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetUpdateChannel() == "experimental",
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.ReceivePrereleasesLabel,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            }
        };
        _settingsReceivePrereleases.IsCheckedChanged += OnSettingsChannelChanged;

        var channelInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 3,
                Children = { channelHeader, _settingsReceivePrereleases }
            }
        };

        // Current version + Check button row in its own SurfaceSunken Border,
        // mirroring desktop NetworkPage:1904-1927 (the SUGGEST-22 panel).
        _settingsCurrentVersion = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var versionLabel = new TextBlock
        {
            Text = Localization.CurrentVersionLabel,
            FontSize = 10,
            Opacity = 0.7,
            // Bug-AND-018 (2026-05-16, polish iter 32) — paired with
            // shortened RU "Версия" label so the row fits without wrap.
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var versionStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { versionLabel, _settingsCurrentVersion }
        };

        var checkBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CheckForUpdatesButton,
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBtn.Click += OnSettingsCheckUpdatesClicked;

        var versionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(versionStack, 0);
        Grid.SetColumn(checkBtn, 1);
        versionRow.Children.Add(versionStack);
        versionRow.Children.Add(checkBtn);

        var versionInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = versionRow,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, channelInner, versionInner }
        };
        return WrapSection(stack);
    }

    /// <summary>
    /// Android Autostart sub-section: Always-on VPN, battery optimisation,
    /// auto-reconnect, and optional external broadcast control.
    /// </summary>
    private Control BuildSettingsAutostartSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionAutostart);

        // Always-on VPN is Android's boot/network-change restoration path.
        var androidIntro = new TextBlock
        {
            Text = Localization.AdvSettingsAutostartAndroidIntro,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        // ── Always-on VPN row (formerly desktop Reliability section) ──
        var alwaysOnTitle = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var alwaysOnHint = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var alwaysOnBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ReliabilityAlwaysOnButton,
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        alwaysOnBtn.Click += OnReliabilityAlwaysOnClicked;
        var alwaysOnRow = new StackPanel
        {
            Spacing = 4,
            Children = { alwaysOnTitle, alwaysOnHint, alwaysOnBtn },
        };

        // ── Battery optimization row (formerly desktop Reliability) ──
        var batteryTitle = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _reliabilityBatteryStatusLabel = new TextBlock
        {
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        };
        var batteryHint = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptHint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        _reliabilityBatteryButton = new Avalonia.Controls.Button
        {
            FontSize = 10,
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _reliabilityBatteryButton.Click += OnReliabilityBatteryClicked;
        UpdateBatteryOptimizationStatus();
        var batteryRow = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                batteryTitle,
                _reliabilityBatteryStatusLabel,
                batteryHint,
                _reliabilityBatteryButton,
            },
        };

        // ── Auto-reconnect on network change toggle ──
        _reliabilityAutoReconnect = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutoReconnectOnNetworkChange(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _reliabilityAutoReconnect.IsCheckedChanged += OnReliabilityAutoReconnectChanged;
        var autoReconnectCard = MakeCheckboxCard(_reliabilityAutoReconnect,
            Localization.ReliabilityAutoReconnectTitle,
            Localization.ReliabilityAutoReconnectHint);

        // ── P4: external broadcast control (Tasker / widgets), default OFF ──
        _externalControlToggle = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetExternalControlEnabled(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _externalControlToggle.IsCheckedChanged += OnExternalControlChanged;
        var externalControlCard = MakeCheckboxCard(_externalControlToggle,
            Localization.ExternalControlTitle,
            Localization.ExternalControlHint);

        var stack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                sectionTitle,
                androidIntro,
                alwaysOnRow,
                batteryRow,
                autoReconnectCard,
                externalControlCard,
            }
        };
        return WrapSection(stack);
    }

    // ── Settings overlay layout helpers ─────────────────────────────────

    private TextBlock MakeSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Foreground = GetBrush("TextPrimaryBrush"),
    };

    private Border WrapSection(Control content) => new Border
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        Background = GetBrush("SurfaceBaseBrush"),
        BorderBrush = GetBrush("BorderSubtleBrush"),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    /// <summary>
    /// "Radio-card" pattern from desktop NetworkPage — Border with a 24,*
    /// Grid (radio left, title+subtitle stack right). Whole card click
    /// flips the radio.
    /// </summary>
    private Border MakeRadioCard(Avalonia.Controls.RadioButton radio, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(radio, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(radio);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            // Tap anywhere on the card flips the radio (desktop card click
            // semantics). Idempotent: clicking an already-active card
            // is a no-op since IsChecked → true is no change.
            radio.IsChecked = true;
        };
        return card;
    }

    /// <summary>"Checkbox-card" — same shape as MakeRadioCard but for a CheckBox.</summary>
    private Border MakeCheckboxCard(Avalonia.Controls.CheckBox cb, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = 10,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(cb, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(cb);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            cb.IsChecked = !(cb.IsChecked == true);
        };
        return card;
    }

    /// <summary>
    /// 24,* grid with checkbox + bold label + wrap-text hint underneath.
    /// Used in Leak section where labels are short and don't deserve a
    /// full radio-card look.
    /// </summary>
    private StackPanel MakeLabeledCheckboxRow(Avalonia.Controls.CheckBox cb, string label, string hint)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(cb, 0);
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(cb);
        grid.Children.Add(labelText);

        return new StackPanel
        {
            Spacing = 2,
            Children = { grid, hintText }
        };
    }

    /// <summary>
    /// Autostart row: checkbox on top, status badge below indented to align
    /// under the label text. Mirrors desktop NetworkPage 2071-2150 — the
    /// status TextBlock is colored per its tier (Success / Warning / Danger).
    /// </summary>
    private StackPanel MakeAutostartRow(Avalonia.Controls.CheckBox cb, string statusText, string statusBrushKey)
    {
        var status = new TextBlock
        {
            Text = statusText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(22, 0, 0, 0),
            Foreground = GetBrush(statusBrushKey),
        };
        return new StackPanel
        {
            Spacing = 2,
            Children = { cb, status }
        };
    }

}
