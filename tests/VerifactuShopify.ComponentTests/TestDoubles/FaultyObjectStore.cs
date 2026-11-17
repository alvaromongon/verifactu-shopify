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

    // Runs once, just before the next replace-if-unchanged: lets a test change the world between the
    // moment the caller read an ETag and the moment it uses it.
    public Func<Task>? BeforeNextReplace { get; set; }

    public void FailNextConditionalWrite(Fault fault) => _next = fault;

    // Like FailNextConditionalWrite, but waits for the next replace-if-unchanged and lets create-if-absent through.
    public void FailNextReplace(Fault fault) => _nextReplace = fault;

    public Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetAsync(key, cancellationToken);

    public Task<string> PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, cancellationToken);

    public Task<WriteOutcome> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        WithFault(() => inner.PutIfAbsentAsync(key, content, cancellationToken));

    public Task<WriteOutcome> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string eTag, CancellationToken cancellationToken = default) =>
        WithFault(_nextReplace is null ? null : TakeReplaceFault(), async () =>
        {
            var before = BeforeNextReplace;
            BeforeNextReplace = null;
            if (before is not null)
            {
                await before();
            }

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
