namespace VerifactuShopify.Storage;

/// <summary>An object read from the store, with the ETag that identifies this version of it.</summary>
public sealed record StoredObject(ReadOnlyMemory<byte> Content, string ETag);

/// <summary>
/// Result of a conditional write. A failed condition is an expected outcome (somebody else got there
/// first), not an error: network and credential failures are reported as exceptions instead.
/// </summary>
public readonly record struct WriteOutcome(bool Written, string? ETag)
{
    public static WriteOutcome Success(string eTag) => new(true, eTag);

    public static WriteOutcome ConditionNotMet { get; } = new(false, null);
}

/// <summary>Minimal object storage: what a lock and a synced folder need, the same on every provider.</summary>
public interface IObjectStore
{
    /// <summary>Reads an object, or returns <c>null</c> when it does not exist.</summary>
    Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes an object, replacing any previous one. Returns the new ETag.</summary>
    Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Writes the object only when the key does not exist yet (<c>If-None-Match: *</c>).</summary>
    Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Replaces the object only when its ETag is still <paramref name="eTag"/> (<c>If-Match</c>).</summary>
    Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object. Deleting a missing one is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists the keys that start with <paramref name="prefix"/>, in ordinal order.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default);
}
