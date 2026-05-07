using System;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 (Android port) — in-memory analog of <see cref="CacheRecovery"/>
/// for storage backends that don't expose their own files (Android
/// <c>SharedPreferences</c>, iOS <c>NSUserDefaults</c>, browser
/// <c>localStorage</c>). Same defense-in-depth idea — never let a corrupt
/// JSON blob silently break a feature — but operates on a string the
/// caller already pulled out of the backend rather than reading a file
/// off disk.
///
/// <para>Responsibilities are intentionally narrow: parse the blob via the
/// caller-supplied deserialiser, optionally run a structural predicate,
/// translate every failure mode into a typed
/// <see cref="StorageBlobReason"/>. Quarantine + notice surfacing are
/// the caller's job (the platform layer knows how to stash the bad
/// payload and how to reach the UI).</para>
///
/// <para>Mirrors <see cref="CacheRecovery"/>'s contract closely so a
/// future test or audit can run the same probes against either path.
/// SR-3 (file caches) + SR-4 (SharedPreferences-backed JSON) end up
/// expressing the same semantics with one shared invariant set.</para>
/// </summary>
public static class StorageBlobRecovery
{
    /// <summary>
    /// Parses <paramref name="blob"/> via <paramref name="deserialize"/>
    /// and applies an optional <paramref name="structuralCheck"/>. Empty
    /// / whitespace input returns <see cref="StorageBlobReason.NotFound"/>
    /// with a null value — caller treats that as a clean "first run"
    /// rather than corruption.
    /// </summary>
    public static BlobLoadResult<T> LoadOrRecover<T>(
        string? blob,
        Func<string, T?> deserialize,
        Predicate<T>? structuralCheck = null)
        where T : class
    {
        if (deserialize is null)
            throw new ArgumentNullException(nameof(deserialize));

        if (string.IsNullOrWhiteSpace(blob))
            return new BlobLoadResult<T>(null, StorageBlobReason.NotFound);

        T? value;
        try
        {
            value = deserialize(blob);
        }
        catch (Exception ex)
        {
            return new BlobLoadResult<T>(null, StorageBlobReason.JsonMalformed, ex.Message);
        }

        if (value is null)
            return new BlobLoadResult<T>(null, StorageBlobReason.JsonMalformed,
                "deserialiser returned null");

        if (structuralCheck is not null && !structuralCheck(value))
            return new BlobLoadResult<T>(null, StorageBlobReason.StructurallyInvalid,
                "structural check failed");

        return new BlobLoadResult<T>(value, StorageBlobReason.Success);
    }
}

/// <summary>Outcome of <see cref="StorageBlobRecovery.LoadOrRecover{T}"/>.</summary>
public sealed record BlobLoadResult<T>(T? Value, StorageBlobReason Reason, string? Detail = null)
    where T : class
{
    /// <summary>True only when <see cref="Reason"/> is Success and Value is set.</summary>
    public bool Loaded => Reason == StorageBlobReason.Success && Value is not null;

    /// <summary>True when the blob was malformed or structurally wrong —
    /// caller should quarantine + reset to defaults.</summary>
    public bool ShouldRecover =>
        Reason is StorageBlobReason.JsonMalformed or StorageBlobReason.StructurallyInvalid;
}

/// <summary>Why a <see cref="StorageBlobRecovery.LoadOrRecover{T}"/> call did or didn't succeed.</summary>
public enum StorageBlobReason
{
    /// <summary>Blob parsed and validated — value is usable.</summary>
    Success = 0,

    /// <summary>Blob was null / empty / whitespace. Treat as first run, not corruption.</summary>
    NotFound = 1,

    /// <summary>Blob present but failed JSON parsing or deserialiser returned null.</summary>
    JsonMalformed = 2,

    /// <summary>Parsed cleanly but failed the caller-supplied structural predicate.</summary>
    StructurallyInvalid = 3,
}
