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

    /// <summary>
    /// The lock is held and its expiry is further away than a lock can ever be (a clock set ahead, or a
    /// corrupt record). It is not taken and not a normal "busy": somebody has to look at it.
    /// </summary>
    ImpossibleExpiry,
}

/// <summary>The result of trying to take an issuer's lock.</summary>
/// <param name="State">What happened.</param>
/// <param name="Lock">The lock, only when <paramref name="State"/> is <see cref="LockState.Acquired"/>.</param>
/// <param name="HeldUntil">
/// When the holder's lock expires, only when <paramref name="State"/> is <see cref="LockState.Held"/> or
/// <see cref="LockState.ImpossibleExpiry"/>.
/// </param>
/// <param name="Key">The key of the lock object, to tell the person which one to look at.</param>
public sealed record LockAcquisition(LockState State, IssuerLock? Lock = null, DateTimeOffset? HeldUntil = null, string? Key = null);

/// <summary>
/// A conditional write failed and so did the read that checks whether it landed. The write's error is the
/// one that matters, so it is the inner exception; the read's is in <see cref="ReadError"/>. It is an
/// <see cref="InvalidOperationException"/> so the host reports it and exits with 1 instead of crashing.
/// </summary>
public sealed class LockWriteException(string message, Exception writeError, Exception readError) : InvalidOperationException(message, writeError)
{
    /// <summary>The error of the read that checked whether the write landed.</summary>
    public Exception ReadError { get; } = readError;
}

