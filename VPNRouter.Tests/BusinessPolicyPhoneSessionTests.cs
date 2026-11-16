using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyPhoneSessionTests
{
    [Fact]
    public void CompositeFloor_RejectsRollbackOrEquivocationOfEitherIndependentFeed()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new BusinessPolicySession(cancellation);
        var current = Main(7).WithPhone(Phone(4));
        Assert.True(session.TryAcknowledge(current));
        Assert.True(session.CanApply(Main(7).WithPhone(Phone(4))));
        Assert.True(session.CanApply(Main(8).WithPhone(Phone(4))));
        Assert.True(session.CanApply(Main(7).WithPhone(Phone(5))));
        Assert.False(session.CanApply(Main(6).WithPhone(Phone(5))));
        Assert.False(session.CanApply(Main(8).WithPhone(Phone(3))));
        Assert.False(session.CanApply(Main(8)));
        Assert.False(session.CanApply(Main(7, "other-main").WithPhone(Phone(5))));
        Assert.False(session.CanApply(Main(8).WithPhone(Phone(4, "other-phone"))));
        Assert.True(session.TryAcknowledge(Main(7).WithPhone(Phone(5))));
        Assert.False(session.CanApply(Main(8).WithPhone(Phone(4))));
        Assert.True(session.CanApply(Main(8).WithPhone(Phone(5))));
    }

    [Fact]
    public void FirstAcceptedPhone_AddsAFloorAndEmptyRemainsAuthoritative()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new BusinessPolicySession(cancellation);
        Assert.True(session.TryAcknowledge(Main(7)));
        Assert.True(session.TryAcknowledge(Main(7).WithPhone(Phone(1))));
        Assert.False(session.CanApply(Main(9)));
        Assert.Empty(Main(7).WithPhone(Phone(1)).Phone!.Rules);
        var newSession = new BusinessPolicySession(cancellation);
        Assert.True(newSession.CanApply(Main(1).WithPhone(Phone(1))));
    }

    [Fact]
    public void CompositeDigest_DoesNotReplaceMainDigestOrLoseApplyTiming()
    {
        var main = Main(7);
        var composite = main.WithPhone(Phone(4), applyNowOverride: true);
        Assert.Equal(main.PayloadDigest, composite.PayloadDigest);
        Assert.Equal(main.PayloadDigest, main.EffectiveDigest);
        Assert.NotEqual(main.EffectiveDigest, composite.EffectiveDigest);
        Assert.NotEqual(composite.EffectiveDigest, main.WithPhone(Phone(5)).EffectiveDigest);
        Assert.NotEqual(composite.EffectiveDigest, Main(8).WithPhone(Phone(4)).EffectiveDigest);
        Assert.Equal(composite.EffectiveDigest, main.WithPhone(Phone(4), false).EffectiveDigest);
        Assert.False(main.ApplyNow);
        Assert.True(composite.ApplyNow);
        Assert.False(composite.WithPhone(composite.Phone, false).ApplyNow);
        Assert.Throws<InvalidDataException>(() => main.WithPhone(new BusinessPhonePolicySnapshot(
            "other-policy", "key", 1, 1, false, [], "other")));
    }

    private static BusinessPolicySnapshot Main(long revision, string? digest = null) =>
        new("corp-test", "main-key", revision, 1, false, [], digest ?? $"main-{revision}");

    private static BusinessPhonePolicySnapshot Phone(long revision, string? digest = null) =>
        new("corp-test", "phone-key", revision, 1, false, [], digest ?? $"phone-{revision}");
}
