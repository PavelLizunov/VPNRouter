#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class TunSettingsEffectiveExcludeTests : IDisposable
{
    private readonly string _tempDir;

    private static string[] WithMandatory(params string[] first)
    {
        var r = new List<string>(first);
        var seen = new HashSet<string>(first.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var s in TunSettings.MandatoryLocalRouteExcludeAddress)
        {
            if (seen.Add(s))
                r.Add(s);
        }
        return r.ToArray();
    }

    private static string[] WithMandatoryAndAuto(string auto, params string[] first)
    {
        var r = WithMandatory(first).ToList();
        if (!r.Any(s => s.Trim().Equals(auto, StringComparison.OrdinalIgnoreCase)))
            r.Add(auto);
        return r.ToArray();
    }

    public TunSettingsEffectiveExcludeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.TunEffExclude." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Effective_NoAutoDetected_ReturnsUserListVerbatim()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24", "10.0.0.0/8" },
            AutoDetectedExcludeAddress = new List<string>()
        };

        var eff = tun.GetEffectiveRouteExcludeAddress();

        Assert.Equal(WithMandatory("192.168.50.0/24", "10.0.0.0/8"), eff);
    }

    [Fact]
    public void Effective_AutoDetectedPresent_UnionsBoth_UserFirst()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24" },
            AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
        };

        var eff = tun.GetEffectiveRouteExcludeAddress();

        Assert.Equal(WithMandatoryAndAuto("10.9.1.0/24", "192.168.50.0/24"), eff);
    }

    [Fact]
    public void Effective_EmptyUserList_ReturnsAutoOnly()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string>(),
            AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
        };

        Assert.Equal(WithMandatoryAndAuto("10.9.1.0/24"), tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_BothEmpty_ReturnsEmpty()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string>(),
            AutoDetectedExcludeAddress = new List<string>()
        };

        Assert.Equal(TunSettings.MandatoryLocalRouteExcludeAddress, tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_AutoOverlapsUser_NoDuplicate_UserVerbatimWins()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "10.9.1.0/24" },
            AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
        };

        var eff = tun.GetEffectiveRouteExcludeAddress();

        Assert.Equal(WithMandatory("10.9.1.0/24"), eff);
        Assert.Equal("10.9.1.0/24", eff[0]);
    }

    [Fact]
    public void Effective_Dedup_IgnoresWhitespaceVariants()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "10.9.1.0/24" },
            AutoDetectedExcludeAddress = new List<string> { "  10.9.1.0/24  " }
        };

        Assert.Equal(WithMandatory("10.9.1.0/24"), tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_PreservesUserEntryVerbatim()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "  10.0.0.0/8  " },
            AutoDetectedExcludeAddress = new List<string>()
        };

        var eff = tun.GetEffectiveRouteExcludeAddress();

        Assert.Equal(WithMandatory("  10.0.0.0/8  "), eff);
        Assert.Equal("  10.0.0.0/8  ", eff[0]);
    }

    [Fact]
    public void Effective_SkipsNullAndWhitespaceEntries()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "", "   ", null!, "10.0.0.0/8" },
            AutoDetectedExcludeAddress = new List<string> { "  ", null! }
        };

        Assert.Equal(WithMandatory("10.0.0.0/8"), tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_NullUserList_DoesNotThrow()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = null!,
            AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
        };

        Assert.Equal(WithMandatoryAndAuto("10.9.1.0/24"), tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_NullAutoList_DoesNotThrow()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24" },
            AutoDetectedExcludeAddress = null!
        };

        Assert.Equal(WithMandatory("192.168.50.0/24"), tun.GetEffectiveRouteExcludeAddress());
    }

    [Fact]
    public void Effective_DoesNotMutateSourceLists()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24" },
            AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
        };

        _ = tun.GetEffectiveRouteExcludeAddress();
        _ = tun.GetEffectiveRouteExcludeAddress();

        Assert.Equal(new[] { "192.168.50.0/24" }, tun.RouteExcludeAddress);
        Assert.Equal(new[] { "10.9.1.0/24" }, tun.AutoDetectedExcludeAddress);
    }

    [Fact]
    public void VanishedAdapter_FreshAssignment_DropsStaleAutoExclude()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24" }
        };

        tun.AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" };
        Assert.Contains("10.9.1.0/24", tun.GetEffectiveRouteExcludeAddress());

        tun.AutoDetectedExcludeAddress = new List<string>();

        var eff = tun.GetEffectiveRouteExcludeAddress();
        Assert.DoesNotContain("10.9.1.0/24", eff);
        Assert.Equal(WithMandatory("192.168.50.0/24"), eff);
        Assert.Equal(new[] { "192.168.50.0/24" }, tun.RouteExcludeAddress);
    }

    [Fact]
    public void AutoDetectedExcludeAddress_IsNotSerializedToYaml()
    {
        var settings = new AppSettings
        {
            Tun = new TunSettings
            {
                RouteExcludeAddress = new List<string> { "192.168.50.0/24" },
                AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
            }
        };

        var path = Path.Combine(_tempDir, "config.yaml");
        SettingsLoader.Save(settings, path);
        var yaml = File.ReadAllText(path);

        Assert.Contains("192.168.50.0/24", yaml);
        Assert.DoesNotContain("10.9.1.0/24", yaml);
        Assert.DoesNotContain("AutoDetectedExcludeAddress", yaml);
        Assert.DoesNotContain("auto_detected", yaml);
    }

    [Fact]
    public void Reload_PreservesUserExcludes_AndLeavesAutoListEmpty()
    {
        var original = new AppSettings
        {
            Tun = new TunSettings
            {
                RouteExcludeAddress = new List<string> { "192.168.50.0/24", "172.16.0.0/12" },
                AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" }
            }
        };

        var path = Path.Combine(_tempDir, "config.yaml");
        SettingsLoader.Save(original, path);
        var reloaded = SettingsLoader.Parse(File.ReadAllText(path));

        Assert.Equal(
            new[] { "192.168.50.0/24", "172.16.0.0/12" },
            reloaded.Tun.RouteExcludeAddress);

        Assert.NotNull(reloaded.Tun.AutoDetectedExcludeAddress);
        Assert.Empty(reloaded.Tun.AutoDetectedExcludeAddress);

        Assert.Equal(
            WithMandatory("192.168.50.0/24", "172.16.0.0/12"),
            reloaded.Tun.GetEffectiveRouteExcludeAddress());
    }
}
