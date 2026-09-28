using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

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
        var after = new[] { S("DE", "1.1.1.1", 443, "u1-rotated"), S("IS", "2.2.2.2", 443, "u2") };

        var sigBefore = SubscriptionRefreshDiff.ActiveServerSignature(before, "IS");
        var sigAfter = SubscriptionRefreshDiff.ActiveServerSignature(after, "IS");

        Assert.Equal(sigBefore, sigAfter);
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
        var after = new[] { S("DE", "1.1.1.1", 443, "u1") };
        Assert.Null(SubscriptionRefreshDiff.ActiveServerSignature(after, "IS"));
    }

    [Fact]
    public void NullServers_ReturnsNull()
    {
        Assert.Null(SubscriptionRefreshDiff.ActiveServerSignature(null, "DE"));
    }

    [Fact]
    public void ActiveServerSignature_FindsByUuid_EvenIfNameChanged()
    {
        var before = new[] { S("DE-Fast", "1.1.1.1", 443, "u1") };
        var after = new[] { S("Germany #1", "1.1.1.1", 443, "u1") };

        var sigBefore = SubscriptionRefreshDiff.ActiveServerSignature(before, "DE-Fast", "u1");
        var sigAfter = SubscriptionRefreshDiff.ActiveServerSignature(after, "DE-Fast", "u1");

        Assert.NotNull(sigAfter);
        Assert.Equal(sigBefore, sigAfter);
    }

    [Fact]
    public void ActiveServerSignature_FallsBackToName_WhenUuidNull()
    {
        var servers = new[] { S("NL", "3.3.3.3", 443, "u3") };
        var sig = SubscriptionRefreshDiff.ActiveServerSignature(servers, "NL", null);

        Assert.Equal(SubscriptionRefreshDiff.SignatureOf("3.3.3.3", 443, "u3"), sig);
    }

    [Fact]
    public void ActiveServerSignature_SharedUserUuid_MatchesByNameNotFirst()
    {
        const string sharedUserUuid = "b09f195d-79e1-4560-9118-875f10b7cb34";
        var servers = new[]
        {
            S("DE-Frankfurt", "1.1.1.1", 443, sharedUserUuid),
            S("IS-Reykjavik", "2.2.2.2", 443, sharedUserUuid),
            S("US-NewYork", "3.3.3.3", 443, sharedUserUuid),
        };

        var sig = SubscriptionRefreshDiff.ActiveServerSignature(servers, "IS-Reykjavik", sharedUserUuid, "2.2.2.2", 443);
        Assert.Equal(SubscriptionRefreshDiff.SignatureOf("2.2.2.2", 443, sharedUserUuid), sig);
    }

    [Fact]
    public void ActiveServerSignature_RenamedServer_MatchesByHostAndPort()
    {
        var before = new[] { S("DE-Fast", "1.1.1.1", 443, "u1") };
        var after = new[] { S("Germany #1 Renamed", "1.1.1.1", 443, "u1") };

        var sigBefore = SubscriptionRefreshDiff.ActiveServerSignature(before, "DE-Fast", "u1", "1.1.1.1", 443);
        var sigAfter = SubscriptionRefreshDiff.ActiveServerSignature(after, "DE-Fast", "u1", "1.1.1.1", 443);

        Assert.NotNull(sigAfter);
        Assert.Equal(sigBefore, sigAfter);
    }
}
