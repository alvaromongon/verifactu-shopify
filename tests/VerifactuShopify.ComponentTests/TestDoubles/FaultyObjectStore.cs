using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.TestDoubles;

/// <summary>What happens to the next conditional write.</summary>
public enum Fault
{
    /// <summary>The write is applied but its response is lost: the caller gets an exception.</summary>
    LostResponse,

    /// <summary>The write is not applied and the caller gets an exception.</summary>
    Failure,

    /// <summary>The write is not applied and the caller gets <see cref="WriteOutcome.ConditionNotMet"/>, as for a 409 conflict.</summary>
    Conflict,

    /// <summary>The write is applied but the call times out: the caller gets a <see cref="TaskCanceledException"/> although nobody cancelled it.</summary>
    TimeoutAfterWrite,

    /// <summary>The write is applied but the caller's own cancellation interrupts the call.</summary>
    CancelledAfterWrite,
}

// Wraps a store and breaks its next conditional write, the way a network or a busy service would.
public sealed class FaultyObjectStore(IObjectStore inner) : IObjectStore
{
    Fault? _next;
    Fault? _nextReplace;
    bool _failNextRead;

    public void FailNextConditionalWrite(Fault fault) => _next = fault;

    // The next read throws, as when the service is down for the check that follows a failed write.
    public void FailNextRead() => _failNextRead = true;

    // Like FailNextConditionalWrite, but waits for the next replace-if-unchanged and lets create-if-absent through.
    public void FailNextReplace(Fault fault) => _nextReplace = fault;

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_failNextRead)
        {
            _failNextRead = false;
            throw new HttpRequestException("The read failed.");
        }

        return inner.GetAsync(key, cancellationToken);
    }

    public Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, cancellationToken);

    public Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        WithFault(() => inner.PutIfAbsentAsync(key, content, cancellationToken));

    public Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default) =>
        WithFault(_nextReplace is null ? null : TakeReplaceFault(), async () =>
        {
            return await inner.PutIfUnchangedAsync(key, content, eTag, cancellationToken);
        });

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(key, cancellationToken);

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        inner.ListAsync(prefix, cancellationToken);

    Fault? TakeReplaceFault()
    {
        var fault = _nextReplace;
        _nextReplace = null;
        return fault;
    }

    Task<WriteOutcome> WithFault(Func<Task<WriteOutcome>> write) => WithFault(null, write);

    async Task<WriteOutcome> WithFault(Fault? replaceFault, Func<Task<WriteOutcome>> write)
    {
        var fault = replaceFault ?? _next;
        if (replaceFault is null)
        {
            _next = null;
        }

        switch (fault)
        {
            case Fault.LostResponse:
                await write();
                throw new HttpRequestException("The response was lost.");
            case Fault.TimeoutAfterWrite:
                await write();
                throw new TaskCanceledException("The request timed out.");
            case Fault.CancelledAfterWrite:
                await write();
                throw new OperationCanceledException();
            case Fault.Failure:
                throw new HttpRequestException("The service is unavailable.");
            case Fault.Conflict:
                return WriteOutcome.ConditionNotMet;
            default:
                return await write();
        }
    }
}
