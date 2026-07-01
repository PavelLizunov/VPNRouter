using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// PlaceholderGuard (v2.32.3 — Z:\kanareik incident follow-up)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Foundation for the "kill placeholder credentials for every user" project.
// Each test pins one path through PlaceholderGuard so we can't regress when
// adding more fingerprint entries (the lists are intentionally narrow — see
// PlaceholderDefense.cs class-level doc).

public class PlaceholderGuardTests
{
    private const string KnownBadPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string KnownBadShortId = "78ca7952";
    private const string KnownBadServer = "195.135.255.216";

    [Fact]
    public void Inspect_NullEntry_ReturnsNull()
    {
        Assert.Null(PlaceholderDefense.Inspect((VlessServerEntry?)null));
    }

    [Fact]
    public void Inspect_CleanEntry_ReturnsNull()
    {
        // Real-looking subscription server. Must NOT trip the guard — that
        // would be the false-positive ban we're scared of (kills VPN for
        // users with healthy configs).
        var clean = new VlessServerEntry
        {
            Name = "de-01 443",
            Server = "94.131.107.42",
            Port = 443,
            Uuid = "abcd1234-aaaa-bbbb-cccc-ddddeeeeffff",
            Reality = new VlessRealityConfig
            {
                PublicKey = "vJgL_someActuallyValidLookingPubkey_xY9q",
                ShortId = "deadbeef",
            },
        };
        Assert.Null(PlaceholderDefense.Inspect(clean));
    }

    [Fact]
    public void Inspect_PlaceholderPubkey_ReturnsRealityPublicKeyField()
    {
        // Exact reproduction of the Z:\kanareik / stas-class config:
        // host is *alive* (so TcpTlsProbe says ✓), but Reality pubkey is
        // the Android PlaceholderVlessUri leftover.
        var dirty = new VlessServerEntry
        {
            Name = "Demonnot-4",
            Server = "194.87.222.111",
            Port = 443,
            Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
            Reality = new VlessRealityConfig
            {
                PublicKey = KnownBadPubkey,
                ShortId = "deadbeef",
            },
        };
        Assert.Equal("reality.public_key", PlaceholderDefense.Inspect(dirty));
    }

    [Fact]
    public void Inspect_PlaceholderShortId_ReturnsRealityShortIdField()
    {
        var dirty = new VlessServerEntry
        {
            Name = "stas-short-id",
            Server = "94.131.107.42",
            Port = 443,
            Uuid = "abcd-1234",
            Reality = new VlessRealityConfig
            {
                PublicKey = "vJgL_realPubkey",
                ShortId = KnownBadShortId,
            },
        };
        Assert.Equal("reality.short_id", PlaceholderDefense.Inspect(dirty));
    }

    [Fact]
    public void Inspect_PlaceholderServerIp_ReturnsServerField()
    {
        // Pubkey + short_id both clean, only server IP matches. This is
        // the original khunrath_ln stas-evidence case (195.135.255.216).
        var dirty = new VlessServerEntry
        {
            Name = "khunrath_ln",
            Server = KnownBadServer,
            Port = 443,
            Uuid = "abcd",
            Reality = new VlessRealityConfig
            {
                PublicKey = "vJgL_realPubkey",
                ShortId = "deadbeef",
            },
        };
        Assert.Equal("server", PlaceholderDefense.Inspect(dirty));
    }

    [Fact]
    public void Inspect_PubkeyMatchTakesPrecedenceOverShortId()
    {
        // When BOTH pubkey AND short_id match, the helper returns the
        // pubkey field (it's the most diagnostically useful — pubkey is
        // the strongest fingerprint, short_id can collide accidentally).
        var dirty = new VlessServerEntry
        {
            Name = "double-trouble",
            Server = "1.2.3.4",
            Port = 443,
            Uuid = "abcd",
            Reality = new VlessRealityConfig
            {
                PublicKey = KnownBadPubkey,
                ShortId = KnownBadShortId,
            },
        };
        Assert.Equal("reality.public_key", PlaceholderDefense.Inspect(dirty));
    }

    [Fact]
    public void InspectTriField_NullsTreatedAsClean()
    {
        Assert.Null(PlaceholderDefense.Inspect(null, null, null));
    }

    [Fact]
    public void InspectTriField_PlaceholderPubkey_Detected()
    {
        Assert.Equal("reality.public_key",
            PlaceholderDefense.Inspect(KnownBadPubkey, null, null));
    }

    [Fact]
    public void IsPlaceholder_BoolConvenience_Matches()
    {
        Assert.True(PlaceholderDefense.IsPlaceholder(KnownBadPubkey, null, null));
        Assert.False(PlaceholderDefense.IsPlaceholder("vJgL_realPubkey", null, null));
        Assert.False(PlaceholderDefense.IsPlaceholder((VlessServerEntry?)null));
    }

    [Fact]
    public void InspectUri_VlessUriWithPlaceholderPubkey_ReturnsField()
    {
        // Synthesize a vless:// URL with the known-bad pubkey. Reflects
        // the v2.32.3 input-gate path: pasted URL / scanned QR carrying
        // placeholder pbk → guard rejects before persistence.
        var uri = $"vless://352714f4-7ecc-4c22-805f-ed5c5239f5bb@example.com:443" +
                  $"?security=reality&pbk={KnownBadPubkey}&sni=yahoo.com&fp=firefox&type=tcp";
        Assert.Equal("reality.public_key", PlaceholderDefense.InspectUri(uri));
    }

    [Fact]
    public void InspectUri_UnparseableInput_ReturnsNull()
    {
        // Garbage strings shouldn't crash the guard — they fail parsing
        // upstream and surface as a separate "couldn't parse" error to
        // the user. Guard returning null here = "not my problem".
        Assert.Null(PlaceholderDefense.InspectUri("not-a-uri"));
        Assert.Null(PlaceholderDefense.InspectUri(""));
        Assert.Null(PlaceholderDefense.InspectUri(null));
    }

    [Fact]
    public void PlaceholderConfigException_TruncatesValueInMessage()
    {
        var ex = new PlaceholderConfigException("reality.public_key", KnownBadPubkey);
        // First-8 + last-4, with ellipsis in the middle. Full value
        // accessible via OffendingValue for logs.
        Assert.Contains("DnT9hIvt…nckU", ex.Message);
        Assert.Equal(KnownBadPubkey, ex.OffendingValue);
        Assert.Equal("reality.public_key", ex.OffendingField);
    }
}
