using VPNRouter.App;

namespace VPNRouter.Tests;

// r8 #4: Simple-mode input must recognise NaiveProxy share-links (Win/Linux
// runtime; platform-gated at apply time) — previously naive:// fell through to
// "Invalid" and the user got a generic error.
public class SimpleInputDetectorTests
{
    [Theory]
    [InlineData("naive://u:p@h:443#n")]
    [InlineData("naive+https://u:p@h:443#n")]
    [InlineData("naive+quic://u:p@h:443#n")]
    [InlineData("vless://x@h:443#n")]
    [InlineData("hysteria2://pw@h:8444#n")]
    [InlineData("tuic://uuid:pw@h:443#n")]
    [InlineData("ss://x@h:443#n")]
    [InlineData("dns-tunnel://x@h:443#n")]
    // P2 (2026-07-10): AmneziaWG share-links recognised at intake (apply-time
    // gate refuses them on a non-lx core, like naive/dns-tunnel).
    [InlineData("awg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32")]
    [InlineData("amneziawg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32")]
    public void Classify_ServerUriSchemes(string uri)
        => Assert.Equal(SmpInputKind.ServerUri, SimpleInputDetector.Classify(uri));

    [Theory]
    [InlineData("http://example.com/sub")]
    [InlineData("https://example.com/sub")]
    public void Classify_SubscriptionUrl(string uri)
        => Assert.Equal(SmpInputKind.SubscriptionUrl, SimpleInputDetector.Classify(uri));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a link")]
    public void Classify_Invalid(string? input)
        => Assert.Equal(SmpInputKind.Invalid, SimpleInputDetector.Classify(input));
}
