using System.Text;

using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;
using VerifactuShopify.Sync;

namespace VerifactuShopify.ComponentTests.Sync;

public sealed class IssuerLockTests
{
    const string Issuer = "B12345678";
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    readonly InMemoryObjectStore _store = new();
    readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    Task<LockAcquisition> AcquireAsync(IObjectStore? store = null, string issuer = Issuer) =>
        IssuerLock.TryAcquireAsync(store ?? _store, issuer, Ttl, _clock, Ct);

    [Fact]
    public async Task A_free_lock_is_acquired_and_stored_under_the_issuer_key()
    {
        var attempt = await AcquireAsync();

        Assert.Equal(LockState.Acquired, attempt.State);
        Assert.NotNull(attempt.Lock);
        Assert.NotNull(await _store.GetAsync(IssuerLock.KeyFor(Issuer), Ct));
    }

    [Fact]
    public async Task A_lock_held_by_another_run_is_reported_with_when_it_expires()
    {
        await AcquireAsync();

        var attempt = await AcquireAsync();

        Assert.Equal(LockState.Held, attempt.State);
        Assert.Null(attempt.Lock);
        Assert.Equal(_clock.GetUtcNow() + Ttl, attempt.HeldUntil);
    }

    [Fact]
    public async Task The_lock_of_another_issuer_is_independent()
    {
        await AcquireAsync();

        var attempt = await AcquireAsync(issuer: "A87654321");

        Assert.Equal(LockState.Acquired, attempt.State);
    }

