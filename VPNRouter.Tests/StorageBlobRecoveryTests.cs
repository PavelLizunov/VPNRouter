using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class StorageBlobRecoveryTests
{
    [Fact]
    public void HappyPath_ValidJson_LoadsValueAndMarksSuccess()
    {
        var blob = JsonSerializer.Serialize(new List<string> { "a", "b" });

        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            blob, j => JsonSerializer.Deserialize<List<string>>(j));

        Assert.True(r.Loaded);
        Assert.Equal(StorageBlobReason.Success, r.Reason);
        Assert.NotNull(r.Value);
        Assert.Equal(2, r.Value!.Count);
        Assert.False(r.ShouldRecover);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNullBlob_ReturnsNotFound(string? blob)
    {
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            blob, j => JsonSerializer.Deserialize<List<string>>(j));

        Assert.Equal(StorageBlobReason.NotFound, r.Reason);
        Assert.Null(r.Value);
        Assert.False(r.Loaded);
        Assert.False(r.ShouldRecover);
    }

    [Fact]
    public void MalformedJson_ReturnsJsonMalformed()
    {
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            "{not actually json", j => JsonSerializer.Deserialize<List<string>>(j));

        Assert.Equal(StorageBlobReason.JsonMalformed, r.Reason);
        Assert.Null(r.Value);
        Assert.True(r.ShouldRecover);
        Assert.False(string.IsNullOrEmpty(r.Detail), "Detail should carry the parse-error message");
    }

    [Fact]
    public void DeserialiserReturnsNull_TreatedAsMalformed()
    {
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            "null", _ => null);

        Assert.Equal(StorageBlobReason.JsonMalformed, r.Reason);
        Assert.Null(r.Value);
        Assert.True(r.ShouldRecover);
    }

    [Fact]
    public void StructuralCheckFails_QuarantinesAsStructurallyInvalid()
    {
        var blob = JsonSerializer.Serialize(new List<string>());
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            blob,
            j => JsonSerializer.Deserialize<List<string>>(j),
            v => v.Count >= 1);

        Assert.Equal(StorageBlobReason.StructurallyInvalid, r.Reason);
        Assert.Null(r.Value);
        Assert.True(r.ShouldRecover);
    }

    [Theory]
    [InlineData(StorageBlobReason.Success, false)]
    [InlineData(StorageBlobReason.NotFound, false)]
    [InlineData(StorageBlobReason.JsonMalformed, true)]
    [InlineData(StorageBlobReason.StructurallyInvalid, true)]
    public void ShouldRecover_OnlyForCorruption(StorageBlobReason reason, bool expected)
    {
        var r = new BlobLoadResult<List<string>>(null, reason);
        Assert.Equal(expected, r.ShouldRecover);
    }

    [Fact]
    public void Loaded_RequiresSuccessAndNonNullValue()
    {
        var r = new BlobLoadResult<List<string>>(null, StorageBlobReason.Success);
        Assert.False(r.Loaded);

        var ok = new BlobLoadResult<List<string>>(new List<string>(), StorageBlobReason.Success);
        Assert.True(ok.Loaded);
    }
}
