using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
    public void PlaceholderConfigException_TruncatesValueInMessage()
    {
        var ex = new PlaceholderConfigException("reality.public_key", KnownBadPubkey);
        Assert.Contains("DnT9hIvt…nckU", ex.Message);
        Assert.Equal(KnownBadPubkey, ex.OffendingValue);
        Assert.Equal("reality.public_key", ex.OffendingField);
    }
}
