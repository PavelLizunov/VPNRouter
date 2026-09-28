using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class CoreAuditPhaseATests
{
    [Theory]
    [InlineData("gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", true)]
    [InlineData("vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", true)]
    [InlineData("", false)]
    [InlineData("pk", false)]
    [InlineData("NOT_A_VALID_KEY!!!", false)]
    [InlineData("AAAA", false)]
    public void IsValidRealityPublicKey_MatchesSingBoxAcceptance(string pbk, bool expectedValid)
        => Assert.Equal(expectedValid, VlessUriParser.IsValidRealityPublicKey(pbk));

    [Theory]
    [InlineData("", true)]
    [InlineData("ab", true)]
    [InlineData("78ca7952", true)]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcdef0123", false)]
    [InlineData("0123456789abcdef01", false)]
    [InlineData("xyz", false)]
    [InlineData("abc", false)]
    public void IsValidRealityShortId_RejectsPanicInducingValues(string sid, bool expectedValid)
        => Assert.Equal(expectedValid, VlessUriParser.IsValidRealityShortId(sid));

    [Fact]
    public void IsValidSs2022Key_AcceptsSingleAndMultiKey_RejectsBad()
    {
        var k32 = System.Convert.ToBase64String(new byte[32]);
        var k16 = System.Convert.ToBase64String(new byte[16]);

        Assert.True(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", k32));
        Assert.True(LeakProtection.IsValidSs2022Key("2022-blake3-chacha20-poly1305", k32));
        Assert.True(LeakProtection.IsValidSs2022Key("2022-blake3-aes-128-gcm", k16));
        Assert.True(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", k32 + ":" + k32));

        Assert.False(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", k16));
        Assert.False(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", "not!base64"));
        Assert.False(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", ""));
        Assert.False(LeakProtection.IsValidSs2022Key("2022-blake3-aes-256-gcm", k32 + ":"));
    }
}
