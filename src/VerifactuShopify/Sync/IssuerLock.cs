using System.Text.Json;

using VerifactuShopify.Storage;

namespace VerifactuShopify.Sync;

/// <summary>What <see cref="IssuerLock.TryAcquireAsync"/> found.</summary>
public enum LockState
{
    /// <summary>The lock is ours.</summary>
    Acquired,

    /// <summary>Another run holds it and has not let it expire.</summary>
    Held,

    /// <summary>
    /// The write was refused but nobody holds the lock: a concurrent conditional write to the same key
    /// (a 409 <c>ConditionalRequestConflict</c> on S3) is reported like a lost condition. The run is
    /// skipped, and the next one tries again.
    /// </summary>
    Contended,
}

/// <param name="State">What happened.</param>
/// <param name="Lock">The lock, only when <paramref name="State"/> is <see cref="LockState.Acquired"/>.</param>
/// <param name="HeldUntil">When the holder's lock expires, only when <paramref name="State"/> is <see cref="LockState.Held"/>.</param>
public sealed record LockAcquisition(LockState State, IssuerLock? Lock = null, DateTimeOffset? HeldUntil = null);

/// <summary>
/// A lock per issuer between sync runs, kept as one object in the bucket and taken with its conditional
/// writes: create-if-absent to acquire, replace-if-unchanged to renew, to take over an expired one and to
/// release. The expiry is inside the object, so a run that dies leaves a lock that frees itself.
/// </summary>
/// <remarks>
/// Losing the lock loses nothing else: the chain lives in the bucket and the AEAT keeps every record, the
/// worst case is that two runs overlap until the first one renews and finds out. A conditional write that
/// fails with an exception is ambiguous (the implementations never retry it), so the object is read back
/// before deciding whether the write landed.
/// </remarks>
public sealed class IssuerLock
{
    /// <summary>
    /// Every lock key starts with this. No other part of the bucket (the VeriFactu folder, the chain state)
    /// may use it, so a key built from an issuer's data cannot coincide with theirs.
    /// </summary>
    public const string KeyPrefix = "locks/";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IObjectStore _store;
    readonly string _key;
    readonly TimeSpan _timeToLive;
    readonly TimeProvider _clock;
    readonly string _owner;
    string _eTag;

    IssuerLock(IObjectStore store, string key, TimeSpan timeToLive, TimeProvider clock, string owner, string eTag)
    {
        _store = store;
        _key = key;
        _timeToLive = timeToLive;
        _clock = clock;
        _owner = owner;
        _eTag = eTag;
    }

    /// <summary>
    /// The key of an issuer's lock. The issuer is escaped as a single path segment, so whatever it
    /// contains stays under <see cref="KeyPrefix"/> and two different issuers never share a key.
    /// </summary>
    public static string KeyFor(string issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        // EscapeDataString leaves "." alone: a dot-only segment would still mean something to a path.
        return $"{KeyPrefix}{Uri.EscapeDataString(issuer).Replace(".", "%2E", StringComparison.Ordinal)}";
    }

    public static async Task<LockAcquisition> TryAcquireAsync(
        IObjectStore store, string issuer, TimeSpan timeToLive, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        var key = KeyFor(issuer);
        var mine = new LockRecord(Guid.NewGuid().ToString("N"), clock.GetUtcNow() + timeToLive);

        var created = await WriteAsync(store, key, mine, eTag: null, cancellationToken);
        if (created.Written)
        {
            return Acquired(store, key, timeToLive, clock, mine.Owner, created.ETag!);
        }

        var current = await store.GetAsync(key, cancellationToken);
        if (current is null)
        {
            // It was there when the write was refused and it is gone now, or nobody ever held it.
            return new LockAcquisition(LockState.Contended);
        }

        var holder = Parse(current);
        if (holder is not null && holder.ExpiresAt > clock.GetUtcNow())
        {
            return new LockAcquisition(LockState.Held, HeldUntil: holder.ExpiresAt);
        }

        // Expired (or unreadable, which nobody is holding): take it over only if it is still the version just read.
        var takenOver = await WriteAsync(store, key, mine, current.ETag, cancellationToken);
        if (takenOver.Written)
        {
            return Acquired(store, key, timeToLive, clock, mine.Owner, takenOver.ETag!);
        }

        // Somebody else took it over first, or the service answered with a conflict: read to tell which.
        var winner = await store.GetAsync(key, cancellationToken);
        var winnerRecord = winner is null ? null : Parse(winner);
        return winnerRecord is not null && winnerRecord.ExpiresAt > clock.GetUtcNow()
            ? new LockAcquisition(LockState.Held, HeldUntil: winnerRecord.ExpiresAt)
            : new LockAcquisition(LockState.Contended);
    }

    /// <summary>
    /// Pushes the expiry forward. Returns <c>false</c> when the lock is no longer ours (it expired and another
    /// run took it over): the caller must stop sending.
    /// </summary>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken = default)
    {
        var renewed = new LockRecord(_owner, _clock.GetUtcNow() + _timeToLive);
        var outcome = await WriteAsync(_store, _key, renewed, _eTag, cancellationToken);
        if (!outcome.Written)
        {
            return false;
        }

        _eTag = outcome.ETag!;
        return true;
    }

    /// <summary>
    /// Lets go of the lock by expiring it, only if it is still ours: an unconditional delete could remove the
    /// lock of the run that took it over. A failure here is an error to report, not to hide, but it costs
    /// nothing more than the time left before the lock expires on its own.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var released = new LockRecord(_owner, _clock.GetUtcNow());
        var outcome = await WriteAsync(_store, _key, released, _eTag, cancellationToken);
        if (outcome.Written)
        {
            _eTag = outcome.ETag!;
        }
    }

    static LockAcquisition Acquired(IObjectStore store, string key, TimeSpan timeToLive, TimeProvider clock, string owner, string eTag) =>
        new(LockState.Acquired, new IssuerLock(store, key, timeToLive, clock, owner, eTag));

    // Writes the record, creating it when there is no ETag and replacing that version when there is one. A
    // failed write is read back: the record landing means the write won, whatever the response said.
    static async Task<WriteOutcome> WriteAsync(
        IObjectStore store, string key, LockRecord record, string? eTag, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        try
        {
            return eTag is null
                ? await store.PutIfAbsentAsync(key, content, cancellationToken)
                : await store.PutIfUnchangedAsync(key, content, eTag, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var stored = await store.GetAsync(key, cancellationToken);
            if (stored is not null && Parse(stored) == record)
            {
                return WriteOutcome.Success(stored.ETag);
            }

            // Another holder's lock is a lost condition; nothing there (or still the old version) means
            // the write never landed and the failure is a real one.
            if (stored is not null && Parse(stored) is { } other && other.Owner != record.Owner)
            {
                return WriteOutcome.ConditionNotMet;
            }

            throw;
        }
    }

    static LockRecord? Parse(StoredObject stored)
    {
        try
        {
            var record = JsonSerializer.Deserialize<LockRecord>(stored.Content.Span, Json);
            return record is { Owner.Length: > 0 } ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed record LockRecord(string Owner, DateTimeOffset ExpiresAt);
}
