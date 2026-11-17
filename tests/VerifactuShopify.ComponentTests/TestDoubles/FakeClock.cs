namespace VerifactuShopify.ComponentTests.TestDoubles;

// A clock that only moves when the test says so.
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
