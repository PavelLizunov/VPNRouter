using System.Collections.Generic;
using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 (Android self-repair port) — pin every
/// <see cref="StorageBlobRecovery"/> outcome. The Android storage layer
/// (<c>AndroidStorage.GetSubscriptions</c> / <c>GetServers</c> /
/// <c>GetPerAppPackages</c>) routes its SharedPreferences-backed JSON
/// through this helper, so a regression in the helper's classification
/// would silently turn corrupt-payload recoveries back into the silent
/// empty-list fallbacks that motivated the rewrite.
///
/// <para>Test classes deliberately mirror the names + roles of
/// <c>CacheRecoveryTests</c> so a future audit can run the same
/// invariant set against either path (file-backed cache vs string blob).</para>
/// </summary>
public sealed class StorageBlobRecoveryTests
{
    // ── happy path ──────────────────────────────────────────────────────
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

    // ── empty / null blob = first run, not corruption ──────────────────
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
        // NotFound MUST NOT be classified as ShouldRecover — caller would
        // otherwise quarantine a value that doesn't exist + spam the
        // recovery banner on every fresh install.
        Assert.False(r.ShouldRecover);
    }

    // ── malformed JSON ──────────────────────────────────────────────────
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

    // ── deserialiser returns null (e.g. literal "null") ────────────────
    [Fact]
    public void DeserialiserReturnsNull_TreatedAsMalformed()
    {
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            "null", _ => null);

        Assert.Equal(StorageBlobReason.JsonMalformed, r.Reason);
        Assert.Null(r.Value);
        Assert.True(r.ShouldRecover);
    }

    // ── structural check fails ──────────────────────────────────────────
    [Fact]
    public void StructuralCheckFails_QuarantinesAsStructurallyInvalid()
    {
        var blob = JsonSerializer.Serialize(new List<string>());
        // Predicate insists on at least 1 entry — empty list trips it.
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            blob,
            j => JsonSerializer.Deserialize<List<string>>(j),
            v => v.Count >= 1);

        Assert.Equal(StorageBlobReason.StructurallyInvalid, r.Reason);
        Assert.Null(r.Value);
        Assert.True(r.ShouldRecover);
    }

    // ── ShouldRecover semantics ─────────────────────────────────────────
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

    // ── deserialiser throws unchecked exception ─────────────────────────
    [Fact]
    public void DeserialiserThrows_TreatedAsMalformed()
    {
        var r = StorageBlobRecovery.LoadOrRecover<List<string>>(
            "anything", _ => throw new System.InvalidOperationException("boom"));

        Assert.Equal(StorageBlobReason.JsonMalformed, r.Reason);
        Assert.Null(r.Value);
        Assert.Equal("boom", r.Detail);
    }

    // ── Loaded gate: Success + non-null value both required ─────────────
    [Fact]
    public void Loaded_RequiresSuccessAndNonNullValue()
    {
        // Manually-built — Success but null value (defensive: not produced
        // by the real LoadOrRecover code path, but the contract on the
        // record must still hold).
        var r = new BlobLoadResult<List<string>>(null, StorageBlobReason.Success);
        Assert.False(r.Loaded);

        var ok = new BlobLoadResult<List<string>>(new List<string>(), StorageBlobReason.Success);
        Assert.True(ok.Loaded);
    }
}
