using System;
using System.IO;
using VPNRouter.Core.Localization;
using Xunit;

namespace VPNRouter.Tests;

[CollectionDefinition("AndroidAccessibilityLocalization", DisableParallelization = true)]
public sealed class AndroidAccessibilityLocalizationCollection
{
}

/// <summary>
/// Source-level guards for the Android TalkBack labels of the 2026-11-20 UX audit
/// (A11Y-01 kebab menu, A11Y-03 unnamed switches). They pin the wiring the controls own;
/// the actual announcement, popup focus order and the popup subtree still need the device
/// TalkBack checklist in plans/phase-android-a11y-labels-2026-11-20.md.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
[Collection("AndroidAccessibilityLocalization")]
public sealed class AndroidAccessibilityLabelsTests
{
    [Fact]
    public void MenuButton_HasLocalizedNameAndDescription()
    {
        Assert.Equal("Menu", Lookup(ru: false, () => Strings.MenuButtonName));
        Assert.Equal("Меню", Lookup(ru: true, () => Strings.MenuButtonName));

        var en = Lookup(ru: false, () => Strings.TipMenuButton);
        var ru = Lookup(ru: true, () => Strings.TipMenuButton);
        Assert.False(string.IsNullOrWhiteSpace(en), "EN menu description is empty");
        Assert.False(string.IsNullOrWhiteSpace(ru), "RU menu description is empty");
        Assert.NotEqual(en, ru);
    }

    [Fact]
    public void KebabButtons_AreNamedAndKeepTheMenuAccessible()
    {
        var menu = Source("VPNRouter.Android", "AndroidApp.KebabMenu.cs");
        Assert.Contains(
            "AutomationProperties.SetName(button, Localization.MenuButtonName)",
            menu,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip.SetTip(button, Localization.TipMenuButton)",
            menu,
            StringComparison.Ordinal);
        Assert.Contains("ApplyMenuButtonAccessibility(_kebabMenuButton)", menu, StringComparison.Ordinal);
        Assert.Contains("ApplyMenuButtonAccessibility(_advKebabMenuBtn)", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessibilityView.Raw", menu, StringComparison.Ordinal);
        Assert.Contains("KebabMenuPopup : Popup", menu, StringComparison.Ordinal);
        Assert.Contains("OnCreateAutomationPeer() => new ControlAutomationPeer(this)", menu, StringComparison.Ordinal);

        var header = Source("VPNRouter.Android", "AndroidApp.axaml.cs");
        Assert.Contains("_kebabPopup = new KebabMenuPopup", header, StringComparison.Ordinal);
        Assert.DoesNotContain("HideSubtreeFromAccessibility(menuPanel)", header, StringComparison.Ordinal);
        const string refresh = "RefreshMenuButtonAccessibility();";
        var languageStart = header.IndexOf("private void ToggleLanguageAndRefresh()", StringComparison.Ordinal);
        Assert.True(languageStart > 0);
        Assert.Contains(refresh, header[..languageStart], StringComparison.Ordinal);
        Assert.Contains(refresh, header[languageStart..], StringComparison.Ordinal);
        var advanced = Source("VPNRouter.Android", "AndroidApp.AdvancedShell.cs");
        Assert.Contains("RefreshMenuButtonAccessibility();", advanced, StringComparison.Ordinal);
    }

    [Fact]
    public void UnnamedSubscriptionLabel_IsLocalized()
    {
        Assert.Equal("Unnamed subscription", Lookup(ru: false, () => Strings.SubscriptionWithoutName));
        Assert.Equal("Подписка без названия", Lookup(ru: true, () => Strings.SubscriptionWithoutName));
    }

    [Fact]
    public void MenuItems_AreNamedFromTheirLocalizedLabels()
    {
        var menu = Source("VPNRouter.Android", "AndroidApp.KebabMenu.cs");
        Assert.Contains("AutomationProperties.SetName(btn, text)", menu, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(_menuAdvancedToggleBtn,", menu, StringComparison.Ordinal);

        var header = Source("VPNRouter.Android", "AndroidApp.axaml.cs");
        Assert.Contains(
            "AutomationProperties.SetName(_menuVersionItem, Localization.SmpMenuAbout)",
            header,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSwitchCards_UseTheVisibleTitleAsTheAccessibleName()
    {
        var ui = Source("VPNRouter.Android", "AndroidApp.UiBindings.cs");
        Assert.Contains(
            "AutomationProperties.SetLabeledBy(radio, titleText)",
            ui,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetLabeledBy(cb, titleText)",
            ui,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppAndSubscriptionSwitches_UseTheVisibleRowTextAsTheAccessibleName()
    {
        Assert.Contains(
            "AutomationProperties.SetLabeledBy(checkbox, label)",
            Source("VPNRouter.Android", "AndroidApp.PerAppFilter.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetLabeledBy(enabledChk, nameText)",
            Source("VPNRouter.Android", "AndroidApp.SubscribePage.cs"),
            StringComparison.Ordinal);
    }

    private static string Lookup(bool ru, Func<string> read)
    {
        var previous = Strings.Lang;
        try
        {
            Strings.Lang = ru ? "ru" : "en";
            return read();
        }
        finally
        {
            Strings.Lang = previous;
        }
    }

    private static string Source(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Missing repository file: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