    [Fact]
    public async Task Simultaneous_runs_for_the_same_issuer_leave_exactly_one_with_the_lock()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => AcquireAsync(), Ct)));

        Assert.Single(attempts, attempt => attempt.State == LockState.Acquired);
        Assert.All(attempts.Where(attempt => attempt.State != LockState.Acquired), attempt => Assert.Equal(LockState.Held, attempt.State));
    }

    [Fact]
    public async Task An_expired_lock_is_taken_over_and_the_previous_holder_finds_out_when_it_renews()
    {
        var first = (await AcquireAsync()).Lock!;
        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin);

        var second = await AcquireAsync();

        Assert.Equal(LockState.Acquired, second.State);
        Assert.False(await first.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_lock_not_yet_expired_is_not_taken_over()
    {
        await AcquireAsync();
        _clock.Advance(Ttl - TimeSpan.FromSeconds(1));

        Assert.Equal(LockState.Held, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task An_expired_lock_is_only_taken_after_the_clock_skew_margin()
    {
        await AcquireAsync();

        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin - TimeSpan.FromSeconds(1));
        Assert.Equal(LockState.Held, (await AcquireAsync()).State);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(LockState.Acquired, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task A_lock_expiring_further_away_than_possible_is_neither_taken_nor_called_held()
    {
        var far = $$"""{"owner":"someone","expiresAt":"{{_clock.GetUtcNow().AddYears(1):O}}"}""";
        await _store.PutAsync(IssuerLock.KeyFor(Issuer), Encoding.UTF8.GetBytes(far), Ct);

        var attempt = await AcquireAsync();

        Assert.Equal(LockState.ImpossibleExpiry, attempt.State);
        Assert.Null(attempt.Lock);
        Assert.Equal(IssuerLock.KeyFor(Issuer), attempt.Key);
    }

    [Fact]
    public async Task Renewing_only_writes_when_half_the_time_to_live_has_passed()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        store.FailNextReplace(Fault.Failure);

        _clock.Advance(Ttl / 2 - TimeSpan.FromSeconds(1));
        Assert.True(await held.RenewIfDueAsync(TimeSpan.Zero, Ct));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => held.RenewIfDueAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task Renewing_writes_early_when_less_than_the_minimum_remaining_is_left()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        store.FailNextReplace(Fault.Failure);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await held.RenewIfDueAsync(Ttl - TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1), Ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => held.RenewIfDueAsync(Ttl - TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1), Ct));
    }

    [Fact]
    public async Task A_failed_write_whose_read_back_also_fails_reports_the_write_error_as_cause_and_keeps_the_read_one()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(Ttl / 2);
        store.FailNextReplace(Fault.Failure);
        store.FailNextRead();

        var error = await Assert.ThrowsAsync<LockWriteException>(() => held.RenewAsync(Ct));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal("The service is unavailable.", error.InnerException.Message);
        Assert.IsType<HttpRequestException>(error.ReadError);
        Assert.Contains("The service is unavailable.", error.Message, StringComparison.Ordinal);
        // The host (Program.cs) prints and exits with 1 on this type; anything else would crash unhandled.
        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }

    [Fact]
    public async Task Renewing_extends_the_expiry()
    {
        var held = (await AcquireAsync()).Lock!;
        _clock.Advance(Ttl - TimeSpan.FromMinutes(1));

        Assert.True(await held.RenewAsync(Ct));
        _clock.Advance(Ttl - TimeSpan.FromMinutes(1));

        var other = await AcquireAsync();
        Assert.Equal(LockState.Held, other.State);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(1), other.HeldUntil);
    }

    [Fact]
    public async Task A_released_lock_can_be_acquired_straight_away()
    {
        var held = (await AcquireAsync()).Lock!;

        await held.ReleaseAsync(Ct);

        Assert.Equal(LockState.Acquired, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task Releasing_after_losing_the_lock_does_not_free_the_new_holder()
    {
        var first = (await AcquireAsync()).Lock!;
        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin);
        await AcquireAsync();

        await first.ReleaseAsync(Ct);

        Assert.Equal(LockState.Held, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task A_lock_with_unreadable_content_does_not_block_forever()
    {
        await _store.PutAsync(IssuerLock.KeyFor(Issuer), Encoding.UTF8.GetBytes("not json"), Ct);

        Assert.Equal(LockState.Acquired, (await AcquireAsync()).State);
    }

    [Theory]
    [InlineData("B12345678")]
    [InlineData("../chain/head")]
    [InlineData("a/b")]
    [InlineData("")]
    public void The_key_lives_under_its_own_prefix_whatever_the_issuer_says(string issuer)
    {
        var key = IssuerLock.KeyFor(issuer);

        Assert.StartsWith(IssuerLock.KeyPrefix, key, StringComparison.Ordinal);
        Assert.DoesNotContain('/', key[IssuerLock.KeyPrefix.Length..]);
        Assert.DoesNotContain("..", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_issuers_never_share_a_key()
    {
        var keys = new[] { "a/b", "a%2Fb", "a_b", "A/B", "a b" }.Select(IssuerLock.KeyFor).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_lost_response_on_acquiring_is_read_back_and_the_lock_counts_as_won()
    {
        var store = new FaultyObjectStore(_store);
        store.FailNextConditionalWrite(Fault.LostResponse);

        var attempt = await AcquireAsync(store);

        Assert.Equal(LockState.Acquired, attempt.State);
        Assert.Equal(LockState.Held, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task A_failed_write_on_acquiring_is_an_error_not_a_lock_lost_or_won()
    {
        var store = new FaultyObjectStore(_store);
        store.FailNextConditionalWrite(Fault.Failure);

        await Assert.ThrowsAsync<HttpRequestException>(() => AcquireAsync(store));

        Assert.Null(await _store.GetAsync(IssuerLock.KeyFor(Issuer), Ct));
    }

    [Fact]
    public async Task A_lost_response_while_another_run_holds_the_lock_reports_it_held()
    {
        await AcquireAsync();
        var store = new FaultyObjectStore(_store);
        store.FailNextConditionalWrite(Fault.Failure);

        var attempt = await AcquireAsync(store);

        Assert.Equal(LockState.Held, attempt.State);
    }

    [Fact]
    public async Task A_conflict_with_nobody_holding_the_lock_skips_the_run_without_calling_it_held()
    {
        var store = new FaultyObjectStore(_store);
        store.FailNextConditionalWrite(Fault.Conflict);

        var attempt = await AcquireAsync(store);

        Assert.Equal(LockState.Contended, attempt.State);
        Assert.Null(attempt.Lock);
    }

    [Fact]
    public async Task A_lost_response_on_renewing_is_read_back_and_the_lock_counts_as_kept()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.FailNextConditionalWrite(Fault.LostResponse);

        Assert.True(await held.RenewAsync(Ct));

        _clock.Advance(Ttl - TimeSpan.FromSeconds(30));
        Assert.Equal(LockState.Held, (await AcquireAsync()).State);
    }

    [Fact]
    public async Task A_failed_write_on_renewing_is_an_error()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.FailNextConditionalWrite(Fault.Failure);

        await Assert.ThrowsAsync<HttpRequestException>(() => held.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_failed_write_on_taking_over_an_expired_lock_is_an_error_not_a_skip()
    {
        await AcquireAsync();
        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin);
        var store = new FaultyObjectStore(_store);
        store.FailNextReplace(Fault.Failure);

        await Assert.ThrowsAsync<HttpRequestException>(() => AcquireAsync(store));
    }

    [Fact]
    public async Task A_failed_write_on_taking_over_a_released_lock_is_an_error_not_a_skip()
    {
        var first = (await AcquireAsync()).Lock!;
        await first.ReleaseAsync(Ct);
        var store = new FaultyObjectStore(_store);
        store.FailNextReplace(Fault.Failure);

        await Assert.ThrowsAsync<HttpRequestException>(() => AcquireAsync(store));
    }

    [Fact]
    public async Task A_lost_response_on_taking_over_an_expired_lock_is_read_back_and_the_lock_counts_as_won()
    {
        await AcquireAsync();
        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin);
        var store = new FaultyObjectStore(_store);
        store.FailNextReplace(Fault.LostResponse);

        var attempt = await AcquireAsync(store);

        Assert.Equal(LockState.Acquired, attempt.State);
    }

    [Fact]
    public async Task A_conflict_on_renewing_a_lock_that_is_still_ours_keeps_it()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.FailNextReplace(Fault.Conflict);

        Assert.True(await held.RenewAsync(Ct));
        Assert.True(await held.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_conflict_on_renewing_after_the_lock_expired_loses_it_even_if_nobody_took_it_over()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(Ttl);
        store.FailNextReplace(Fault.Conflict);

        Assert.False(await held.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_conflict_on_renewing_a_lock_taken_over_by_another_run_loses_it()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(Ttl + IssuerLock.ClockSkewMargin);
        await AcquireAsync();
        store.FailNextReplace(Fault.Conflict);

        Assert.False(await held.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_timeout_on_renewing_is_read_back_like_any_other_failure()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        _clock.Advance(TimeSpan.FromMinutes(1));
        store.FailNextReplace(Fault.TimeoutAfterWrite);

        Assert.True(await held.RenewAsync(Ct));
    }

    [Fact]
    public async Task A_cancelled_write_is_not_read_back()
    {
        var store = new FaultyObjectStore(_store);
        var held = (await AcquireAsync(store)).Lock!;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        store.FailNextReplace(Fault.CancelledAfterWrite);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held.RenewAsync(cancelled.Token));
    }
}
