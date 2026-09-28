using System;

namespace VPNRouter.Core.Services;

public static class StorageBlobRecovery
{
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

public sealed record BlobLoadResult<T>(T? Value, StorageBlobReason Reason, string? Detail = null)
    where T : class
{
    public bool Loaded => Reason == StorageBlobReason.Success && Value is not null;

    public bool ShouldRecover =>
        Reason is StorageBlobReason.JsonMalformed or StorageBlobReason.StructurallyInvalid;
}

public enum StorageBlobReason
{
    Success = 0,

    NotFound = 1,

    JsonMalformed = 2,

    StructurallyInvalid = 3,
}
