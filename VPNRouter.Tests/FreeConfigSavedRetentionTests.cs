using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class FreeConfigSavedRetentionTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status,
        DateTime? lastTestedAt)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = status,
            LastTestedAt = lastTestedAt,
        };
    }

    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Verified_FreshlyTested_Retained()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddHours(-1));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_29Days_Retained()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-29));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_31Days_Dropped()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-31));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_NullLastTested_Retained()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, null);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void NonVerified_Dropped_RegardlessOfAge()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
            Now.AddHours(-1));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Null_Dropped()
    {
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(null!, Now));
    }

    [Fact]
    public void RetentionDays_Const_Is30()
    {
        Assert.Equal(30, VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .SavedListRetentionDays);
    }
}
