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
}
