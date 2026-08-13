using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.49: the connected-Apply structural fingerprint must describe the same
/// effective Include/Exclude policy that ConfigGenerator emits.
/// </summary>
public sealed class ConfigGeneratorAppRoutingFingerprintTests
{
    [Fact]
    public void ComputeAppRoutingFingerprint_ExplicitIncludeChanges_FingerprintChanges()
    {
        var first = MakeSettings("include", include: ["chrome.exe"]);
        var second = MakeSettings("include", include: ["telegram.exe"]);

        var firstFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["legacy.exe"], first);
        var secondFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["legacy.exe"], second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ComputeAppRoutingFingerprint_ExplicitExcludeChanges_FingerprintChanges()
    {
        var first = MakeSettings("exclude", exclude: ["chrome.exe"]);
        var second = MakeSettings("exclude", exclude: ["telegram.exe"]);

        var firstFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["legacy.exe"], first);
        var secondFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["legacy.exe"], second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ComputeAppRoutingFingerprint_SameSetDifferentOrder_FingerprintStable()
    {
        var first = MakeSettings("include", include: ["Telegram.exe", "chrome.exe"]);
        var second = MakeSettings("INCLUDE", include: ["chrome.exe", "Telegram.exe", "CHROME.EXE"]);

        var firstFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint([], first);
        var secondFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint([], second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ComputeAppRoutingFingerprint_CaseOnlyProcessEdit_FingerprintChanges()
    {
        var first = MakeSettings("include", include: ["Chrome.exe"]);
        var second = MakeSettings("include", include: ["chrome.exe"]);

        var firstFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint([], first);
        var secondFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint([], second);

        Assert.NotEqual(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ComputeAppRoutingFingerprint_FullTunnelListChanges_FingerprintStable()
    {
        var first = MakeSettings("include", routingMode: "full", include: ["chrome.exe"]);
        var second = MakeSettings("exclude", routingMode: "FULL", exclude: ["telegram.exe"]);

        var firstFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["legacy.exe"], first);
        var secondFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(["other.exe"], second);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    [Fact]
    public void ResolveEffectiveAppProcesses_EmptyInclude_UsesScannerList()
    {
        var settings = MakeSettings("include");

        var processes = ConfigGenerator.ResolveEffectiveAppProcesses(
            ["chrome.exe", "*.invalid", "CHROME.EXE", "telegram.exe"],
            settings);

        Assert.Equal(["chrome.exe", "telegram.exe"], processes);
    }

    [Fact]
    public void ResolveEffectiveAppProcesses_ExplicitEmptyInclude_StaysEmpty()
    {
        var settings = MakeSettings("include");
        settings.App.RoutingAppsIncludeInitialized = true;

        var processes = ConfigGenerator.ResolveEffectiveAppProcesses(
            ["legacy.exe"], settings);

        Assert.Empty(processes);
    }

    private static AppSettings MakeSettings(
        string appsMode,
        string routingMode = "split",
        string[]? include = null,
        string[]? exclude = null)
    {
        var settings = new AppSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = appsMode;
        settings.App.RoutingAppsInclude = include?.ToList() ?? [];
        settings.App.RoutingAppsExclude = exclude?.ToList() ?? [];
        return settings;
    }
}
