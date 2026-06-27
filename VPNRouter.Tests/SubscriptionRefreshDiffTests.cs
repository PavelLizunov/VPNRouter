using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

/// <summary>
/// G3 (2026-06-27): a subscription refresh must reconnect ONLY when the ACTIVE
/// server's identity (host|port|uuid) changed — a rotation of some OTHER server
/// in the pool must not drop the tunnel. These pin the pure decision helpers
/// (<see cref="SubscriptionRefreshDiff.ActiveServerSignature"/>); the
/// RefreshSubscriptionSilentAsync wiring compares before/after signatures and
/// skips the reconnect when they're equal.
/// </summary>
public class SubscriptionRefreshDiffTests
{
    private static VlessServerEntry S(string name, string host, int port, string uuid)
        => new() { Name = name, Server = host, Port = port, Uuid = uuid };

    [Fact]
    public void ActiveServerSignature_FindsActiveByName()
    {
        var servers = new[] { S("DE", "1.1.1.1", 443, "u1"), S("IS", "2.2.2.2", 443, "u2") };
        Assert.Equal(
            SubscriptionRefreshDiff.SignatureOf("2.2.2.2", 443, "u2"),
            SubscriptionRefreshDiff.ActiveServerSignature(servers, "IS"));
    }

    [Fact]
    public void OtherServerRotated_ActiveSignatureUnchanged_NoReconnect()
    {
        var before = new[] { S("DE", "1.1.1.1", 443, "u1"), S("IS", "2.2.2.2", 443, "u2") };
        // DE rotated its UUID; the active server IS is untouched.
        var after = new[] { S("DE", "1.1.1.1", 443, "u1-rotated"), S("IS", "2.2.2.2", 443, "u2") };

        var sigBefore = SubscriptionRefreshDiff.ActiveServerSignature(before, "IS");
        var sigAfter = SubscriptionRefreshDiff.ActiveServerSignature(after, "IS");

        Assert.Equal(sigBefore, sigAfter); // unchanged -> G3 skips the reconnect
    }

    [Fact]
    public void ActiveServerUuidRotated_SignatureDiffers_Reconnect()
    {
        var before = new[] { S("DE", "1.1.1.1", 443, "u1") };
        var after = new[] { S("DE", "1.1.1.1", 443, "u1-new") };

        Assert.NotEqual(
            SubscriptionRefreshDiff.ActiveServerSignature(before, "DE"),
            SubscriptionRefreshDiff.ActiveServerSignature(after, "DE"));
    }

    [Fact]
    public void ActiveServerHostOrPortChange_SignatureDiffers()
    {
        var sb = SubscriptionRefreshDiff.ActiveServerSignature(new[] { S("DE", "1.1.1.1", 443, "u1") }, "DE");
        Assert.NotEqual(sb, SubscriptionRefreshDiff.ActiveServerSignature(new[] { S("DE", "9.9.9.9", 443, "u1") }, "DE"));
        Assert.NotEqual(sb, SubscriptionRefreshDiff.ActiveServerSignature(new[] { S("DE", "1.1.1.1", 8443, "u1") }, "DE"));
    }

    [Fact]
    public void ActiveServerRemoved_SignatureNull_TreatedAsChanged()
    {
        // Active "IS" no longer in the refreshed set -> null != before -> reconnect.
        var after = new[] { S("DE", "1.1.1.1", 443, "u1") };
        Assert.Null(SubscriptionRefreshDiff.ActiveServerSignature(after, "IS"));
    }

    [Fact]
    public void NullServers_ReturnsNull()
    {
        Assert.Null(SubscriptionRefreshDiff.ActiveServerSignature(null, "DE"));
    }
}
