using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// FreeConfigKeepPolicy — v2.28.5 trim policy used by the FreeConfigsPage VM
// after a search ends. _allConfigs is trimmed to entries that pass this
// predicate so the working set drops back close to baseline within seconds
// of the search completing (instead of holding ~12 MB of dead/unverified
// FreeConfigEntry objects until the next search overwrites the list).
// ═══════════════════════════════════════════════════════════════════════════════

public class FreeConfigKeepPolicyTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = status,
        };
    }

    [Fact]
    public void Verified_Kept()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(entry));
    }

    [Theory]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok)] // v2.28.5-r2: Ok no longer kept (Verified-only)
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Slow)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.ParseError)]
    public void NonVerifiedStatus_Dropped(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status)
    {
        var entry = Make(status);
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(entry));
    }

    [Fact]
    public void Null_Dropped()
    {
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(null!));
    }

    [Fact]
    public void TrimSimulation_DropsToVerifiedOnly()
    {
        // Mimic a realistic post-search _allConfigs: ~25k entries, of which
        // ~10 are Verified, ~200 Ok (TCP+TLS but not deep-verified), the
        // rest dead statuses. v2.28.5-r2: only Verified survive — Ok no
        // longer counted as "keep" because the user wants the displayed
        // list to show only fully-working configs.
        var entries = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        for (int i = 0; i < 10; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified));
        for (int i = 0; i < 200; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed));
        for (int i = 0; i < 9790; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible));

        var trimmed = entries
            .Where(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy.ShouldKeepInLiveCache)
            .ToList();

        Assert.Equal(25000, entries.Count);
        Assert.Equal(10, trimmed.Count);
        Assert.All(trimmed, e => Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, e.Status));
    }
}
