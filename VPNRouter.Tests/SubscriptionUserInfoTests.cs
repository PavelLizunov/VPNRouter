using System;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>P2 (2026-06-21) — Subscription-Userinfo header parser + summary.</summary>
public class SubscriptionUserInfoTests
{
    [Fact]
    public void Parse_FullHeader_ReadsAllFields()
    {
        var ui = SubscriptionUserInfo.Parse("upload=455727941; download=6174315146; total=107374182400; expire=1735689600");
        Assert.NotNull(ui);
        Assert.Equal(455727941, ui!.Upload);
        Assert.Equal(6174315146, ui.Download);
        Assert.Equal(107374182400, ui.Total);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), ui.Expire);
        Assert.Equal(455727941 + 6174315146, ui.Used);
        Assert.Equal(107374182400 - (455727941 + 6174315146), ui.RemainingBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage-no-equals")]
    [InlineData("foo=bar; baz=qux")]   // unknown keys, non-numeric
    public void Parse_BlankOrUnparseable_ReturnsNull(string? header)
    {
        Assert.Null(SubscriptionUserInfo.Parse(header));
    }

    [Fact]
    public void Parse_PartialHeader_TolerantOfMissingKeys()
    {
        var ui = SubscriptionUserInfo.Parse("total=1073741824; expire=0");  // expire=0 = unlimited
        Assert.NotNull(ui);
        Assert.Equal(1073741824, ui!.Total);
        Assert.Null(ui.Expire);               // expire=0 ignored
        Assert.Equal(1073741824, ui.RemainingBytes);  // nothing used
    }

    [Fact]
    public void Parse_NoTotal_RemainingNull()
    {
        var ui = SubscriptionUserInfo.Parse("upload=100; download=200");
        Assert.NotNull(ui);
        Assert.Null(ui!.RemainingBytes);   // no total → remaining unknown
        Assert.Equal(300, ui.Used);
    }

    [Fact]
    public void DaysLeft_FloorsAndClampsToZero()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var future = SubscriptionUserInfo.Parse($"expire={1_000_000 + (int)TimeSpan.FromDays(5.9).TotalSeconds}");
        Assert.Equal(5, future!.DaysLeft(now));         // floored
        var past = SubscriptionUserInfo.Parse($"expire={1_000_000 - 100}");
        Assert.Equal(0, past!.DaysLeft(now));           // clamped, never negative
    }

    [Fact]
    public void FormatSummary_NonEmptyWhenHasData()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var ui = SubscriptionUserInfo.Parse("download=6174315146; total=107374182400; expire=" + (1_000_000 + 86400 * 10));
        var s = ui!.FormatSummary(now);
        Assert.False(string.IsNullOrEmpty(s));
        Assert.Contains("GB", s);            // human-readable bytes
    }
}