/// <summary>
/// A lock per issuer between sync runs, kept as one object in the bucket and taken with its conditional
/// writes: create-if-absent to acquire, replace-if-unchanged to renew, to take over an expired one and to
/// release. The expiry is inside the object, so a run that dies leaves a lock that frees itself.
/// </summary>
/// <remarks>
/// Losing the lock does not lose records (the AEAT keeps every one), but it is not harmless: the chain head
/// is read from the AEAT, so a run stalled past the time to live (there is no fencing token) can wake up and
/// chain onto a head another run already used, until it renews and finds out. Expiry is judged on the
/// reader's clock plus <see cref="ClockSkewMargin"/>, so the hosts' clocks must be synchronized (NTP). A conditional write that
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

    /// <summary>
    /// How far past its expiry a lock must be before another run takes it, so a clock a little behind
    /// does not make two runs hold it at once. A released lock is exempt (see <see cref="ReleaseAsync"/>).
    /// </summary>
    public static readonly TimeSpan ClockSkewMargin = TimeSpan.FromSeconds(60);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IObjectStore _store;
    readonly string _key;
    readonly TimeSpan _timeToLive;
    readonly TimeProvider _clock;
    readonly string _owner;
    string _eTag;
    DateTimeOffset _writtenAt;
    DateTimeOffset _expiresAt;

    IssuerLock(IObjectStore store, string key, TimeSpan timeToLive, TimeProvider clock, LockRecord record, DateTimeOffset writtenAt, string eTag)
    {
        _store = store;
        _key = key;
        _timeToLive = timeToLive;
        _clock = clock;
        _owner = record.Owner;
        _expiresAt = record.ExpiresAt;
        _writtenAt = writtenAt;
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
        var writtenAt = clock.GetUtcNow();
        var mine = new LockRecord(Guid.NewGuid().ToString("N"), writtenAt + timeToLive);

        var created = await WriteAsync(store, key, mine, eTag: null, cancellationToken);
        if (created.Written)
        {
            return Acquired(store, key, timeToLive, clock, mine, writtenAt, created.ETag!);
        }

        var current = await store.GetAsync(key, cancellationToken);
        if (current is null)
        {
            // It was there when the write was refused and it is gone now, or nobody ever held it.
            return new LockAcquisition(LockState.Contended);
        }

        var holder = Parse(current);
        if (holder is not null)
        {
            var seen = Judge(holder, key, timeToLive, clock);
            if (seen is not null)
            {
                return seen;
            }
        }

        // Expired (or unreadable, which nobody is holding): take it over only if it is still the version just read.
        var takenOver = await WriteAsync(store, key, mine, current.ETag, cancellationToken);
        if (takenOver.Written)
        {
            return Acquired(store, key, timeToLive, clock, mine, writtenAt, takenOver.ETag!);
        }

        // Somebody else took it over first, or the service answered with a conflict: read to tell which.
        var winner = await store.GetAsync(key, cancellationToken);
        var winnerRecord = winner is null ? null : Parse(winner);
        return (winnerRecord is null ? null : Judge(winnerRecord, key, timeToLive, clock))
            ?? new LockAcquisition(LockState.Contended);
    }

    // Held (or impossible) when somebody else's lock has not yet expired by the margin; null when it can be taken.
    static LockAcquisition? Judge(LockRecord holder, string key, TimeSpan timeToLive, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        if (holder.ExpiresAt > now + timeToLive + ClockSkewMargin)
        {
            return new LockAcquisition(LockState.ImpossibleExpiry, HeldUntil: holder.ExpiresAt, Key: key);
        }

        return holder.ExpiresAt + ClockSkewMargin > now
            ? new LockAcquisition(LockState.Held, HeldUntil: holder.ExpiresAt, Key: key)
            : null;
    }

    /// <summary>
    /// Renews the lock only when it is due: half the time to live has passed since the last write, or less
    /// than <paramref name="minimumRemaining"/> is left (before a send, the longest wait for its answer).
    /// Cheap to call at every step. Returns <c>false</c> when the lock is no longer ours.
    /// </summary>
    public async Task<bool> RenewIfDueAsync(TimeSpan minimumRemaining, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var due = now - _writtenAt >= _timeToLive / 2 || _expiresAt - now < minimumRemaining;
        return !due || await RenewAsync(cancellationToken);
    }

    /// <summary>
    /// Pushes the expiry forward. Returns <c>false</c> when the lock is no longer ours (it expired and another
    /// run took it over): the caller must stop sending.
    /// </summary>
    public async Task<bool> RenewAsync(CancellationToken cancellationToken = default)
    {
        var writtenAt = _clock.GetUtcNow();
        var renewed = new LockRecord(_owner, writtenAt + _timeToLive);
        var outcome = await WriteAsync(_store, _key, renewed, _eTag, cancellationToken);
        if (outcome.Written)
        {
            _eTag = outcome.ETag!;
            _writtenAt = writtenAt;
            _expiresAt = renewed.ExpiresAt;
            return true;
        }

        // A refused write is not always a lost lock: a 409 conflict leaves it untouched. Still ours and
        // unchanged means we keep it (the next renewal will extend it).
        var stored = await _store.GetAsync(_key, cancellationToken);
        return stored is not null && stored.ETag == _eTag
            && Parse(stored) is { } record && record.Owner == _owner && record.ExpiresAt > _clock.GetUtcNow();
    }

    /// <summary>
    /// Lets go of the lock by expiring it, only if it is still ours: an unconditional delete could remove the
    /// lock of the run that took it over. The record is written as expired since the beginning of time, so
    /// the next run does not wait for the clock skew margin. A failure here is an error to report, not to hide, but it costs
    /// nothing more than the time left before the lock expires on its own.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var released = new LockRecord(_owner, DateTimeOffset.MinValue);
        var outcome = await WriteAsync(_store, _key, released, _eTag, cancellationToken);
        if (outcome.Written)
        {
            _eTag = outcome.ETag!;
        }
    }

    static LockAcquisition Acquired(IObjectStore store, string key, TimeSpan timeToLive, TimeProvider clock, LockRecord record, DateTimeOffset writtenAt, string eTag) =>
        new(LockState.Acquired, new IssuerLock(store, key, timeToLive, clock, record, writtenAt, eTag));

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
        // The caller's own cancellation is not read back; an HTTP timeout (a cancellation it did not ask for) is.
        catch (Exception writeError) when (!cancellationToken.IsCancellationRequested)
        {
            StoredObject? stored;
            try
            {
                stored = await store.GetAsync(key, cancellationToken);
            }
            catch (Exception readError) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LockWriteException(
                    $"La escritura del cerrojo falló ({writeError.Message}) y tampoco se pudo leer para comprobar si llegó ({readError.Message}).",
                    writeError, readError);
            }

            if (stored is not null && Parse(stored) == record)
            {
                return WriteOutcome.Success(stored.ETag);
            }

            // Another holder's lock is a lost condition. Nothing there, or still the very version the write
            // was conditioned on, means it never landed and the failure is a real one.
            if (stored is not null && stored.ETag != eTag && Parse(stored) is { } other && other.Owner != record.Owner)
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
