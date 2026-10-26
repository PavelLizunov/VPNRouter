using System;
using System.Buffers.Binary;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class PackagedRuntimePolicyTests
{
    [Theory]
    [InlineData(0, 0x81ED, false, true)] // root-owned regular 0755
    [InlineData(0, 0x41ED, true, true)]  // root-owned directory 0755
    [InlineData(1000, 0x81ED, false, false)]
    [InlineData(1000, 0x41ED, true, false)]
    [InlineData(0, 0x81FD, false, false)] // group writable
    [InlineData(0, 0x81EF, false, false)] // other writable
    [InlineData(0, 0x41FD, true, false)]
    [InlineData(0, 0x41EF, true, false)]
    [InlineData(0, 0xA1FF, false, false)] // symlink
    [InlineData(0, 0xA1FF, true, false)]
    [InlineData(0, 0x11ED, false, false)] // FIFO
    public void PackageMetadata_RequiresRootOwnedNonWritableRegularFileOrDirectory(
        int uid, int mode, bool directory, bool expected)
        => Assert.Equal(expected, SingBoxRuntimePolicy.IsTrustedPackageMetadata(0x7FF, (uint)uid, (uint)mode, directory));

    [Fact]
    public void MissingStatFields_AreRejected()
        => Assert.False(SingBoxRuntimePolicy.IsTrustedPackageMetadata(1, 0, 0x81ED, false));

    [Theory]
    [InlineData(0x02000001u, 1u << 12, 20, true)]
    [InlineData(0x03000001u, 1u << 12, 24, true)]
    [InlineData(0x02000001u, (1u << 12) | (1u << 13), 20, true)] // NET_RAW optional
    [InlineData(0x02000000u, 1u << 12, 20, false)] // permitted but not effective
    [InlineData(0x02000001u, 1u << 13, 20, false)] // NET_ADMIN absent
    [InlineData(0x02000001u, 1u << 10, 20, false)] // BIND_SERVICE insufficient
    [InlineData(0x02000001u, 0u, 20, false)]
    [InlineData(0x01000001u, 1u << 12, 20, false)] // unexpected format
    public void Capabilities_RequireEffectiveAndPermittedNetAdmin(uint magic, uint permitted, int length, bool expected)
    {
        byte[] data = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), permitted);
        Assert.Equal(expected, SingBoxRuntimePolicy.HasEffectiveNetAdmin(data));
    }

    [Fact]
    public void MissingTruncatedOrNamespacedCapabilities_AreRejected()
    {
        Assert.False(SingBoxRuntimePolicy.HasEffectiveNetAdmin(Array.Empty<byte>()));
        Assert.False(SingBoxRuntimePolicy.HasEffectiveNetAdmin(new byte[19]));
        byte[] data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x03000001);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1u << 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1000);
        Assert.False(SingBoxRuntimePolicy.HasEffectiveNetAdmin(data));
    }
}
