#nullable enable
// ═══════════════════════════════════════════════════════════════════════════════
// Stale-persist defect fix (2026-06-15): auto-detected WG/AWG subnets must be
// folded into the TUN route-exclude set at config-generation time WITHOUT being
// merged into the persisted user list (config.yaml route_exclude_address).
//
// Background: StartupPipeline step 4.5 used to MERGE NetworkInterfaceDetector's
// auto-detected subnets into settings.Tun.RouteExcludeAddress additively and
// never prune. Once persisted, a subnet (e.g. 10.9.1.0/24 widened from a /32
// point-to-point) survived forever — even after the WG/AWG adapter was gone or
// the user moved networks — sending that range DIRECT past the VPN, or excluding
// a now-unrelated LAN.
//
// The fix keeps auto-detected subnets in a runtime-only, non-persisted list
// (TunSettings.AutoDetectedExcludeAddress) and computes the EFFECTIVE exclude
// (= persisted user list + freshly auto-detected) via
// GetEffectiveRouteExcludeAddress() only when the config is generated.
//
// This is NOT the reconnect-conflict bug (fixed in commit f7690f5) — it's the
// related stale-persist defect found during that diagnosis.
//
// Mirrors NetworkInterfaceDetectorTests in style: pure transformation surface
// pinned directly; the persistence path exercised through SettingsLoader.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for <see cref="TunSettings.GetEffectiveRouteExcludeAddress"/> and
/// the non-persistence contract of <see cref="TunSettings.AutoDetectedExcludeAddress"/>.
/// </summary>
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

    // ───────────────────────────────────────────────────────────────────────
    // GetEffectiveRouteExcludeAddress — union / dedup / ordering
    // ───────────────────────────────────────────────────────────────────────

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
        // The common WG-coexistence case: user has one manual exclude and the
        // detector found a live WG subnet. Both must be present, user first.
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
        // If the detector reports a subnet the user already listed, it must not
        // appear twice. The user's verbatim entry wins.
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
        // A whitespace-padded auto entry that matches a user entry is a dupe.
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
        // Historical config behaviour: the user's string is emitted into the
        // sing-box config exactly as authored (the old code passed
        // RouteExcludeAddress through untrimmed). Preserve that.
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
        // Freshly-deserialized YAML can leave a collection null. Treat as empty.
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
        // The helper is a pure read — it must never mutate either backing list
        // (mutating RouteExcludeAddress is exactly the bug being fixed).
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

    // ───────────────────────────────────────────────────────────────────────
    // Vanished-adapter semantics at the model level: re-assigning the runtime
    // auto list (what StartupPipeline does fresh every connect) drops a stale
    // entry from the effective set without touching the persisted user list.
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void VanishedAdapter_FreshAssignment_DropsStaleAutoExclude()
    {
        var tun = new TunSettings
        {
            RouteExcludeAddress = new List<string> { "192.168.50.0/24" }
        };

        // Connect #1 — WG adapter present.
        tun.AutoDetectedExcludeAddress = new List<string> { "10.9.1.0/24" };
        Assert.Contains("10.9.1.0/24", tun.GetEffectiveRouteExcludeAddress());

        // Connect #2 — adapter gone; pipeline re-assigns fresh (empty) detection.
        tun.AutoDetectedExcludeAddress = new List<string>();

        var eff = tun.GetEffectiveRouteExcludeAddress();
        Assert.DoesNotContain("10.9.1.0/24", eff);
        Assert.Equal(WithMandatory("192.168.50.0/24"), eff);
        // Persisted user list never affected by either connect.
        Assert.Equal(new[] { "192.168.50.0/24" }, tun.RouteExcludeAddress);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Persistence contract — AutoDetectedExcludeAddress must NOT round-trip
    // through config.yaml; the user-authored RouteExcludeAddress must.
    // ───────────────────────────────────────────────────────────────────────

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

        // The user-authored exclude is persisted...
        Assert.Contains("192.168.50.0/24", yaml);
        // ...but the auto-detected one is NOT written anywhere in the file.
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

        // User-authored excludes survive the YAML round-trip verbatim.
        Assert.Equal(
            new[] { "192.168.50.0/24", "172.16.0.0/12" },
            reloaded.Tun.RouteExcludeAddress);

        // The runtime auto list starts empty after a reload (never persisted),
        // so a stale auto subnet can't resurrect itself across launches.
        Assert.NotNull(reloaded.Tun.AutoDetectedExcludeAddress);
        Assert.Empty(reloaded.Tun.AutoDetectedExcludeAddress);

        // And the effective set on a fresh reload (before any detection) is the
        // user list only.
        Assert.Equal(
            WithMandatory("192.168.50.0/24", "172.16.0.0/12"),
            reloaded.Tun.GetEffectiveRouteExcludeAddress());
    }
}
