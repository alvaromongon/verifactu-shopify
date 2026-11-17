using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// Same semantics as the S3 implementation, conditions included: the ETag is a content hash.
public sealed class InMemoryObjectStore : IObjectStore
{
    readonly Lock _gate = new();
    readonly SortedDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_objects.GetValueOrDefault(key));
        }
    }

    public Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Store(key, content));
        }
    }

    public Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_objects.ContainsKey(key) ? WriteOutcome.ConditionNotMet : WriteOutcome.Success(Store(key, content)));
        }
    }

    public Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eTag);
        lock (_gate)
        {
            var unchanged = _objects.TryGetValue(key, out var current) && current.ETag == eTag;
            return Task.FromResult(unchanged ? WriteOutcome.Success(Store(key, content)) : WriteOutcome.ConditionNotMet);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _objects.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))]);
        }
    }

    string Store(string key, ReadOnlyMemory<byte> content)
    {
        var eTag = ETags.Of(content.Span);
        _objects[key] = new StoredObject(content.ToArray(), eTag);
        return eTag;
    }
}
