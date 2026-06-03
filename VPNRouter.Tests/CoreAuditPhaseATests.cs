using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// v2.40.0-r9 — core-audit Phase A regression coverage for the generated/subscribe
// config-pipeline invariant fixes. The diff-scoped sweeps never probed these; this
// locks the validators that close the latent leak / crash-loop classes the audit found.
// (The dns_mode=direct→vpn-dns leak fix #1 is pinned by ConfigGeneratorTests.
//  DnsRule_DirectMode_RoutedAppGetsVpnDnsRule; RoutingMode canonicalization #3 by the
//  SettingsLoader/AppSettingsSane guards + the broad LeakProtection suite.)
public class CoreAuditPhaseATests
{
    // #5/#7 — a Reality public_key is usable only as a 32-byte x25519 base64url key.
    // Empty / truncated / non-base64url previously passed every Core gate and FATAL'd
    // sing-box ("invalid public_key" / "illegal base64") into a crash-loop.
    [Theory]
    [InlineData("gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", true)]  // real 32-byte key
    [InlineData("vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", true)]  // another real key
    [InlineData("", false)]                                            // empty → FATAL
    [InlineData("pk", false)]                                          // too short
    [InlineData("NOT_A_VALID_KEY!!!", false)]                          // non-base64url ('!')
    [InlineData("AAAA", false)]                                        // valid base64url but 3 bytes ≠ 32
    public void IsValidRealityPublicKey_MatchesSingBoxAcceptance(string pbk, bool expectedValid)
        => Assert.Equal(expectedValid, VlessUriParser.IsValidRealityPublicKey(pbk));

    // #2 — sing-box's hex.Decode PANICS on a Reality short_id > 8 bytes. short_id is
    // optional (empty ok), else must be even-length hex of at most 8 bytes (16 chars).
    [Theory]
    [InlineData("", true)]                       // optional
    [InlineData("ab", true)]                     // 1 byte
    [InlineData("78ca7952", true)]               // 4 bytes
    [InlineData("0123456789abcdef", true)]       // 8 bytes (max)
    [InlineData("0123456789abcdef0123", false)]  // 10 bytes → would PANIC sing-box
    [InlineData("0123456789abcdef01", false)]    // 9 bytes
    [InlineData("xyz", false)]                   // non-hex
    [InlineData("abc", false)]                   // odd length
    public void IsValidRealityShortId_RejectsPanicInducingValues(string sid, bool expectedValid)
        => Assert.Equal(expectedValid, VlessUriParser.IsValidRealityShortId(sid));
}
